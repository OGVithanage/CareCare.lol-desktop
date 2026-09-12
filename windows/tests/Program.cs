using CareCare;
using System.Net;
using System.Net.Sockets;
using System.Text;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action)
{
    try { action(); }
    catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException or System.Text.Json.JsonException) { return; }
    throw new Exception("Invalid policy accepted.");
}
static Allowlist Policy(params WebsiteRule[] rules) => DomainPolicy.Normalize(rules);

var migrated = DomainPolicy.Parse("[\"https://Example.com/a\",\"http://www.example.com:8080\",\"bücher.de\",\"https://example.com.\"]", true);
Check(migrated.Websites.SequenceEqual(new[] { new WebsiteRule("example.com", false), new WebsiteRule("xn--bcher-kva.de", false) }), "Migration failed.");
foreach (var invalid in new[] { "", "localhost", "127.0.0.1", "[::1]", "*.example.com", "https://user:pass@example.com", "file:///test", "-bad.example", "a_b.example", "example.com..", new string('a', 64) + ".com" })
    Reject(() => Policy(new WebsiteRule(invalid, false)));
Reject(() => Policy(Enumerable.Repeat(new WebsiteRule("example.com", false), 501).ToArray()));
Check(Policy(Enumerable.Range(0, 500).Select(i => new WebsiteRule($"site{i}.example.com", true)).ToArray()).Websites.Length == 500, "500-rule limit failed.");
foreach (var json in new[] { "{}", "[]", "null", "{\"version\":1,\"websites\":[]}", "{\"version\":3,\"websites\":[]}",
    "{\"version\":2,\"websites\":[{\"domain\":\"example.com\"}]}", "{\"version\":2,\"websites\":[{\"domain\":\"example.com\",\"allowSubdomains\":\"false\"}]}" })
    Reject(() => DomainPolicy.Parse(json));
foreach (var enabled in new[] { false, true })
{
    var policy = Policy(new WebsiteRule("google.com", enabled));
    foreach (var host in new[] { "google.com", "www.google.com", "WWW.Google.COM." }) Check(DomainPolicy.Allows(policy, host), host);
    foreach (var host in new[] { "mail.google.com", "a.b.google.com" }) Check(DomainPolicy.Allows(policy, host) == enabled, host);
    foreach (var host in new[] { "notgoogle.com", "google.com.other.com" }) Check(!DomainPolicy.Allows(policy, host), host);
}
var child = Policy(new WebsiteRule("mail.google.com", true));
Check(DomainPolicy.Allows(child, "a.mail.google.com") && !DomainPolicy.Allows(child, "google.com") && !DomainPolicy.Allows(child, "other.google.com"), "Child scope widened.");
Check(DomainPolicy.Allows(Policy(new WebsiteRule("bücher.de", true)), "A.BÜCHER.DE."), "IDN matching failed.");
Check(Policy(new WebsiteRule("example.com", true), new("WWW.EXAMPLE.COM", false)).Websites.Single().AllowSubdomains == false, "Duplicate update failed.");

