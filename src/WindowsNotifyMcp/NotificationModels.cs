namespace WindowsNotifyMcp;

public sealed record NotificationButton(string Label, string? Url = null, string? ActionId = null);

/// <summary>
/// Records Windows accepting a toast, not proof that a banner appeared or was read.
/// </summary>
public sealed record NotificationSubmission(string NotificationId, string Status, DateTimeOffset ExpiresAt);

/// <summary>
/// A session-local snapshot. Pending includes dismissed toasts; an activated result with no
/// action ID represents a body click. URL buttons do not update this result.
/// </summary>
public sealed record NotificationResult(
    string NotificationId,
    string Status,
    DateTimeOffset ExpiresAt,
    string? ActionId = null,
    DateTimeOffset? ActivatedAt = null);

public sealed record NotificationContent(string Title, string Message, IReadOnlyList<NotificationButton> Buttons);

public sealed record NotificationActivation(string NotificationId, string Token, string? ActionId);

/// <summary>
/// Submits validated content to the platform. Activation can arrive on another thread
/// or before Show returns; callers must establish tracking before submission.
/// Cancellation can stop queued work, but cannot interrupt a native call already executing.
/// </summary>
public interface INotificationBackend : IDisposable
{
    event Action<NotificationActivation>? Activated;
    void Show(NotificationContent content, string notificationId, string token,
        CancellationToken cancellationToken = default);
}

public sealed class NotificationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
