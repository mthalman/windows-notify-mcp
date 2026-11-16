using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WindowsNotifyMcp;

[McpServerToolType]
public sealed class NotificationTools(NotificationService notifications)
{
    [McpServerTool(Name = "show_notification", UseStructuredContent = true)]
    [Description("Submit a Windows toast. Returns immediately; submission does not guarantee a visible banner. Callback results are session-only and expire after one hour.")]
    public NotificationSubmission Show(
        [Description("Required title, 1-256 characters.")] string title,
        [Description("Required message, 1-2048 characters.")] string message,
        [Description("Up to five buttons. Each needs label (1-64 characters) and exactly one of url (absolute HTTP/HTTPS) or actionId (unique, 1-128 characters). No images.")] NotificationButton[]? buttons = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return notifications.Show(title, message, buttons, cancellationToken);
        }
        catch (NotificationException ex)
        {
            // Preserve actionable validation and delivery messages in the MCP tool error.
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "get_notification_result", ReadOnly = true, UseStructuredContent = true)]
    [Description("Retrieve pending or activated state without consuming it. Activated includes callback actionId (null for a body click). URL buttons have no callback. Unknown/expired IDs are errors; results are lost at shutdown.")]
    public NotificationResult GetResult(
        [Description("The notificationId returned by show_notification.")] string notificationId)
    {
        try
        {
            return notifications.GetResult(notificationId);
        }
        catch (NotificationException ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
