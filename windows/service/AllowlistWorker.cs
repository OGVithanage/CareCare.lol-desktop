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
    private readonly FilteringProxy proxy = new();

    private static string Resolvers() => string.Join('\n', NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
        .SelectMany(adapter => adapter.GetIPProperties().DnsAddresses)
        .Select(address => address.ToString().Split('%')[0]).Distinct().Order());

    private static void ApplyBoundary() => Native.Check(Native.ApplyPolicy(
        Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path."), Resolvers()));

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
        ApplyBoundary();
        var policy = await PolicyState.Load(StatePath, stoppingToken);
        proxy.Publish(policy);
        var config = JsonSerializer.Deserialize<ServiceConfig>(await File.ReadAllTextAsync(Path.Combine(Root, "service.json"), stoppingToken), Json)
            ?? throw new InvalidDataException("Missing service configuration.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(config.ControllerSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));

        // Keep the first instance alive for the service lifetime to prevent pipe-name squatting.
        using var pipe = NamedPipeServerStreamAcl.Create("CareCare.Allowlist.v2", PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            65536, 65536, security);
        proxy.Start();
        using var runtimeStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var proxyTask = proxy.Run(runtimeStop.Token);
        var boundaryTask = BoundaryLoop(runtimeStop.Token);
        var pipeTask = Serve(pipe, runtimeStop.Token);
        var completed = await Task.WhenAny(proxyTask, boundaryTask, pipeTask);
        runtimeStop.Cancel();
        proxy.Dispose();
        // A failed listener or boundary refresh terminates the process for SCM recovery.
        await completed;
        stoppingToken.ThrowIfCancellationRequested();
        throw new IOException("An enforcement component stopped unexpectedly.");
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(150));
                    // v1 requests are deliberately rejected; no lossy downgrade.
                    var updated = DomainPolicy.Parse(await ReadRequest(pipe, deadline.Token));
                    await PolicyState.Save(StatePath, updated, deadline.Token);
                    try { proxy.Publish(updated); }
                    catch
                    {
                        proxy.Publish(new Allowlist(2, []));
                        throw;
                    }
                    await Reply(pipe, true, "Saved and applied by Windows service (browser proxy).", deadline.Token);
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
        finally { proxy.Dispose(); }
    }

    private static async Task BoundaryLoop(CancellationToken token)
    {
        var signature = Resolvers();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(token))
        {
            var current = Resolvers();
            if (current == signature) continue;
            ApplyBoundary();
            signature = current;
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
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = 2, applied, message }, Json) + "\n");
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    private sealed record ServiceConfig(string ControllerSid);
}
