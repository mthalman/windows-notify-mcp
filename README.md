# Windows Notify MCP

A .NET MCP server that submits native Windows toast notifications over local stdio. Notifications support a title, message, and up to five buttons. Buttons either open an HTTP/HTTPS URL or record a callback action for the MCP client to retrieve. Images are not supported.

## Prerequisites

- Windows 10 version 2004 (build 19041) or later, or Windows 11.
- An interactive desktop session running as a **non-administrator**. Elevated processes, Windows services, and headless sessions are unsupported.
- The .NET SDK specified by `global.json` to build and test.
- Visual Studio 2022 or later with the **Desktop development with C++** workload and a Windows SDK to publish Native AOT. ARM64 publishing also requires the C++ ARM64 build tools. The native publish does not require .NET or the Windows App SDK runtime to be installed on the destination machine.

## Install as a Copilot CLI plugin

The repository is a Copilot CLI plugin and its own plugin marketplace. Copilot downloads the source, and the plugin builds the server on first use. Nothing needs to be published to a package feed, and you don't need your own checkout.

The plugin requires Windows x64, PowerShell 7.2 or later, and the .NET SDK specified by `global.json`. It does not need the C++ build tools, because it builds a framework-dependent server instead of the Native AOT one.

Install it:

```powershell
copilot plugin marketplace add mthalman/windows-notify-mcp
copilot plugin install windows-notify-mcp@windows-notify-mcp
```

The plugin adds an MCP server named `windows-notify`. The first session builds the server, which can take a minute. Later sessions reuse the build unless its source or build configuration has changed.

Get updates:

```powershell
copilot plugin marketplace update windows-notify-mcp
copilot plugin update windows-notify-mcp@windows-notify-mcp
```

Then start a new Copilot session. To update automatically at the start of each session, set `"autoUpdate": true` on the marketplace's `extraKnownMarketplaces` entry in your Copilot user settings. Auto-update runs new code from the repository without review, so only enable it if you trust every change pushed to the default branch.

How it works:

1. `plugin.json` and `mcp.json` declare the plugin and its server. `.github\plugin\marketplace.json` lists the repository root as the plugin source.
2. Copilot starts `scripts\Start-Server.ps1`. It builds into one persistent directory under `PLUGIN_DATA`, reusing the executable until project sources or build configuration change. Documentation and test-only changes do not force a rebuild. Each rebuild starts with fresh intermediate files so timestamp-preserving source updates cannot reuse stale assemblies. Intermediate build files also stay in `PLUGIN_DATA`, not in Copilot's replaceable plugin checkout.
3. Build output goes to stderr so it can't corrupt the MCP protocol on stdout. The executable's path remains fixed across updates, preserving one Windows notification identity and its settings.
4. If an update requires rebuilding while an older server process is running from the plugin data directory, the launcher exits with an explanation instead of replacing its files. Startup also shares the update lock; if a process starts during the swap, the launcher restores the previous build. Close the other sessions using `windows-notify`, then start a new session to rebuild.
5. The script runs the server with its own stdin and stdout. Toast clicks after the server exits start the same executable.

## Build and configure your MCP client

1. Build and run the automated tests from the repository root:

   ```powershell
   dotnet build WindowsNotifyMcp.slnx
   dotnet test WindowsNotifyMcp.slnx
   ```

   Tests cover notification validation, tracking, activation routing, and stdio protocol behavior. They use a fake notification backend and do not display toasts.

2. Publish a Native AOT x64 application:

   ```powershell
   dotnet publish src\WindowsNotifyMcp\WindowsNotifyMcp.csproj --configuration Release --runtime win-x64 --output artifacts\publish\aot-win-x64
   ```

   `PublishAot` is enabled in the project. Publishing produces a self-contained native executable, without a JIT or managed application DLL. Ordinary builds and tests still use .NET. Tool serialization uses source-generated JSON metadata, and tool registration uses the MCP SDK's trim-compatible generic API.

   Keep the **entire publish directory**, not just the executable. Windows App SDK native files are required. For Windows on ARM, publish with `--runtime win-arm64` and use a separate output directory. Publish to a fresh directory when switching from a managed deployment to AOT so old runtime files are not mixed into the native output.

   To run the production-host protocol regression test against the native executable:

   ```powershell
   $env:WINDOWS_NOTIFY_MCP_NATIVE_EXE = (Resolve-Path artifacts\publish\aot-win-x64\WindowsNotifyMcp.exe).Path
   dotnet test WindowsNotifyMcp.slnx --configuration Release --filter FullyQualifiedName~ProductionHostUsesCleanJsonStdoutWithoutNativeRegistration
   Remove-Item Env:\WINDOWS_NOTIFY_MCP_NATIVE_EXE
   ```

   This also verifies that the executable has no CLR header and requires no application runtime configuration file.

3. Configure your MCP client to launch the published executable. Adapt the configuration location and enclosing keys to your client:

   ```json
   {
     "mcpServers": {
       "windows-notify": {
         "command": "C:\\tools\\windows-notify-mcp\\WindowsNotifyMcp.exe",
         "args": []
       }
     }
   }
   ```

   Replace the example path with the absolute path to your published executable. A development build can instead launch `src\WindowsNotifyMcp\bin\Debug\net11.0-windows10.0.19041.0\win-x64\WindowsNotifyMcp.exe`.

   Launch the apphost `.exe`, not `dotnet run` or `dotnet WindowsNotifyMcp.dll`. Windows notification activation needs a stable executable identity. The server reserves stdout for MCP traffic and writes diagnostics to stderr. It waits for a client handshake; running it directly does not show a notification.

