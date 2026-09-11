using CareCare;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("CareCare filtering requires Windows.");
if (args.Contains("--remove-policy"))
{
    Native.Check(Native.RemovePolicy());
    return;
}
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "CareCareAllowlist");
builder.Services.AddHostedService<AllowlistWorker>();
await builder.Build().RunAsync();
