using Microsoft.Extensions.Logging.Abstractions;
using WindowsNotifyMcp;
using Xunit;

namespace WindowsNotifyMcp.Tests;

public sealed class NotificationServiceTests
{
    [Fact]
    public void SubmissionIsPendingAndRetrievalDoesNotConsumeIt()
    {
        using var backend = new FakeBackend();
        var time = new FakeTime();
        using var service = new NotificationService(backend, time);
        var submission = service.Show("Title", "Message");
        Assert.Equal("submitted", submission.Status);
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromHours(1), submission.ExpiresAt);
        var result = service.GetResult(submission.NotificationId);
        Assert.Equal("pending", result.Status);
        Assert.Equal(result, service.GetResult(submission.NotificationId));
    }

    [Fact]
    public void CallbackRecordsFirstActionAndTimestamp()
    {
        using var backend = new FakeBackend();
        var time = new FakeTime();
        using var service = new NotificationService(backend, time);
        var submission = service.Show("Title", "Message",
            [new("Yes", ActionId: "yes"), new("No", ActionId: "no")]);
        backend.Click("yes");
        var first = service.GetResult(submission.NotificationId);
        Assert.Equal("activated", first.Status);
        Assert.Equal("yes", first.ActionId);
        Assert.Equal(time.GetUtcNow(), first.ActivatedAt);
        time.Advance(TimeSpan.FromMinutes(1));
        backend.Click("no");
        Assert.Equal(first, service.GetResult(submission.NotificationId));
    }

    [Fact]
    public void BodyClickIsActivationWithoutAction()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        var submission = service.Show("Title", "Message");
        backend.Click(null);
        var result = service.GetResult(submission.NotificationId);
        Assert.Equal("activated", result.Status);
        Assert.Null(result.ActionId);
    }

    [Fact]
    public async Task WaitReturnsAnActivationThatArrivesAfterItStarts()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        var submission = service.Show("Title", "Message", [new("Yes", ActionId: "yes")]);

        var waiting = service.WaitForResultAsync(submission.NotificationId, TimeSpan.FromSeconds(5));
        backend.Click("yes");

        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("activated", result.Status);
        Assert.Equal("yes", result.ActionId);
    }

    [Fact]
    public async Task WaitReturnsPendingWhenItTimesOut()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        var submission = service.Show("Title", "Message");

        var result = await service.WaitForResultAsync(submission.NotificationId, TimeSpan.FromMilliseconds(20));

        Assert.Equal("pending", result.Status);
    }

    [Fact]
    public async Task TaskWaitIgnoresTheFallbackTimeoutUntilActivation()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, TimeProvider.System, TimeSpan.FromSeconds(2));
        var submission = service.Show("Title", "Message", [new("Yes", ActionId: "yes")]);

        var waiting = service.WaitForResultAsync(
            submission.NotificationId, TimeSpan.FromMilliseconds(20), waitUntilExpired: true);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);

        backend.Click("yes");
        Assert.Equal("activated", (await waiting).Status);
    }

    [Fact]
    public async Task WaitReturnsExpiredWhenTheNotificationExpiresFirst()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, TimeProvider.System, TimeSpan.FromMilliseconds(30));
        var submission = service.Show("Title", "Message");

        var result = await service.WaitForResultAsync(submission.NotificationId, TimeSpan.FromSeconds(1));

        Assert.Equal("expired", result.Status);
        Assert.Equal(submission.ExpiresAt, result.ExpiresAt);
    }

    [Fact]
    public async Task WaitCanBeCanceled()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        var submission = service.Show("Title", "Message");
        using var cancellation = new CancellationTokenSource();

        var waiting = service.WaitForResultAsync(
            submission.NotificationId, TimeSpan.FromSeconds(5), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public void UnissuedIdsTokensAndActionsCannotActivate()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        var submission = service.Show("Title", "Message", [new("Yes", ActionId: "yes")]);
        backend.Emit(new NotificationActivation(submission.NotificationId, "wrong", "yes"));
        backend.Emit(new NotificationActivation(Guid.NewGuid().ToString("N"), backend.Token!, "yes"));
        backend.Click("unissued");
        Assert.Equal("pending", service.GetResult(submission.NotificationId).Status);
    }

    [Fact]
    public void FastClickDuringSubmissionIsNotLost()
    {
        using var backend = new FakeBackend { ClickDuringShow = true };
        using var service = new NotificationService(backend, new FakeTime());
        var submission = service.Show("Title", "Message", [new("Yes", ActionId: "yes")]);
        Assert.Equal("yes", service.GetResult(submission.NotificationId).ActionId);
    }

    [Fact]
    public void CapacityRejectsWithoutEvictionAndExpirationFreesSpace()
    {
        using var backend = new FakeBackend();
        var time = new FakeTime();
        using var service = new NotificationService(backend, time);
        var first = service.Show("Title", "Message");
        for (var i = 1; i < NotificationService.Capacity; i++)
            service.Show("Title", "Message");
        Assert.Throws<NotificationException>(() => service.Show("Title", "Message"));
        Assert.Equal("pending", service.GetResult(first.NotificationId).Status);
        time.Advance(NotificationService.Retention - TimeSpan.FromTicks(1));
        Assert.Equal("pending", service.GetResult(first.NotificationId).Status);
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Throws<NotificationException>(() => service.GetResult(first.NotificationId));
        backend.Click(null);
        Assert.Equal("submitted", service.Show("Title", "Message").Status);
    }

    [Fact]
    public void FailedSubmissionRollsBackTracking()
    {
        using var backend = new FakeBackend { Fail = true };
        using var service = new NotificationService(backend, new FakeTime());
        for (var i = 0; i < NotificationService.Capacity + 1; i++)
            Assert.Throws<NotificationException>(() => service.Show("Title", "Message"));
        Assert.Throws<NotificationException>(() => service.GetResult(backend.Id!));
        backend.Fail = false;
        Assert.Equal("submitted", service.Show("Title", "Message").Status);
    }

    [Fact]
    public void CanceledSubmissionDoesNotReachBackend()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        Assert.Throws<OperationCanceledException>(() =>
            service.Show("Title", "Message", cancellationToken: new CancellationToken(true)));
        Assert.Null(backend.Id);
    }

    [Fact]
    public void ConcurrentSubmissionsRespectCapacity()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        var accepted = 0;
        var rejected = 0;
        Parallel.For(0, 300, _ =>
        {
            try
            {
                var submission = service.Show("Title", "Message");
                Assert.Equal("pending", service.GetResult(submission.NotificationId).Status);
                Interlocked.Increment(ref accepted);
            }
            catch (NotificationException)
            {
                Interlocked.Increment(ref rejected);
            }
        });
        Assert.Equal(256, accepted);
        Assert.Equal(44, rejected);
    }

    [Theory]
    [InlineData(null, "message")]
    [InlineData("", "message")]
    [InlineData(" ", "message")]
    [InlineData("title", null)]
    [InlineData("title", "")]
    [InlineData("title", " ")]
    [InlineData("bad\u0001", "message")]
    public void InvalidTextIsRejected(string? title, string? message)
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        Assert.Throws<NotificationException>(() => service.Show(title!, message!));
        Assert.Null(backend.Id);
    }

    [Fact]
    public void UnpairedSurrogateIsRejected()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        // Construct invalid UTF-16 at runtime; attribute argument encoding can replace it.
        Assert.Throws<NotificationException>(() => service.Show("Title", new string((char)0xd800, 1)));
        Assert.Throws<NotificationException>(() => service.Show("Title", "Message",
            [new("Open", "https://example.com/" + new string((char)0xd800, 1))]));
    }

    [Fact]
    public void TextLimitsAreExact()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        service.Show(new string('t', 256), new string('m', 2048));
        Assert.Throws<NotificationException>(() => service.Show(new string('t', 257), "m"));
        Assert.Throws<NotificationException>(() => service.Show("t", new string('m', 2049)));
    }

    public static TheoryData<NotificationButton[]> InvalidButtons => new()
    {
        new NotificationButton[] { null! },
        new NotificationButton[] { new(" ") },
        new NotificationButton[] { new("No action") },
        new NotificationButton[] { new("Ambiguous", "https://example.com", "action") },
        new NotificationButton[] { new("Empty URL", "") },
        new NotificationButton[] { new("Empty action", ActionId: " ") },
        new NotificationButton[] { new("One", ActionId: "same"), new("Two", ActionId: "same") },
        Enumerable.Repeat(new NotificationButton("URL", "https://example.com"), 6).ToArray(),
        new NotificationButton[] { new(new string('x', 65), ActionId: "action") },
        new NotificationButton[] { new("Action", ActionId: new string('x', 129)) }
    };

    [Theory]
    [MemberData(nameof(InvalidButtons))]
    public void InvalidButtonDefinitionsAreRejected(NotificationButton[] buttons)
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        Assert.Throws<NotificationException>(() => service.Show("Title", "Message", buttons));
        Assert.Null(backend.Id);
    }

    [Theory]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:")]
    [InlineData("ftp://example.com")]
    [InlineData("/relative")]
    [InlineData("https://user:password@example.com")]
    [InlineData("https://example.com/\r\nbad")]
    public void UnsafeUrlsAreRejected(string url)
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        Assert.Throws<NotificationException>(() =>
            service.Show("Title", "Message", [new("Open", url)]));
    }

    [Theory]
    [InlineData("https://example.com/a?x=1&y=2")]
    [InlineData("http://localhost:1234/")]
    public void HttpUrlsAreAccepted(string url)
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        Assert.Equal("submitted", service.Show("Title", "Message", [new("Open", url)]).Status);
    }

    [Fact]
    public void DisposedServiceRejectsCallsAndIgnoresLateActivation()
    {
        using var backend = new FakeBackend();
        var service = new NotificationService(backend, new FakeTime());
        var submission = service.Show("Title", "Message");
        service.Dispose();
        service.Dispose();
        backend.Click(null);
        Assert.Throws<ObjectDisposedException>(() => service.GetResult(submission.NotificationId));
        Assert.Throws<ObjectDisposedException>(() => service.Show("Title", "Message"));
    }

    [Fact]
    public async Task SlowSubmissionDoesNotBlockResultReadsOrActivation()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        var first = service.Show("Title", "Message");
        var activation = new NotificationActivation(first.NotificationId, backend.Token!, null);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        backend.OnShow = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release native submission.");
        };
        var submission = Task.Run(() => service.Show("Slow", "Message"));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var read = Task.Run(() => service.GetResult(first.NotificationId));
            Assert.Equal("pending", (await read.WaitAsync(TimeSpan.FromSeconds(2))).Status);
            await Task.Run(() => backend.Emit(activation)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("activated", service.GetResult(first.NotificationId).Status);
        }
        finally
        {
            release.Set();
            await submission.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task DisposalWaitsForSubmissionAndStopsNewSubmissions()
    {
        using var backend = new FakeBackend();
        var service = new NotificationService(backend, new FakeTime());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        backend.OnShow = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release native submission.");
        };
        var submission = Task.Run(() => service.Show("Title", "Message"));
        Task? disposal = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            disposal = Task.Run(service.Dispose);
            Assert.NotSame(disposal, await Task.WhenAny(disposal, Task.Delay(100)));
            await Task.Run(() =>
                Assert.Throws<ObjectDisposedException>(() => service.Show("Rejected", "Message")))
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.Set();
            await submission.WaitAsync(TimeSpan.FromSeconds(5));
            if (disposal is not null)
                await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            service.Dispose();
        }
        Assert.Throws<ObjectDisposedException>(() => service.Show("Title", "Message"));
    }

    [Fact]
    public async Task InFlightReservationKeepsCapacityButCannotReturnAnExpiredResult()
    {
        using var backend = new FakeBackend();
        var time = new FakeTime();
        using var service = new NotificationService(backend, time);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var showCount = 0;
        backend.OnShow = () =>
        {
            if (Interlocked.Increment(ref showCount) != 1)
                return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release native submission.");
        };
        var submission = Task.Run(() => service.Show("Slow", "Message"));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var slowId = backend.Id!;
            var activation = new NotificationActivation(slowId, backend.Token!, null);
            time.Advance(NotificationService.Retention);
            for (var index = 1; index < NotificationService.Capacity; index++)
                service.Show("Title", "Message");
            Assert.Throws<NotificationException>(() => service.Show("At capacity", "Message"));
            backend.Emit(activation);
            Assert.Throws<NotificationException>(() => service.GetResult(slowId));
        }
        finally
        {
            release.Set();
            await submission.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal("submitted", service.Show("Space reclaimed", "Message").Status);
    }

    [Fact]
    public async Task CanceledQueuedSubmissionDoesNotDeliverAndReleasesItsReservation()
    {
        using var admission = new WindowsNotificationBackend(NullLogger<WindowsNotificationBackend>.Instance);
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var delivered = 0;
        backend.OnSubmit = token =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt == 2)
                queued.SetResult();
            admission.ExecuteSubmission(() =>
            {
                if (attempt == 1)
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Test did not release native submission.");
                }
                Interlocked.Increment(ref delivered);
            }, token);
        };
        var active = Task.Run(() => service.Show("Active", "Message"));
        Task<NotificationSubmission>? canceled = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            canceled = Task.Run(() => service.Show("Queued", "Message",
                cancellationToken: cancellation.Token));
            await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var canceledId = backend.Id!;
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => canceled.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(active.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref delivered));
            Assert.Throws<NotificationException>(() => service.GetResult(canceledId));
        }
        finally
        {
            release.Set();
            await active.WaitAsync(TimeSpan.FromSeconds(5));
            if (canceled is not null)
            {
                try
                {
                    await canceled.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
            }
        }

        for (var index = 1; index < NotificationService.Capacity; index++)
            service.Show("Title", "Message");
        Assert.Equal(NotificationService.Capacity, delivered);
        Assert.Throws<NotificationException>(() => service.Show("At capacity", "Message"));
    }

    [Fact]
    public void PreCanceledBackendAdmissionDoesNotRunNativeCode()
    {
        using var backend = new WindowsNotificationBackend(NullLogger<WindowsNotificationBackend>.Instance);
        var delivered = false;
        Assert.ThrowsAny<OperationCanceledException>(() =>
            backend.ExecuteSubmission(() => delivered = true, new CancellationToken(true)));
        Assert.False(delivered);
        backend.ExecuteSubmission(() => delivered = true, CancellationToken.None);
        Assert.True(delivered);
    }

    [Fact]
    public void CancellationAfterDeliveryDoesNotTurnSuccessfulSubmissionIntoAnError()
    {
        using var backend = new FakeBackend();
        using var service = new NotificationService(backend, new FakeTime());
        using var cancellation = new CancellationTokenSource();
        backend.OnSubmit = _ => cancellation.Cancel();
        var submission = service.Show("Already delivered", "Message",
            cancellationToken: cancellation.Token);
        Assert.Equal("submitted", submission.Status);
        Assert.Equal("pending", service.GetResult(submission.NotificationId).Status);
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class FakeBackend : INotificationBackend
    {
        public event Action<NotificationActivation>? Activated;
        public string? Id { get; private set; }
        public string? Token { get; private set; }
        public bool Fail { get; set; }
        public bool ClickDuringShow { get; init; }
        public Action? OnShow { get; set; }
        public Action<CancellationToken>? OnSubmit { get; set; }

        public void Show(NotificationContent content, string notificationId, string token,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Id = notificationId;
            Token = token;
            OnShow?.Invoke();
            OnSubmit?.Invoke(cancellationToken);
            if (Fail)
                throw new NotificationException("Simulated Windows failure.");
            if (ClickDuringShow)
                Click(content.Buttons.First().ActionId);
        }

        public void Click(string? actionId) => Emit(new NotificationActivation(Id!, Token!, actionId));
        public void Emit(NotificationActivation activation) => Activated?.Invoke(activation);
        public void Dispose() { }
    }
}
