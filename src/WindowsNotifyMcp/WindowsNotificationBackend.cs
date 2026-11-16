using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace WindowsNotifyMcp;

public sealed class WindowsNotificationBackend(ILogger<WindowsNotificationBackend> logger) : INotificationBackend
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ActivationRouter? router;
    private AppNotificationManager? manager;
    private bool disposed;

    public event Action<NotificationActivation>? Activated;

    public void Show(NotificationContent content, string notificationId, string token,
        CancellationToken cancellationToken = default) =>
        ExecuteSubmission(() =>
        {
            try
            {
                EnsureRegistered();
                cancellationToken.ThrowIfCancellationRequested();
                router!.ThrowIfFaulted();
                if (manager!.Setting != AppNotificationSetting.Enabled)
                    throw new NotificationException($"Windows notifications are unavailable: {manager.Setting}. Enable notifications for this app in Windows Settings.");
                var notification = new AppNotification(
                    NotificationPayload.Create(content, notificationId, token, router!.Session));
                cancellationToken.ThrowIfCancellationRequested();
                manager.Show(notification);
                if (notification.Id == 0)
                    throw new NotificationException("Windows did not accept the notification.");
            }
            catch (Exception ex) when (ex is COMException or Win32Exception or InvalidOperationException or IOException)
            {
                logger.LogError(ex, "Windows notification submission failed.");
                throw new NotificationException("Windows notification registration or delivery failed. Run the apphost executable as a non-elevated interactive desktop user.", ex);
            }
        }, cancellationToken);

    internal void ExecuteSubmission(Action submit, CancellationToken cancellationToken)
    {
        // Admission is cancelable even while another request is inside a native call.
        // A native operation already executing cannot be interrupted safely.
        gate.Wait(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            submit();
        }
        finally
        {
            gate.Release();
        }
    }

    private void EnsureRegistered()
    {
        // Defer per-user Windows registration until a valid tool call needs native delivery.
        if (manager is not null)
            return;
        using var identity = WindowsIdentity.GetCurrent();
        if (!Environment.UserInteractive ||
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new NotificationException("Notifications require a non-elevated interactive Windows desktop session.");
        if (!AppNotificationManager.IsSupported())
            throw new NotificationException("Windows app notifications are not supported on this system.");

        var newManager = AppNotificationManager.Default;
        // Register can deliver activation, so install the handler and router first.
        newManager.NotificationInvoked += OnInvoked;
        ActivationRouter? newRouter = null;
        try
        {
            newRouter = new ActivationRouter(activation => Activated?.Invoke(activation), logger);
            router = newRouter;
            newManager.Register();
            manager = newManager;
        }
        finally
        {
            if (manager is null)
            {
                newManager.NotificationInvoked -= OnInvoked;
                router = null;
                newRouter?.Dispose();
            }
        }
    }

    // The WinRT event requires a void handler. Handle expected forwarding failures here
    // because the event publisher cannot await this operation.
    private async void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var current = router;
        if (current is null)
            return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await current.RouteAsync(args.Argument, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or NotificationException)
        {
            logger.LogWarning(ex, "Notification activation could not reach its original MCP session.");
        }
    }

    public static Task HandleColdActivationAsync() => HandleColdActivationAsync(
        () => AppNotificationManager.Default.Register(),
        () =>
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind != ExtendedActivationKind.AppNotification ||
                activation.Data is not AppNotificationActivatedEventArgs notification)
                throw new NotificationException("The cold launch did not contain a notification activation.");
            return notification.Argument;
        },
        () => AppNotificationManager.Default.Unregister());

    internal static async Task HandleColdActivationAsync(
        Action register, Func<string> getArguments, Action unregister)
    {
        var registered = false;
        try
        {
            register();
            registered = true;
            // The first cold-launch activation is stored by AppLifecycle, not raised
            // through NotificationInvoked. Register must precede retrieving that payload.
            var arguments = getArguments();
            var parsed = ActivationRouter.Parse(arguments);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ActivationRouter.ForwardAsync(parsed.Session, arguments, timeout.Token);
        }
        catch (Exception ex) when (ex is COMException or IOException or OperationCanceledException or NotificationException)
        {
            Console.Error.WriteLine($"Notification activation failed: {ex.Message}");
        }
        finally
        {
            if (registered)
                unregister();
        }
    }

    public void Dispose()
    {
        ActivationRouter? current;
        gate.Wait();
        try
        {
            if (disposed)
                return;
            disposed = true;
            current = router;
            router = null;
            if (manager is not null)
            {
                manager.NotificationInvoked -= OnInvoked;
                try
                {
                    manager.Unregister();
                }
                catch (COMException ex)
                {
                    logger.LogError(ex, "Windows notification unregistration failed.");
                }
                manager = null;
            }
        }
        finally
        {
            gate.Release();
        }
        // Joining the listener can wait for an activation callback; do not hold the backend lock.
        current?.Dispose();
    }
}
