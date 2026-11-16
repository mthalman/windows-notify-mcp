using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WindowsNotifyMcp;

// Serialize process startup with a plugin update that is swapping the deployment.
using (var updateMutex = new Mutex(false, "Local\\WindowsNotifyMcp-PluginBuild"))
{
    try { updateMutex.WaitOne(); } catch (AbandonedMutexException) { }
    updateMutex.ReleaseMutex();
}

// Windows can launch the executable for a click after the originating MCP process exits.
// That launch forwards activation and exits; it has no MCP client connected to stdin.
if (args.Any(arg => arg.StartsWith("----AppNotificationActivated:", StringComparison.Ordinal)))
{
    await WindowsNotificationBackend.HandleColdActivationAsync();
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
// Stdout carries JSON-RPC messages, so even low-severity diagnostics belong on stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<INotificationBackend, WindowsNotificationBackend>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddMcpServer().WithStdioServerTransport()
    .WithTools<NotificationTools>(serializerOptions: NotificationJsonContext.Default.Options);
await builder.Build().RunAsync();
