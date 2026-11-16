using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
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

    [McpServerTool(Name = "wait_for_notification_result", UseStructuredContent = true)]
    [Description("Wait for a notification click. Tasks-capable clients wait until activation or notification ID expiry; other clients wait up to timeoutSeconds. Use get_notification_result for a non-blocking check.")]
    public async Task<NotificationResult> WaitForResult(
        RequestContext<CallToolRequestParams> requestContext,
        [Description("The notificationId returned by show_notification.")] string notificationId,
        [Description("Maximum seconds to wait for clients without MCP Tasks, from 1 to 300. Defaults to 60.")] int timeoutSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        var supportsTasks = SupportsTasks(requestContext);
        if (!supportsTasks && (timeoutSeconds is < 1 or > 300))
            throw new McpException("timeoutSeconds must be between 1 and 300.");
        try
        {
            return await notifications.WaitForResultAsync(
                notificationId, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken,
                waitUntilExpired: supportsTasks);
        }
        catch (NotificationException ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    private static bool SupportsTasks(RequestContext<CallToolRequestParams> requestContext) =>
        requestContext.Params.Meta?["io.modelcontextprotocol/clientCapabilities"] is JsonObject clientCapabilities &&
        clientCapabilities["extensions"] is JsonObject extensions &&
        extensions.ContainsKey("io.modelcontextprotocol/tasks");
}
