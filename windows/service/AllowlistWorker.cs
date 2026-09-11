using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace CareCare;

internal sealed class AllowlistWorker(ILogger<AllowlistWorker> logger) : BackgroundService
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CareCare");
    private static readonly string StatePath = Path.Combine(Root, "allowlist.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Resolution> cache = new(StringComparer.Ordinal);
    private string[] domains = [];
    private string resolverSignature = "";
    private bool policyDirty;

    private static string Resolvers() => string.Join('\n', NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
        .SelectMany(adapter => adapter.GetIPProperties().DnsAddresses)
        // WFP address matches do not include IPv6 scope identifiers.
        .Select(address => address.ToString().Split('%')[0]).Distinct().Order());

    private void Apply()
    {
        policyDirty = true;
        var resolvers = Resolvers();
        var addresses = domains.Where(cache.ContainsKey).SelectMany(domain => cache[domain].Addresses).Distinct();
        Native.Check(Native.ApplyPolicy(string.Join('\n', addresses), resolvers));
        resolverSignature = resolvers;
        policyDirty = false;
    }

    private async Task Refresh(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var domain in cache.Keys.Where(domain => !domains.Contains(domain) || cache[domain].Expires <= now).ToArray())
            cache.Remove(domain);
        // Revoke removed/expired addresses before performing possibly slow DNS lookups.
        Apply();
        var missing = domains.Where(domain => !cache.ContainsKey(domain)).ToArray();
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, Resolution>();
        await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token },
            (domain, _) =>
            {
                try { results[domain] = Native.Resolve(domain); }
                catch (Exception error) { logger.LogWarning(error, "DNS lookup failed for {Domain}; it remains blocked", domain); }
                return ValueTask.CompletedTask;
            });
        foreach (var (domain, result) in results) cache[domain] = result;
        // A lookup batch can outlive a short TTL. Never install already-expired results.
        foreach (var domain in cache.Keys.Where(domain => cache[domain].Expires <= DateTimeOffset.UtcNow).ToArray()) cache.Remove(domain);
        Apply();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Run(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogCritical(error, "Service failed; persistent policy remains installed");
            Environment.Exit(1);
        }
    }

    private async Task Run(CancellationToken stoppingToken)
    {
        // Install deny policy before reading state or resolving any domain. Persistent
        // rules survive service crashes/stops. Startup never trusts stale saved IPs.
        Apply();
        if (File.Exists(StatePath))
            domains = DomainPolicy.Normalize(JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(StatePath, stoppingToken), Json));
        var config = JsonSerializer.Deserialize<ServiceConfig>(await File.ReadAllTextAsync(Path.Combine(Root, "service.json"), stoppingToken), Json)
            ?? throw new InvalidDataException("Missing service configuration.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(config.ControllerSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));

        // Keep the first instance alive for the service lifetime to prevent pipe-name squatting.
        using var pipe = NamedPipeServerStreamAcl.Create("CareCare.Allowlist.v1", PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            65536, 65536, security);
        await Refresh(stoppingToken);
        using var refreshStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var refreshTask = RefreshLoop(refreshStop.Token);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(150));
                    var request = JsonSerializer.Deserialize<Request>(await ReadRequest(pipe, deadline.Token), Json);
                    if (request?.Version != 1) throw new ArgumentException("Unsupported protocol version.");
                    var updated = DomainPolicy.Normalize(request.Websites);
                    await gate.WaitAsync(deadline.Token);
                    try
                    {
                        // Save desired domains atomically for boot recovery, then apply them.
                        var temporary = StatePath + ".tmp";
                        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                        {
                            await JsonSerializer.SerializeAsync(stream, updated, Json, deadline.Token);
                            stream.Flush(true);
                        }
                        File.Move(temporary, StatePath, true);
                        domains = updated;
                        await Refresh(deadline.Token);
                        var unresolved = domains.Where(domain => !cache.ContainsKey(domain)).ToArray();
                        await Reply(pipe, true, unresolved.Length == 0 ? "Saved and applied by Windows service." :
                            $"Windows policy applied. {unresolved.Length} unresolved domain(s) remain blocked: {string.Join(", ", unresolved.Take(5))}", deadline.Token);
                    }
                    finally { gate.Release(); }
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(error, "Allowlist update failed");
                    try
                    {
                        using var replyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await Reply(pipe, false, "Windows update failed: " + error.Message + ". Retry applying the list.", replyDeadline.Token);
                    }
                    catch (Exception replyError) { logger.LogDebug(replyError, "Client disconnected"); }
                }
                finally { pipe.Disconnect(); }
            }
        }
        finally
        {
            refreshStop.Cancel();
            try { await refreshTask; }
            catch (OperationCanceledException) when (refreshStop.IsCancellationRequested) { }
        }
    }

    private async Task RefreshLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var nextRetry = DateTimeOffset.MinValue;
        while (await timer.WaitForNextTickAsync(token))
        {
            await gate.WaitAsync(token);
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (policyDirty || Resolvers() != resolverSignature || cache.Values.Any(result => result.Expires <= now) ||
                    (domains.Any(domain => !cache.ContainsKey(domain)) && now >= nextRetry))
                {
                    await Refresh(token);
                    nextRetry = DateTimeOffset.UtcNow.AddSeconds(30);
                }
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogCritical(error, "Could not refresh WFP policy; retained policy may be stale");
                // Let SCM recovery restart and install the fail-closed baseline.
                Environment.Exit(1);
            }
            finally { gate.Release(); }
        }
    }

    private static async Task<string> ReadRequest(Stream stream, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        for (;;)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) throw new EndOfStreamException("Incomplete request.");
            var end = Array.IndexOf(buffer, (byte)'\n', 0, count);
            bytes.Write(buffer, 0, end < 0 ? count : end);
            if (bytes.Length > 1048576) throw new InvalidDataException("Request too large.");
            if (end >= 0) return new UTF8Encoding(false, true).GetString(bytes.ToArray());
        }
    }

    private static async Task Reply(Stream stream, bool applied, string message, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { applied, message }, Json) + "\n");
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    private sealed record Request(int Version, string[] Websites);
    private sealed record ServiceConfig(string ControllerSid);
}
