# Windows Notify MCP

A Copilot CLI plugin that shows Windows toast notifications. Notifications can include up to five buttons that open web links or let Copilot check which button you clicked.

## Prerequisites

- Windows x64, with Windows 10 version 2004 (build 19041) or later, or Windows 11.
- An interactive desktop session running as a **non-administrator**. Elevated processes, Windows services, and headless sessions are unsupported.
- PowerShell 7.2 or later.
- The .NET SDK specified by [`global.json`](global.json).
- Copilot CLI.

## Install as a Copilot CLI plugin

Copilot downloads the source and builds the server on first use; you do not need a checkout.

Install it:

```powershell
copilot plugin marketplace add mthalman/windows-notify-mcp
copilot plugin install windows-notify-mcp@windows-notify-mcp
```

Start a new Copilot session after installing. The plugin adds an MCP server named `windows-notify`. Its first build can take a minute; later sessions reuse the build unless its source or build configuration has changed.

## Use the plugin

Ask Copilot to show a notification. For example:

> Show a Windows notification titled "Explore Windows Notify MCP" with a "View repository" button that opens https://github.com/mthalman/windows-notify-mcp and an "Explain here" button. If I click "Explain here", inspect the repository and explain what it does in two sentences.

Select **View repository** to open GitHub in your browser, or select **Explain here** and ask Copilot to check the notification. Copilot can then give you the explanation. The plugin does not push the response to Copilot; Copilot has to check for it.

Link buttons open HTTP or HTTPS URLs and do not report a response to Copilot. Button responses expire after one hour and are lost when the Copilot session ends. Images are not supported.

## Troubleshooting

- **No visible banner:** In Windows **Settings > System > Notifications**, enable notifications for the application and check Do Not Disturb and banner permissions. Check Notification Center too. A successful submission does not guarantee a visible banner.
- **Registration or delivery error:** Make sure Copilot is running in a non-elevated, interactive desktop session. Check the server's stderr for details.
- **Button response unavailable:** Keep the same Copilot session open and check within one hour. A dismissed notification remains pending; a link button does not produce a response.
