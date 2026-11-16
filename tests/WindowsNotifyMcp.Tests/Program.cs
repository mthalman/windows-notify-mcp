using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WindowsNotifyMcp;

// The test executable doubles as a stdio fixture so integration tests can use the
// production tools and serializers without registering or displaying Windows toasts.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<INotificationBackend, AutoActivatingBackend>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddMcpServer().WithStdioServerTransport()
    .WithTools<NotificationTools>(serializerOptions: NotificationJsonContext.Default.Options);
await builder.Build().RunAsync();

internal sealed class AutoActivatingBackend : INotificationBackend
{
    public event Action<NotificationActivation>? Activated;
    public void Show(NotificationContent content, string notificationId, string token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Activated?.Invoke(new NotificationActivation(notificationId, token,
            content.Buttons.FirstOrDefault(button => button.ActionId is not null)?.ActionId));
    }
    public void Dispose() { }
}
