using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace RaCMAN.App;

public enum LiveSplitStatus
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>
/// The timer phase LiveSplit reports for <c>getcurrenttimerphase</c>. <see cref="Unknown"/> is what
/// the client holds before it has asked, or when the answer was something it does not know.
/// </summary>
public enum LiveSplitPhase
{
    Unknown,
    NotRunning,
    Running,
    Paused,
    Ended,
}

/// <summary>
/// The client half of LiveSplit's built-in TCP server: one connection, ASCII commands terminated
/// with CRLF, replies one line at a time. Commands are fire and forget; the few queries are
/// answered on the same connection, so everything is serialised through one worker and a query
/// gives up after a second rather than holding the caller.
/// <para>
/// Nothing here blocks the render thread: <see cref="Send"/> only enqueues, and
/// <see cref="QueryAsync"/> is awaited off the UI thread with its result posted back.
/// </para>
/// </summary>
public sealed class LiveSplitClient : IDisposable
{
    public const string DefaultHost = "127.0.0.1";

    public const int DefaultPort = 16834;

    /// <summary>How LiveSplit's server is switched on, which is not obvious and not on by default.</summary>
    public const string ServerHint =
        "LiveSplit's server ships with LiveSplit but is not running until you start it: right-click "
        + "LiveSplit, Control -> Start TCP Server. It listens on port 16834.";

    // Fire and forget, section "commands" of LiveSplit.Server.
    public const string StartTimer = "starttimer";
    public const string StartOrSplit = "startorsplit";
    public const string Split = "split";
    public const string Unsplit = "unsplit";
    public const string SkipSplit = "skipsplit";
    public const string Reset = "reset";
    public const string Pause = "pause";
    public const string Resume = "resume";

    // One line back.
    public const string GetCurrentSplitName = "getcurrentsplitname";
    public const string GetUpcomingSplitName = "getupcomingsplitname";
    public const string GetPreviousSplitName = "getprevioussplitname";
    public const string GetSplitIndex = "getsplitindex";
    public const string GetCurrentTimerPhase = "getcurrenttimerphase";
    public const string GetLiveSplitVersion = "getlivesplitversion";
    public const string Ping = "ping";

    private static readonly HashSet<string> QueryCommands = new(StringComparer.Ordinal)
    {
        GetCurrentSplitName, GetUpcomingSplitName, GetPreviousSplitName,
        GetSplitIndex, GetCurrentTimerPhase, GetLiveSplitVersion, Ping,
    };

    /// <summary>A query that goes unanswered for this long is treated as a dead connection.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly List<byte> _pendingBytes = new();
    private readonly byte[] _readBuffer = new byte[512];

    private Channel<Operation> _queue = NewQueue();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private volatile LiveSplitStatus _status = LiveSplitStatus.Disconnected;
    private bool _disposed;

    private sealed class Operation
    {
        public required string Command { get; init; }

        /// <summary>Set for a query; completed with the reply line, or null when it never came.</summary>
        public TaskCompletionSource<string?>? Reply { get; init; }
    }

    private static Channel<Operation> NewQueue() =>
        Channel.CreateUnbounded<Operation>(new UnboundedChannelOptions { SingleReader = true });

    public string Host { get; private set; } = DefaultHost;

    public int Port { get; private set; } = DefaultPort;

    public LiveSplitStatus Status => _status;

    public bool IsConnected => _status == LiveSplitStatus.Connected;

    /// <summary>Whatever <c>getlivesplitversion</c> answered on connect. Null until it has.</summary>
    public string? Version { get; private set; }

    /// <summary>Why the last connection attempt failed, for the panel's status line.</summary>
    public string? LastError { get; private set; }

    /// <summary>True while the client wants a connection: connected, or waiting to retry.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Every command actually written to LiveSplit, in order. Debug aid and test hook.</summary>
    public int CommandsSent { get; private set; }

    /// <summary>Raised on the worker thread once a connection is up and its version is known.</summary>
    public event Action? Established;

    /// <summary>One line for the panel: the status, and the version or the reason it is not connected.</summary>
    public string StatusLine => _status switch
    {
        LiveSplitStatus.Connected => $"Connected to LiveSplit{(Version is null ? string.Empty : $" {Version}")} at {Host}:{Port}",
        LiveSplitStatus.Connecting => $"Connecting to {Host}:{Port}...",
        _ when !Enabled => "Not connected",
        _ => $"Not connected to {Host}:{Port}{(LastError is null ? string.Empty : $" ({LastError})")}",
    };

