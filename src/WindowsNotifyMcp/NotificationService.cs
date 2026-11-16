using System.Security.Cryptography;
using System.Xml;

namespace WindowsNotifyMcp;

public sealed class NotificationService : IDisposable
{
    public const int Capacity = 256;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    // Tool calls and native activation callbacks share this state. Capacity checks,
    // submission rollback, and first-activation selection must be atomic.
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly INotificationBackend backend;
    private readonly TimeProvider time;
    private readonly TimeSpan retention;
    private int submissionsInFlight;
    private bool disposing;
    private bool disposed;

    public NotificationService(INotificationBackend backend, TimeProvider time, TimeSpan? retention = null)
    {
        this.backend = backend;
        this.time = time;
        this.retention = retention ?? Retention;
        if (this.retention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention), "Retention must be positive.");
        backend.Activated += OnActivated;
    }

    public NotificationSubmission Show(
        string title, string message, NotificationButton[]? buttons = null,
        CancellationToken cancellationToken = default)
    {
        var content = Validate(title, message, buttons);
        var id = Guid.NewGuid().ToString("N");
        // Keep the activation token separate from the ID returned to the MCP client.
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset expiresAt;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposing || disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            RemoveExpired();
            if (entries.Count >= Capacity)
                throw new NotificationException("Notification tracking is full (256 entries). Wait for an entry to expire.");

            expiresAt = time.GetUtcNow() + retention;
            // A fast click may arrive during Show, including a synchronous callback from a backend.
            entries.Add(id, new Entry(token, content.Buttons.Where(b => b.ActionId is not null)
                .Select(b => b.ActionId!).ToHashSet(StringComparer.Ordinal),
                new NotificationResult(id, "pending", expiresAt)));
            submissionsInFlight++;
        }

        var submitted = false;
        try
        {
            // Native registration and delivery can block; readers and callbacks need only
            // the tracking lock, while disposal waits for this reservation to finish.
            backend.Show(content, id, token, cancellationToken);
            submitted = true;
        }
        finally
        {
            lock (gate)
            {
                // Failed native submissions must not occupy the bounded tracking store.
                if (!submitted)
                    entries.Remove(id);
                else
                    entries[id].Submitting = false;
                submissionsInFlight--;
                Monitor.PulseAll(gate);
            }
        }
        return new NotificationSubmission(id, "submitted", expiresAt);
    }

    public NotificationResult GetResult(string notificationId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RemoveExpired();
            if (string.IsNullOrWhiteSpace(notificationId) || !entries.TryGetValue(notificationId, out var entry) ||
                entry.Result.ExpiresAt <= time.GetUtcNow())
                throw new NotificationException("Unknown or expired notification ID.");
            return entry.Result;
        }
    }

    public async Task<NotificationResult> WaitForResultAsync(
        string notificationId, TimeSpan timeout, CancellationToken cancellationToken = default,
        bool waitUntilExpired = false)
    {
        Task<NotificationResult> activation;
        DateTimeOffset expiresAt;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RemoveExpired();
            if (string.IsNullOrWhiteSpace(notificationId) || !entries.TryGetValue(notificationId, out var entry) ||
                entry.Result.ExpiresAt <= time.GetUtcNow())
                throw new NotificationException("Unknown or expired notification ID.");
            if (entry.Result.Status != "pending")
                return entry.Result;
            activation = entry.Activation.Task;
            expiresAt = entry.Result.ExpiresAt;
        }

        var waitForExpiry = waitUntilExpired || timeout >= expiresAt - time.GetUtcNow();
        var waitDuration = GetWaitDuration(timeout, expiresAt, waitForExpiry);
        while (true)
        {
            try
            {
                return await activation.WaitAsync(waitDuration, time, cancellationToken);
            }
            catch (TimeoutException)
            {
                lock (gate)
                {
                    if (!entries.TryGetValue(notificationId, out var entry) ||
                        entry.Result.ExpiresAt <= time.GetUtcNow())
                        return new NotificationResult(notificationId, "expired", expiresAt);
                    if (!waitForExpiry)
                        return entry.Result;
                    waitDuration = GetWaitDuration(timeout, expiresAt, waitForExpiry);
                }
            }
        }
    }

    private TimeSpan GetWaitDuration(TimeSpan timeout, DateTimeOffset expiresAt, bool waitUntilExpired)
    {
        var remaining = expiresAt - time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            return TimeSpan.Zero;
        return waitUntilExpired || remaining < timeout ? remaining : timeout;
    }

    private void OnActivated(NotificationActivation activation)
    {
        lock (gate)
        {
            if (disposed)
                return;
            RemoveExpired();
            // Pipe routing alone does not establish that this notification issued the action.
            // Late or duplicate clicks must not replace the first valid result.
            if (!entries.TryGetValue(activation.NotificationId, out var entry) ||
                entry.Result.ExpiresAt <= time.GetUtcNow() ||
                !string.Equals(entry.Token, activation.Token, StringComparison.Ordinal) ||
                (activation.ActionId is not null && !entry.ActionIds.Contains(activation.ActionId)) ||
                entry.Result.Status != "pending")
                return;
            entry.Result = entry.Result with
            {
                Status = "activated",
                ActionId = activation.ActionId,
                ActivatedAt = time.GetUtcNow()
            };
            entry.Activation.TrySetResult(entry.Result);
        }
    }

    private void RemoveExpired()
    {
        // Opportunistic cleanup bounds memory without a timer; it does not remove Windows toasts.
        var now = time.GetUtcNow();
        // Keep in-flight reservations counted even if native delivery outlives retention.
        foreach (var id in entries.Where(pair => !pair.Value.Submitting && pair.Value.Result.ExpiresAt <= now)
                     .Select(pair => pair.Key).ToArray())
            entries.Remove(id);
    }

    private static NotificationContent Validate(string title, string message, NotificationButton[]? buttons)
    {
        ValidateText(title, "Title", 256);
        ValidateText(message, "Message", 2048);
        // Normalize a private copy so validation never changes the caller's button array.
        buttons = buttons?.ToArray() ?? [];
        if (buttons.Length > 5)
            throw new NotificationException("At most five buttons are supported.");
        var actionIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];
            if (button is null)
                throw new NotificationException("Buttons must not contain null entries.");
            ValidateText(button.Label, "Button label", 64);
            if ((button.Url is null) == (button.ActionId is null))
                throw new NotificationException("Each button must specify exactly one of url or actionId.");
            if (button.Url is not null)
            {
                ValidateText(button.Url, "Button URL", 2048);
                // Windows handles protocol activation. Restrict schemes to web navigation,
                // not local files or app-specific commands; this is not a trusted-site check.
                if (button.Url.Any(char.IsControl) ||
                    !Uri.TryCreate(button.Url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                    string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new NotificationException("Button URLs must be absolute HTTP/HTTPS URLs without credentials.");
                buttons[index] = button with { Url = uri.AbsoluteUri };
            }
            else
            {
                ValidateText(button.ActionId!, "Callback action ID", 128);
                if (!actionIds.Add(button.ActionId!))
                    throw new NotificationException("Callback action IDs must be unique within a notification.");
            }
        }
        return new NotificationContent(title, message, buttons);
    }

    private static void ValidateText(string text, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
            throw new NotificationException($"{name} must contain between 1 and {maxLength} characters.");
        try
        {
            XmlConvert.VerifyXmlChars(text);
        }
        catch (XmlException ex)
        {
            throw new NotificationException($"{name} contains invalid XML characters.", ex);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            while (disposing && !disposed)
                Monitor.Wait(gate);
            if (disposed)
                return;
            disposing = true;
            // Monitor.Wait releases the lock so native callbacks and submission cleanup
            // can complete before handlers are detached and tracking is cleared.
            while (submissionsInFlight > 0)
                Monitor.Wait(gate);
            disposed = true;
            backend.Activated -= OnActivated;
            foreach (var entry in entries.Values)
                entry.Activation.TrySetCanceled();
            entries.Clear();
            Monitor.PulseAll(gate);
        }
    }

    private sealed class Entry(string token, HashSet<string> actionIds, NotificationResult result)
    {
        public string Token { get; } = token;
        public HashSet<string> ActionIds { get; } = actionIds;
        public NotificationResult Result { get; set; } = result;
        public TaskCompletionSource<NotificationResult> Activation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Submitting { get; set; } = true;
    }
}