var directory = Path.Combine(Path.GetTempPath(), "carecare-policy-tests-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
try
{
    var path = Path.Combine(directory, "allowlist.json");
    await File.WriteAllTextAsync(path, "[\"www.example.com\",\"http://example.com\"]");
    var loaded = await PolicyState.Load(path, default);
    Check(loaded.Websites.Single() == new WebsiteRule("example.com", false), "State migration failed.");
    Check((await PolicyState.Load(path, default)).Websites.SequenceEqual(loaded.Websites), "Repeat migration failed.");
    await File.WriteAllTextAsync(path, "{\"version\":3,\"websites\":[]}");
    try { await PolicyState.Load(path, default); throw new Exception("Corrupt state accepted."); }
    catch (ArgumentException) { }
    Check((await File.ReadAllTextAsync(path)).Contains("\"version\":3"), "Corrupt state overwritten.");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try { await PolicyState.Save(path, loaded, cancelled.Token); throw new Exception("Cancelled write accepted."); }
    catch (OperationCanceledException) { }
    Check((await File.ReadAllTextAsync(path)).Contains("\"version\":3"), "Failed write replaced existing state.");
}
finally { Directory.Delete(directory, true); }
Console.WriteLine("Rule matching, protocol validation and migration checks passed.");

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var token = deadline.Token;
var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
upstreamListener.Start();
var upstreamPort = ((IPEndPoint)upstreamListener.LocalEndpoint).Port;
var decisions = new List<string>();
using var proxy = new FilteringProxy(0, async (host, port, ct) => {
    lock (decisions) decisions.Add(host + ":" + port);
    var socket = new TcpClient();
    await socket.ConnectAsync(IPAddress.Loopback, upstreamPort, ct);
    return socket;
});
proxy.Publish(Policy(new WebsiteRule("example.com", true)));
proxy.Start();
using var runStop = CancellationTokenSource.CreateLinkedTokenSource(token);
var running = proxy.Run(runStop.Token);
async Task<TcpClient> Request(string text)
{
    var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, proxy.BoundPort, token);
    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(text), token);
    return client;
}
async Task<string> Header(TcpClient client)
{
    var bytes = new List<byte>();
    var one = new byte[1];
    while (bytes.Count < 32768)
    {
        if (await client.GetStream().ReadAsync(one, token) == 0) break;
        bytes.Add(one[0]);
        if (Encoding.ASCII.GetString(bytes.ToArray()).EndsWith("\r\n\r\n")) break;
    }
    return Encoding.ASCII.GetString(bytes.ToArray());
}
async Task Closed(TcpClient client)
{
    try { Check(await client.GetStream().ReadAsync(new byte[1], token) == 0, "Revoked connection still open."); }
    catch (IOException) { }
}
try
{
    foreach (var request in new[] {
        "GET http://notexample.com/ HTTP/1.1\r\nHost: notexample.com\r\n\r\n",
        "CONNECT example.com.other.com:443 HTTP/1.1\r\n\r\n" })
    {
        using var denied = await Request(request);
        Check((await Header(denied)).StartsWith("HTTP/1.1 403"), "Expected denial.");
    }
    Check(decisions.Count == 0, "Denied requests reached connector.");
    foreach (var request in new[] {
        "GET http://example.com/ HTTP/1.1\r\nHost: other.com\r\n\r\n",
        "CONNECT example.com:80 HTTP/1.1\r\n\r\n",
        "CONNECT 127.0.0.1:443 HTTP/1.1\r\n\r\n",
        "GET http://example.com/ HTTP/1.1\r\nContent-Length: 1\r\nContent-Length: 2\r\n\r\n",
        "POST http://example.com/ HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n",
        "GET http://example.com/ HTTP/1.1\r\nBad: " + new string('x', 32768) + "\r\n\r\n" })
    {
        using var invalid = await Request(request);
        Check((await Header(invalid)).StartsWith("HTTP/1.1 400"), "Malformed request was accepted.");
    }
    using (var http = await Request("GET http://new.example.com/a?q=1 HTTP/1.1\r\nHost: new.example.com\r\n\r\nGET http://blocked.com/ HTTP/1.1\r\n\r\n"))
    using (var server = await upstreamListener.AcceptTcpClientAsync(token))
    {
        var forwarded = await Header(server);
        Check(forwarded.Contains("GET /a?q=1 HTTP/1.1") && forwarded.Contains("Connection: close"), "HTTP forwarding failed.");
        await server.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
        server.Dispose();
        Check((await Header(http)).StartsWith("HTTP/1.1 200"), "HTTP response failed.");
        await Closed(http);
    }
    Check(decisions.Count == 1, "Pipelined request bypassed policy.");
    using var tunnel = await Request("CONNECT a.b.example.com:443 HTTP/1.1\r\nHost: a.b.example.com:443\r\n\r\n");
    using var remote = await upstreamListener.AcceptTcpClientAsync(token);
    Check((await Header(tunnel)).StartsWith("HTTP/1.1 200"), "CONNECT failed.");
    await tunnel.GetStream().WriteAsync("ping"u8.ToArray(), token);
    var data = new byte[4];
    await remote.GetStream().ReadExactlyAsync(data, token);
    Check(Encoding.ASCII.GetString(data) == "ping", "Tunnel upload failed.");
    proxy.Publish(Policy(new WebsiteRule("example.com", false), new("a.b.example.com", false)));
    await remote.GetStream().WriteAsync("pong"u8.ToArray(), token);
    await tunnel.GetStream().ReadExactlyAsync(data, token);
    Check(Encoding.ASCII.GetString(data) == "pong", "Additive rule did not retain tunnel.");
    proxy.Publish(Policy(new WebsiteRule("example.com", false)));
    await Closed(tunnel);
    await Closed(remote);
    using var revoked = await Request("CONNECT a.b.example.com:443 HTTP/1.1\r\n\r\n");
    Check((await Header(revoked)).StartsWith("HTTP/1.1 403"), "Revocation did not block new connection.");
    // An in-flight HTTP response is also revoked when its rule is removed.
    using (var http = await Request("GET http://example.com/download HTTP/1.1\r\nHost: example.com\r\n\r\n"))
    using (var server = await upstreamListener.AcceptTcpClientAsync(token))
    {
        await Header(server);
        await server.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 1000000\r\n\r\n"u8.ToArray(), token);
        await Header(http);
        proxy.Publish(Policy());
        await Closed(http);
        await Closed(server);
    }
    foreach (var address in new[] { "127.0.0.1", "10.1.2.3", "172.16.0.1", "192.168.0.1", "169.254.169.254", "100.64.0.1", "::1", "::ffff:127.0.0.1", "fe80::1", "fc00::1" })
        Check(!FilteringProxy.PublicAddress(IPAddress.Parse(address)), "Private destination allowed: " + address);
}
finally
{
    runStop.Cancel();
    try { await running; } catch (OperationCanceledException) { }
    upstreamListener.Stop();
}
Console.WriteLine("HTTP/CONNECT, parsing, shared-endpoint isolation, pipeline rejection and live revocation checks passed.");

// Publish a restrictive policy while name resolution/connection is still pending.
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var cancelledConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var pendingProxy = new FilteringProxy(0, async (_, _, ct) => {
    entered.SetResult();
    try { await Task.Delay(Timeout.Infinite, ct); }
    catch (OperationCanceledException) { cancelledConnect.SetResult(); throw; }
    throw new Exception("Unexpected connector completion.");
});
pendingProxy.Publish(Policy(new WebsiteRule("example.com", true)));
pendingProxy.Start();
using var pendingStop = CancellationTokenSource.CreateLinkedTokenSource(token);
var pendingRun = pendingProxy.Run(pendingStop.Token);
try
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, pendingProxy.BoundPort, token);
    await client.GetStream().WriteAsync("CONNECT new.example.com:443 HTTP/1.1\r\n\r\n"u8.ToArray(), token);
    await entered.Task.WaitAsync(token);
    pendingProxy.Publish(Policy());
    await cancelledConnect.Task.WaitAsync(token);
    await Closed(client);
}
finally
{
    pendingStop.Cancel();
    try { await pendingRun; } catch (OperationCanceledException) { }
}
Console.WriteLine("Pending connection revocation checks passed.");
