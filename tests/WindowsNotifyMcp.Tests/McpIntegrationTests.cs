using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace WindowsNotifyMcp.Tests;

public sealed class McpIntegrationTests
{
    [Fact]
    public async Task StdioClientCanDiscoverSubmitAndRetrieveStructuredCallback()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Name = "Fake Windows notifications",
            Command = Path.ChangeExtension(typeof(McpIntegrationTests).Assembly.Location, ".exe")
        }), cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(new[] { "get_notification_result", "show_notification" },
            tools.Select(tool => tool.Name).Order().ToArray());
        var shown = await client.CallToolAsync("show_notification", new Dictionary<string, object?>
        {
            ["title"] = "Build finished",
            ["message"] = "Ready to inspect",
            ["buttons"] = new[] { new { label = "Review", actionId = "review" } }
        }, cancellationToken: timeout.Token);
        Assert.False(shown.IsError == true);
        var submission = JsonSerializer.SerializeToElement(shown.StructuredContent);
        Assert.Equal("submitted", submission.GetProperty("status").GetString());
        var id = submission.GetProperty("notificationId").GetString();
        var result = await client.CallToolAsync("get_notification_result", new Dictionary<string, object?>
        {
            ["notificationId"] = id
        }, cancellationToken: timeout.Token);
        Assert.False(result.IsError == true);
        var activated = JsonSerializer.SerializeToElement(result.StructuredContent);
        Assert.Equal("activated", activated.GetProperty("status").GetString());
        Assert.Equal("review", activated.GetProperty("actionId").GetString());
        Assert.True(activated.GetProperty("activatedAt").TryGetDateTimeOffset(out _));
        var invalid = await client.CallToolAsync("show_notification", new Dictionary<string, object?>
        {
            ["title"] = "",
            ["message"] = "Invalid input"
        }, cancellationToken: timeout.Token);
        Assert.True(invalid.IsError);
        Assert.Contains("Title", invalid.Content.OfType<TextContentBlock>().Single().Text);
        var unknown = await client.CallToolAsync("get_notification_result", new Dictionary<string, object?>
        {
            ["notificationId"] = "missing"
        }, cancellationToken: timeout.Token);
        Assert.True(unknown.IsError);
    }

    [Fact]
    public async Task ProductionHostUsesCleanJsonStdoutWithoutNativeRegistration()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WindowsNotifyMcp.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Name;
        var nativeExecutable = Environment.GetEnvironmentVariable("WINDOWS_NOTIFY_MCP_NATIVE_EXE");
        var executable = nativeExecutable ?? Path.Combine(directory.FullName, "src", "WindowsNotifyMcp", "bin",
            configuration, "net11.0-windows10.0.19041.0", "win-x64", "WindowsNotifyMcp.exe");
        if (nativeExecutable is not null)
        {
            // A successful launch alone cannot distinguish Native AOT from a managed apphost.
            using var stream = File.OpenRead(executable);
            using var image = new PEReader(stream);
            Assert.Null(image.PEHeaders.CorHeader);
            Assert.False(File.Exists(Path.ChangeExtension(executable, ".runtimeconfig.json")));
        }
        var toolLauncher = Environment.GetEnvironmentVariable("WINDOWS_NOTIFY_MCP_TOOL_LAUNCHER");
        var startInfo = new ProcessStartInfo(toolLauncher is null ? executable :
            Environment.GetEnvironmentVariable("ComSpec") ?? throw new InvalidOperationException("ComSpec is not set."))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = directory.FullName
        };
        if (toolLauncher is not null)
        {
            Assert.True(File.Exists(toolLauncher), $"Tool launcher does not exist: {toolLauncher}");
            startInfo.Arguments = $"/d /s /c \"\"{toolLauncher}\"\"";
        }
        using var process = Process.Start(startInfo)!;
        // Drain stderr concurrently so diagnostics cannot fill the pipe and stall the server.
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""");
            var initialize = await ReadResponse(process, 1, timeout.Token);
            Assert.True(initialize.GetProperty("result").TryGetProperty("serverInfo", out _));
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
            var list = await ReadResponse(process, 2, timeout.Token);
            Assert.Equal(2, list.GetProperty("result").GetProperty("tools").GetArrayLength());
            var showTool = list.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Single(tool => tool.GetProperty("name").GetString() == "show_notification");
            Assert.True(showTool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("buttons", out _));
            Assert.True(showTool.TryGetProperty("outputSchema", out _));
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"show_notification","arguments":{"title":"","message":"invalid"}}}""");
            var invalid = await ReadResponse(process, 3, timeout.Token);
            Assert.True(invalid.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Contains("Title", invalid.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"show_notification","arguments":{"title":"Test","message":"Invalid button","buttons":[{"label":"Unsafe","url":"file:///C:/Windows/notepad.exe"}]}}}""");
            var invalidButton = await ReadResponse(process, 4, timeout.Token);
            Assert.True(invalidButton.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Contains("HTTP/HTTPS", invalidButton.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"get_notification_result","arguments":{"notificationId":"unknown"}}}""");
            var unknown = await ReadResponse(process, 5, timeout.Token);
            Assert.True(unknown.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Contains("Unknown or expired", unknown.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(await process.StandardOutput.ReadToEndAsync(timeout.Token)));
            Assert.DoesNotContain("Windows notification submission failed", await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task<JsonElement> ReadResponse(Process process, int id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            Assert.NotNull(line);
            // Parse every line, including notifications, to catch non-protocol stdout output.
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("id", out var value) && value.GetInt32() == id)
                return json.RootElement.Clone();
        }
    }
}