    /// <summary>
    /// Points the client at a server and keeps it there: it reconnects with a backoff for as long
    /// as it is enabled, because LiveSplit's server is often started after the game is.
    /// </summary>
    public void Start(string host, int port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        host = string.IsNullOrWhiteSpace(host) ? DefaultHost : host.Trim();
        if (port <= 0 || port > 65535) port = DefaultPort;

        lock (_gate)
        {
            if (Enabled && _worker is { IsCompleted: false }
                && string.Equals(Host, host, StringComparison.OrdinalIgnoreCase) && Port == port)
            {
                return;
            }
        }

        Stop();

        lock (_gate)
        {
            Host = host;
            Port = port;
            Enabled = true;
            _queue = NewQueue();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _worker = Task.Run(() => WorkerAsync(token), token);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? worker;
        lock (_gate)
        {
            Enabled = false;
            cts = _cts;
            worker = _worker;
            _cts = null;
            _worker = null;
        }

        try { cts?.Cancel(); } catch { /* already gone */ }

        try
        {
            worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
            // The worker only ever ends on its own token.
        }

        cts?.Dispose();
        _status = LiveSplitStatus.Disconnected;
        Version = null;
        DrainQueue();
    }

    /// <summary>
    /// Queues one fire-and-forget command. Dropped when nothing is connected, rather than held:
    /// a split that arrives after the connection comes back is a split in the wrong place.
    /// </summary>
    public void Send(string command)
    {
        if (_disposed || !IsConnected) return;
        _queue.Writer.TryWrite(new Operation { Command = command });
    }

    /// <summary>
    /// Sends one query and waits for its single line. Null when nothing is connected or the answer
    /// did not arrive within <see cref="QueryTimeout"/>.
    /// </summary>
    public async Task<string?> QueryAsync(string command)
    {
        if (_disposed || !IsConnected) return null;

        var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new Operation { Command = command, Reply = reply })) return null;

        // The worker's own read timeout is the real one; this is only a backstop for a query that
        // was queued behind a connection that never came up.
        var completed = await Task.WhenAny(reply.Task, Task.Delay(QueryTimeout + QueryTimeout)).ConfigureAwait(false);
        return completed == (Task)reply.Task ? reply.Task.Result : null;
    }

    /// <summary>The timer phase behind a <c>getcurrenttimerphase</c> answer.</summary>
    public static LiveSplitPhase ParsePhase(string? reply) => (reply ?? string.Empty).Trim() switch
    {
        "NotRunning" => LiveSplitPhase.NotRunning,
        "Running" => LiveSplitPhase.Running,
        "Paused" => LiveSplitPhase.Paused,
        "Ended" => LiveSplitPhase.Ended,
        _ => LiveSplitPhase.Unknown,
    };

    public static bool IsQuery(string command) => QueryCommands.Contains(command);

    // ---------------------------------------------------------------- worker

    private async Task WorkerAsync(CancellationToken token)
    {
        int attempt = 0;

        while (!token.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                _status = LiveSplitStatus.Connecting;
                client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(Host, Port, token).ConfigureAwait(false);

                var stream = client.GetStream();
                _pendingBytes.Clear();
                attempt = 0;
                LastError = null;
                _status = LiveSplitStatus.Connected;

                Version = await AskAsync(stream, GetLiveSplitVersion, token).ConfigureAwait(false);
                Established?.Invoke();

                await PumpAsync(stream, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex is SocketException socket ? socket.SocketErrorCode.ToString() : ex.Message;
            }
            finally
            {
                client?.Dispose();
                _status = LiveSplitStatus.Disconnected;
                Version = null;
                DrainQueue();
            }

            if (token.IsCancellationRequested) break;

            // 1 s, 2 s, 4 s, then every 5 s: LiveSplit's server is usually started by hand, and
            // the panel says so, so retrying forever at a calm rate is the right behaviour.
            attempt++;
            int seconds = attempt >= 4 ? 5 : 1 << (attempt - 1);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _status = LiveSplitStatus.Disconnected;
        DrainQueue();
    }

    private async Task PumpAsync(NetworkStream stream, CancellationToken token)
    {
        await foreach (var operation in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            try
            {
                await WriteAsync(stream, operation.Command, token).ConfigureAwait(false);
                CommandsSent++;

                if (operation.Reply is null) continue;

                var line = await ReadLineAsync(stream, token).ConfigureAwait(false);
                operation.Reply.TrySetResult(line);
            }
            catch (Exception)
            {
                // A failed write or a query that went unanswered means this connection is done;
                // the worker reconnects and the caller sees a null answer.
                operation.Reply?.TrySetResult(null);
                throw;
            }
        }
    }

    /// <summary>One query on a stream the pump does not own yet, used for the version handshake.</summary>
    private async Task<string?> AskAsync(NetworkStream stream, string command, CancellationToken token)
    {
        await WriteAsync(stream, command, token).ConfigureAwait(false);
        CommandsSent++;
        return await ReadLineAsync(stream, token).ConfigureAwait(false);
    }

    private static Task WriteAsync(NetworkStream stream, string command, CancellationToken token)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\r\n");
        return stream.WriteAsync(bytes, token).AsTask();
    }

    /// <summary>
    /// Reads one CRLF-terminated line, giving up after <see cref="QueryTimeout"/>. A timeout throws
    /// so the worker drops the connection: LiveSplit always answers a query, so silence means the
    /// far end is gone or wedged, and reconnecting is what gets the panel truthful again.
    /// </summary>
    private async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(QueryTimeout);

        while (true)
        {
            int newline = _pendingBytes.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                var line = Encoding.ASCII.GetString(_pendingBytes.ToArray(), 0, newline).TrimEnd('\r');
                _pendingBytes.RemoveRange(0, newline + 1);
                return line;
            }

            int read = await stream.ReadAsync(_readBuffer, timeout.Token).ConfigureAwait(false);
            if (read <= 0) throw new IOException("LiveSplit closed the connection");
            _pendingBytes.AddRange(_readBuffer.AsSpan(0, read).ToArray());
        }
    }

    /// <summary>Fails every queued query and drops the commands: they belonged to a dead connection.</summary>
    private void DrainQueue()
    {
        while (_queue.Reader.TryRead(out var operation)) operation.Reply?.TrySetResult(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
