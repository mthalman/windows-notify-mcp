using System.Text;
using System.Xml.Linq;

namespace WindowsNotifyMcp;

public static class NotificationPayload
{
    public static string Create(NotificationContent content, string id, string token, string session)
    {
        // XML nodes escape caller-supplied text and attributes without treating them as markup.
        var toast = new XElement("toast",
            new XAttribute("launch", Arguments(id, token, session, null)),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", content.Title), new XElement("text", content.Message))));
        if (content.Buttons.Count > 0)
        {
            // Protocol buttons go directly to Windows' URL handler, bypassing callback tracking.
            toast.Add(new XElement("actions", content.Buttons.Select(button =>
                new XElement("action", new XAttribute("content", button.Label),
                    new XAttribute("activationType", button.Url is null ? "foreground" : "protocol"),
                    new XAttribute("arguments", button.Url ?? Arguments(id, token, session, button.ActionId))))));
        }
        var payload = toast.ToString(SaveOptions.DisableFormatting);
        // Check the serialized UTF-8 size: escaping and activation metadata also use the budget.
        if (Encoding.UTF8.GetByteCount(payload) > 5000)
            throw new NotificationException("The combined toast content exceeds the 5,000-byte payload limit.");
        return payload;
    }

    // Action IDs are caller-defined and can contain the separators used by activation arguments.
    private static string Arguments(string id, string token, string session, string? actionId) =>
        $"session={session}&id={id}&token={token}" +
        (actionId is null ? "" : $"&action={Uri.EscapeDataString(actionId)}");
}
