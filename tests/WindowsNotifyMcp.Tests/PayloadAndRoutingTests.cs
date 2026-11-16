using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using WindowsNotifyMcp;
using Xunit;

namespace WindowsNotifyMcp.Tests;

public sealed class PayloadAndRoutingTests
{
    [Fact]
    public void PayloadEscapesTextAndSeparatesProtocolFromCallbacks()
    {
        var session = Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid().ToString("N");
        var token = new string('A', 64);
        var content = new NotificationContent("<Title> & \"quotes\"", "Message & <text>",
            (NotificationButton[])[new("Open & see", "https://example.com/?a=1&b=2"),
             new("Reply", ActionId: "a&= +/unicode-\u2713")]);
        var xml = XElement.Parse(NotificationPayload.Create(content, id, token, session));
        Assert.Equal(content.Title, xml.Descendants("text").First().Value);
        Assert.Equal(content.Message, xml.Descendants("text").Last().Value);
        var buttons = xml.Descendants("action").ToArray();
        Assert.Equal("protocol", buttons[0].Attribute("activationType")!.Value);
        Assert.Equal(content.Buttons[0].Url, buttons[0].Attribute("arguments")!.Value);
        Assert.Equal(content.Buttons[0].Label, buttons[0].Attribute("content")!.Value);
        Assert.Equal("foreground", buttons[1].Attribute("activationType")!.Value);
        var parsed = ActivationRouter.Parse(buttons[1].Attribute("arguments")!.Value);
        Assert.Equal(session, parsed.Session);
        Assert.Equal(new NotificationActivation(id, token, content.Buttons[1].ActionId), parsed.Activation);
        Assert.Null(ActivationRouter.Parse(xml.Attribute("launch")!.Value).Activation.ActionId);
    }

    [Fact]
    public void PayloadLimitCountsUtf8BytesAfterEscaping()
    {
        Assert.Throws<NotificationException>(() => NotificationPayload.Create(
            new NotificationContent("Title", new string('\u4e00', 2048), []), "id", "token", "session"));
        Assert.Throws<NotificationException>(() => NotificationPayload.Create(
            new NotificationContent("Title", new string('&', 2048), []), "id", "token", "session"));
    }

    [Fact]
    public void PayloadAcceptsExactlyFiveThousandBytes()
    {
        var baseline = NotificationPayload.Create(new NotificationContent("Title", "", []), "id", "token", "session");
        var length = 5000 - Encoding.UTF8.GetByteCount(baseline);
        var payload = NotificationPayload.Create(new NotificationContent("Title", new string('x', length), []),
            "id", "token", "session");
        Assert.Equal(5000, Encoding.UTF8.GetByteCount(payload));
        Assert.Throws<NotificationException>(() => NotificationPayload.Create(
            new NotificationContent("Title", new string('x', length + 1), []), "id", "token", "session"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("session=../../pipe&id=a&token=b")]
    [InlineData("session=a&session=b")]
    [InlineData("bad")]
    public void InvalidActivationArgumentsAreRejected(string arguments) =>
        Assert.Throws<NotificationException>(() => ActivationRouter.Parse(arguments));

    [Fact]
    public async Task ActivationsRouteToOriginalSessionOnly()
    {
        var firstResult = new TaskCompletionSource<NotificationActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResult = new TaskCompletionSource<NotificationActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = new ActivationRouter(value => firstResult.TrySetResult(value), NullLogger.Instance);
        using var second = new ActivationRouter(value => secondResult.TrySetResult(value), NullLogger.Instance);
        var id = Guid.NewGuid().ToString("N");
        var token = new string('A', 64);
        var xml = XElement.Parse(NotificationPayload.Create(new NotificationContent("Title", "Message",
            (NotificationButton[])[new("Yes", ActionId: "yes")]), id, token, first.Session));
        var arguments = xml.Descendants("action").Single().Attribute("arguments")!.Value;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await second.RouteAsync(arguments, timeout.Token);
        Assert.Equal(new NotificationActivation(id, token, "yes"),
            await firstResult.Task.WaitAsync(timeout.Token));
        Assert.False(secondResult.Task.IsCompleted);
    }

    [Fact]
    public async Task MissingSessionForwardingIsCanceled()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ActivationRouter.ForwardAsync(Guid.NewGuid().ToString("N"), "arguments", timeout.Token));
    }

    [Fact]
    public async Task MalformedPipeMessageDoesNotBreakSubsequentActivation()
    {
        var result = new TaskCompletionSource<NotificationActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var router = new ActivationRouter(value => result.TrySetResult(value), NullLogger.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var pipe = new NamedPipeClientStream(".", $"WindowsNotifyMcp-{router.Session}",
                   PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await pipe.ConnectAsync(timeout.Token);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, 8193);
            await pipe.WriteAsync(header, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
        }
        var id = Guid.NewGuid().ToString("N");
        var token = new string('A', 64);
        await ActivationRouter.ForwardAsync(router.Session,
            $"session={router.Session}&id={id}&token={token}", timeout.Token);
        Assert.Equal(new NotificationActivation(id, token, null), await result.Task.WaitAsync(timeout.Token));
    }

    [Fact]
    public async Task StalledSenderDoesNotBlockAnotherActivation()
    {
        var result = new TaskCompletionSource<NotificationActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var router = new ActivationRouter(value => result.TrySetResult(value), NullLogger.Instance);
        using var stalled = new NamedPipeClientStream(".", $"WindowsNotifyMcp-{router.Session}",
            PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await stalled.ConnectAsync(timeout.Token);
        var id = Guid.NewGuid().ToString("N");
        var token = new string('A', 64);
        await ActivationRouter.ForwardAsync(router.Session,
            $"session={router.Session}&id={id}&token={token}", timeout.Token);
        Assert.Equal(new NotificationActivation(id, token, null), await result.Task.WaitAsync(timeout.Token));
    }

    [Fact]
    public async Task ColdActivationReadsStoredPayloadAfterRegistrationWithoutWaitingForAnEvent()
    {
        var result = new TaskCompletionSource<NotificationActivation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var router = new ActivationRouter(value => result.TrySetResult(value), NullLogger.Instance);
        var id = Guid.NewGuid().ToString("N");
        var token = new string('A', 64);
        var calls = new List<string>();
        await WindowsNotificationBackend.HandleColdActivationAsync(
            () => calls.Add("register"),
            () =>
            {
                Assert.Equal(new[] { "register" }, calls);
                calls.Add("read");
                return $"session={router.Session}&id={id}&token={token}&action=confirm";
            },
            () => calls.Add("unregister"));
        Assert.Equal(new[] { "register", "read", "unregister" }, calls);
        Assert.Equal(new NotificationActivation(id, token, "confirm"),
            await result.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task ColdActivationUnregistersWhenReadingPayloadFails()
    {
        var unregistered = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsNotificationBackend.HandleColdActivationAsync(
                () => { },
                () => throw new InvalidOperationException("Simulated lifecycle failure."),
                () => unregistered = true));
        Assert.True(unregistered);
    }

    [Fact]
    public async Task ColdActivationDoesNotUnregisterWhenRegistrationFails()
    {
        var unregistered = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsNotificationBackend.HandleColdActivationAsync(
                () => throw new InvalidOperationException("Simulated registration failure."),
                () => throw new InvalidOperationException("Must not read after registration failure."),
                () => unregistered = true));
        Assert.False(unregistered);
    }
}