4. Ask your client to call `show_notification` with the following arguments:

   ```json
   {
     "title": "Build finished",
     "message": "The application is ready to review.",
     "buttons": [
       { "label": "Open documentation", "url": "https://learn.microsoft.com/dotnet/" },
       { "label": "Acknowledge", "actionId": "acknowledge" }
     ]
   }
   ```

   The result includes a notification ID:

   ```json
   {
     "notificationId": "82b038ea569e4b428613a84ed798663f0",
     "status": "submitted",
     "expiresAt": "2026-10-23T18:00:00+00:00"
   }
   ```

   `submitted` means Windows accepted the notification. It does **not** guarantee a visible banner: Windows settings and Do Not Disturb can suppress it.

5. Click **Acknowledge** while the client and server remain connected, then call `get_notification_result` using the returned ID:

   ```json
   {
     "notificationId": "82b038ea569e4b428613a84ed798663f0"
   }
   ```

   A callback produces a result like this:

   ```json
   {
     "notificationId": "82b038ea569e4b428613a84ed798663f0",
     "status": "activated",
     "expiresAt": "2026-10-23T18:00:00+00:00",
     "actionId": "acknowledge",
     "activatedAt": "2026-10-23T17:02:00+00:00"
   }
   ```

   Before a click, the status is `pending`. Retrieving a result does not consume it.

## Tool inputs and limits

`show_notification` requires `title` (1-256 characters) and `message` (1-2048 characters). Both must contain non-whitespace text and valid XML characters. `buttons` is optional and accepts at most five entries.

Each button requires a non-whitespace `label` of at most 64 characters and exactly one of:

- `url`: an absolute HTTP/HTTPS URL of at most 2048 characters, without embedded credentials or control characters. Windows opens it with the registered protocol handler; it does not produce a callback.
- `actionId`: a non-whitespace callback identifier of at most 128 characters, unique within the notification. The value is returned when that button is clicked.

The complete XML notification payload, including escaped text and activation metadata, must fit within 5,000 UTF-8 bytes. This combined limit can reject a notification even when each field meets its individual limit. No arbitrary commands or custom URI schemes are accepted.

`get_notification_result` requires the `notificationId` returned by `show_notification`. It returns `pending` or `activated`. A toast-body click records activation with a null/omitted `actionId`. Only the first valid body or callback-button activation is recorded. `pending` does not indicate whether Windows is still displaying the notification; dismissal is not tracked.

Both tools return structured content and a JSON text representation. Invalid requests, unknown/expired IDs, disabled Windows notifications, and native delivery failures produce MCP tool errors.

## Callback lifetime

The server tracks at most **256 notifications** per process, including activated results and notifications without callback buttons. Each entry expires **one hour after submission**. At capacity, the server rejects new submissions without evicting unexpired entries. Expired entries are removed on subsequent operations.

State is in memory and disappears when the server stops. Keep the MCP connection open to retrieve callback results. No database, HTTP listener, unsolicited action notification, or automatic client polling is used. Multiple server processes route activation to the issuing session through a per-session, current-user-only Windows named pipe; an issued token and action mapping protect against accidental cross-session results. Eight independent pipe listeners keep an incomplete sender from blocking other activations, with a five-second read timeout per connection.

Tracking expiry does not remove a toast from Windows Notification Center. Clicking an old callback after its server has stopped cannot restore the lost state. Windows may briefly launch the executable for activation; that process reads the initial activation through Windows App SDK AppLifecycle, attempts to forward to the original session, and then exits instead of starting an orphan MCP server. URL buttons use Windows protocol activation independently of callback tracking.

Native submission does not hold the tracking lock, so an outstanding delivery does not block reads or recording other callbacks. Shutdown rejects new submissions and waits for in-flight submissions before clearing tracking. In-flight reservations count toward capacity until delivery finishes, even if they expire while Windows is processing them.

Cancellation interrupts requests waiting for native-delivery admission and releases their tracking reservations. It is also checked after registration and before posting the toast. A Windows API call already executing cannot be interrupted safely, and a toast already posted is not withdrawn by cancellation. If Windows registration or delivery hangs indefinitely, shutdown can also wait indefinitely; native delivery remains in-process rather than using a separately terminable helper process.

Windows registers notification support for the executable under the current user on the first valid native submission. The process unregisters its active notification handler on shutdown; it does not remove all notification history or permanently uninstall the application's notification identity.

## Troubleshooting

- **No visible banner:** Open Windows **Settings > System > Notifications**, enable notifications for the application, and check Do Not Disturb/Focus Assist and banner permissions. Inspect Notification Center as well. A successful submission is not proof that a banner appeared.
- **Registration or delivery error:** Run the actual `.exe` from its complete build/publish directory in a non-elevated desktop session. Check the MCP client's captured stderr for native error details.
- **Unknown or expired ID:** Use the ID from this server process and retrieve it within one hour. Reconnecting a client starts a new process and cannot recover old results.
- **Tracking is full:** Wait for entries to expire. Reading an entry does not delete it. Restarting the server clears all tracking but also loses all pending callbacks.
- **Callback remains pending:** Keep the server running and click a callback button rather than a URL button. A dismissed toast stays pending until expiry. If another server process receives the Windows activation, its stderr reports forwarding failures.

To check native behavior after publishing, submit a toast with both button types, verify the banner or Notification Center entry, click a callback and retrieve its result, and submit another toast to check URL activation. Automated tests do not replace this interactive check.

Built with the [official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) and [Windows App SDK app-notification APIs](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-console).
