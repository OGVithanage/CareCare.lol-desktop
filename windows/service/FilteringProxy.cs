using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CareCare;

// One HTTP request per connection (Connection: close); HTTPS is an opaque CONNECT
// tunnel. Never forward a second HTTP request without a new policy decision.
internal sealed class FilteringProxy : IDisposable
{
    internal const int Port = 17843;
    private readonly TcpListener listener;
    private readonly Func<string, int, CancellationToken, Task<TcpClient>> connect;
    private readonly object gate = new();
    private readonly Dictionary<TcpClient, (string Host, CancellationTokenSource Stop)> active = new();
    private readonly SemaphoreSlim slots = new(256);
    private Allowlist policy = new(2, []);

    internal FilteringProxy(int port = Port, Func<string, int, CancellationToken, Task<TcpClient>>? connector = null)
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
        connect = connector ?? Connect;
    }
    internal int BoundPort => ((IPEndPoint)listener.LocalEndpoint).Port;
    internal void Start() => listener.Start(128);

    internal void Publish(Allowlist updated)
    {
        var snapshot = DomainPolicy.Normalize(updated.Websites);
        lock (gate)
        {
            policy = snapshot;
            foreach (var (client, entry) in active)
                if (!DomainPolicy.Allows(snapshot, entry.Host)) { entry.Stop.Cancel(); client.Dispose(); }
        }
    }

    internal async Task Run(CancellationToken token)
    {
        var tasks = new HashSet<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                await slots.WaitAsync(token);
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(token); }
                catch { slots.Release(); throw; }
                foreach (var completed in tasks.Where(task => task.IsCompleted).ToArray())
                { await completed; tasks.Remove(completed); }
                tasks.Add(Handle(client, token));
            }
        }
        finally
        {
            Dispose();
            await Task.WhenAll(tasks);
        }
    }

    private static async Task<TcpClient> Connect(string host, int port, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, token);
        foreach (var address in addresses)
        {
            // Prevent DNS rebinding into local proxies, metadata services or the LAN.
            if (!PublicAddress(address)) continue;
            var client = new TcpClient(address.AddressFamily);
            try { await client.ConnectAsync(address, port, token); return client; }
            catch (SocketException) { client.Dispose(); }
            catch { client.Dispose(); throw; }
        }
        throw new IOException("No reachable public address.");
    }

    internal static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return PublicAddress(address.MapToIPv4());
        var b = address.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xe0) == 0x20 &&
            !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
        return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
            !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    private async Task Handle(TcpClient client, CancellationToken serviceToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        stop.CancelAfter(TimeSpan.FromSeconds(15));
        var token = stop.Token;
        var started = false;
        try
        {
            {
                var downstream = client.GetStream();
                var header = await ReadHeader(downstream, token);
                var lines = header.Split("\r\n", StringSplitOptions.None);
                var request = lines[0].Split(' ');
                if (request.Length != 3 || request[2] != "HTTP/1.1" && request[2] != "HTTP/1.0" ||
                    request[0].Length == 0 || request[0].Any(c => c < 'A' || c > 'Z'))
                    throw new InvalidDataException("Invalid request line.");
                var tunnel = request[0] == "CONNECT";
                if (!Uri.TryCreate(tunnel ? "https://" + request[1] : request[1], UriKind.Absolute, out var uri) ||
                    uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
                    (tunnel ? uri.Scheme != "https" || uri.Port != 443 || uri.PathAndQuery != "/" : uri.Scheme != "http" || uri.Port != 80))
                    throw new InvalidDataException("Only HTTP port 80 and CONNECT port 443 are supported.");
                var host = DomainPolicy.Hostname(uri.IdnHost);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0 || line[..colon].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                        line[(colon + 1)..].Any(c => c == 127 || c < 32 && c != '\t') || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim()))
                        throw new InvalidDataException("Invalid or duplicate header.");
                }
                if (headers.ContainsKey("Transfer-Encoding") || headers.ContainsKey("Upgrade") || headers.ContainsKey("Expect"))
                    throw new InvalidDataException("Chunked uploads, upgrades and Expect are unsupported.");
                long length = 0;
                if (headers.TryGetValue("Content-Length", out var value) &&
                    (!long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length) || length > 64 * 1024 * 1024))
                    throw new InvalidDataException("Invalid or oversized request body.");
                if (tunnel && length != 0) throw new InvalidDataException("CONNECT cannot have a body.");
                if (headers.TryGetValue("Host", out var authority) &&
                    (!Uri.TryCreate(uri.Scheme + "://" + authority, UriKind.Absolute, out var declared) ||
                     declared.UserInfo.Length != 0 || declared.PathAndQuery != "/" || declared.Fragment.Length != 0 ||
                     DomainPolicy.Hostname(declared.IdnHost) != host || declared.Port != uri.Port))
                    throw new InvalidDataException("Host does not match request destination.");
                lock (gate)
                {
                    if (!DomainPolicy.Allows(policy, host)) throw new UnauthorizedAccessException();
                    active.Add(client, (host, stop));
                }
                token.ThrowIfCancellationRequested();
                using var upstream = await connect(host, uri.Port, token);
                using var registration = token.Register(() => upstream.Dispose());
                token.ThrowIfCancellationRequested();
                // Bound even idle tunnels; policy publication cancels them immediately.
                stop.CancelAfter(TimeSpan.FromMinutes(30));
                var remote = upstream.GetStream();
                if (tunnel)
                {
                    await downstream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), token);
                    started = true;
                    var upload = downstream.CopyToAsync(remote, token);
                    var download = remote.CopyToAsync(downstream, token);
                    await Task.WhenAny(upload, download);
                    stop.Cancel();
                    await Task.WhenAll(upload, download);
                }
                else
                {
                    var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                        "Host", "Connection", "Proxy-Connection", "Proxy-Authorization", "Keep-Alive", "TE", "Trailer", "Content-Length"
                    };
                    if (headers.TryGetValue("Connection", out var connection))
                        foreach (var name in connection.Split(',')) excluded.Add(name.Trim());
                    var output = new StringBuilder($"{request[0]} {uri.PathAndQuery} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n");
                    foreach (var (name, headerValue) in headers)
                        if (!excluded.Contains(name)) output.Append($"{name}: {headerValue}\r\n");
                    if (headers.ContainsKey("Content-Length")) output.Append($"Content-Length: {length}\r\n");
                    output.Append("\r\n");
                    await remote.WriteAsync(Encoding.ASCII.GetBytes(output.ToString()), token);
                    var buffer = new byte[16384];
                    while (length > 0)
                    {
                        var count = await downstream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)), token);
                        if (count == 0) throw new EndOfStreamException();
                        await remote.WriteAsync(buffer.AsMemory(0, count), token);
                        length -= count;
                    }
                    started = true;
                    await remote.CopyToAsync(downstream, token);
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or SocketException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (!started && !token.IsCancellationRequested)
            {
                try
                {
                    var code = error is UnauthorizedAccessException ? "403 Forbidden" : "400 Bad Request";
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"), deadline.Token);
                }
                catch (Exception replyError) when (replyError is IOException or InvalidOperationException or SocketException or OperationCanceledException) { }
            }
        }
        finally
        {
            lock (gate) active.Remove(client);
            client.Dispose();
            slots.Release();
        }
    }

    private static async Task<string> ReadHeader(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 32768)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new EndOfStreamException();
            if (one[0] > 127 || one[0] == 0) throw new InvalidDataException("Invalid header encoding.");
            bytes.Add(one[0]);
            var n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("Header too large.");
    }

    public void Dispose()
    {
        listener.Stop();
        lock (gate)
            foreach (var (client, entry) in active) { entry.Stop.Cancel(); client.Dispose(); }
    }
}
