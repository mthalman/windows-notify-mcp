using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WindowsNotifyMcp;

// Windows activation may reach another instance of this executable. Route it to the
// session encoded in the toast rather than recording it against the receiving client.
internal sealed class ActivationRouter : IDisposable
{
    private const int MaxBytes = 8192;
    private const int ListenerCount = 8;
    private readonly CancellationTokenSource stopping = new();
    private readonly ILogger logger;
    private readonly Action<NotificationActivation> activated;
    private readonly Task[] listeners;

    public string Session { get; } = Guid.NewGuid().ToString("N");

    public void ThrowIfFaulted()
    {
        foreach (var listener in listeners)
            if (listener.IsFaulted)
                listener.GetAwaiter().GetResult();
    }

    public ActivationRouter(Action<NotificationActivation> activated, ILogger logger)
    {
        this.activated = activated;
        this.logger = logger;
        // Pre-create bounded independent listeners so one incomplete sender cannot
        // hold up other activations, and registration never races pipe availability.
        var pipes = new List<NamedPipeServerStream>();
        var initialized = false;
        try
        {
            for (var index = 0; index < ListenerCount; index++)
                pipes.Add(CreatePipe());
            listeners = pipes.Select(ListenAsync).ToArray();
            initialized = true;
        }
        finally
        {
            if (!initialized)
            {
                foreach (var pipe in pipes)
                    pipe.Dispose();
                stopping.Dispose();
            }
        }
    }

    public async Task RouteAsync(string arguments, CancellationToken cancellationToken)
    {
        var parsed = Parse(arguments);
        if (parsed.Session == Session)
        {
            activated(parsed.Activation);
            return;
        }
        await ForwardAsync(parsed.Session, arguments, cancellationToken);
    }

    public static async Task ForwardAsync(string session, string arguments, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName(session), PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(cancellationToken);
        var bytes = Encoding.UTF8.GetBytes(arguments);
        if (bytes.Length > MaxBytes)
            throw new NotificationException("Activation arguments exceed the supported size.");
        // Byte-mode pipes have no message boundaries; prefix each UTF-8 payload with its length.
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await pipe.WriteAsync(header, cancellationToken);
        await pipe.WriteAsync(bytes, cancellationToken);
        await pipe.FlushAsync(cancellationToken);
    }

    public static (string Session, NotificationActivation Activation) Parse(string arguments)
    {
        if (arguments.Length > MaxBytes)
            throw new NotificationException("Invalid activation arguments.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in arguments.Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !values.TryAdd(pair[0], Uri.UnescapeDataString(pair[1])))
                throw new NotificationException("Invalid activation arguments.");
        }
        // Validate the session before it becomes part of a pipe name. The notification
        // service separately checks the issued token and action ID.
        if (!values.TryGetValue("session", out var session) || !Guid.TryParseExact(session, "N", out _) ||
            !values.TryGetValue("id", out var id) || !Guid.TryParseExact(id, "N", out _) ||
            !values.TryGetValue("token", out var token) || token.Length != 64 ||
            !token.All(Uri.IsHexDigit) || values.Keys.Any(key => key is not ("session" or "id" or "token" or "action")))
            throw new NotificationException("Invalid activation arguments.");
        values.TryGetValue("action", out var action);
        return (session, new NotificationActivation(id, token, action));
    }

    // Limit IPC to the current user; this does not isolate mutually untrusted same-user processes.
    private NamedPipeServerStream CreatePipe() => new(PipeName(Session), PipeDirection.In, ListenerCount,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static string PipeName(string session) => $"WindowsNotifyMcp-{session}";

    private async Task ListenAsync(NamedPipeServerStream pipe)
    {
        while (true)
        {
            using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stopping.Token);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    // Bound each sender's occupancy without blocking the other listeners.
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var header = new byte[4];
                    await pipe.ReadExactlyAsync(header, timeout.Token);
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length is < 1 or > MaxBytes)
                        throw new NotificationException("Invalid activation message length.");
                    var bytes = new byte[length];
                    await pipe.ReadExactlyAsync(bytes, timeout.Token);
                    var parsed = Parse(Encoding.UTF8.GetString(bytes));
                    if (parsed.Session != Session)
                        throw new NotificationException("Activation belongs to another session.");
                    activated(parsed.Activation);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or NotificationException)
                {
                    logger.LogWarning(ex, "Could not receive notification activation.");
                }
            }
            if (stopping.IsCancellationRequested)
                return;
            pipe = CreatePipe();
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        try
        {
            Task.WhenAll(listeners).GetAwaiter().GetResult();
        }
        finally
        {
            stopping.Dispose();
        }
    }
}
