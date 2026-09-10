using System.Globalization;
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
/// The client half of LiveSplit's TCP server: one connection, ASCII commands terminated with CRLF,
/// replies one line at a time. Commands are fire and forget; the few queries are answered on the
/// same connection, so everything is serialised through one worker.
/// <para>
/// The one rule that matters here is that <b>only a socket error drops the connection</b>. A query
/// that goes unanswered is not an error: LiveSplit's server silently ignores a command it does not
/// know, and its command set differs between builds. Such a query is remembered as unanswered,
/// never sent again on this connection, and reported as "unknown" to the caller. Treating that
/// silence as a dead connection is what made the link drop a second after it came up.
/// </para>
/// <para>
/// Nothing here blocks the render thread: <see cref="Send"/> only enqueues, and
/// <see cref="QueryAsync"/> is awaited off the UI thread with its result posted back.
/// </para>
/// </summary>
public sealed class LiveSplitClient : IDisposable
{
    public const string DefaultHost = "127.0.0.1";

    public const int DefaultPort = 16834;

    // Fire and forget.
    public const string StartTimer = "starttimer";
    public const string StartOrSplit = "startorsplit";
    public const string Split = "split";
    public const string Unsplit = "unsplit";
    public const string SkipSplit = "skipsplit";
    public const string Reset = "reset";
    public const string Pause = "pause";
    public const string Resume = "resume";

    /// <summary>
    /// Takes one time and adds it to the run's loading times, which is how game time is corrected:
    /// game time is real time less the loading times, so a positive number takes time off the
    /// clock and a negative one puts time back on it. The server parses the argument with its own
    /// <c>TimeSpanParser</c>, which reads a leading minus, so both directions are one command.
    /// </summary>
    public const string AddLoadingTimes = "addloadingtimes";

    /// <summary>
    /// Stops and starts game time while real time keeps running, which is what LiveSplit does for
    /// an ASL's <c>isLoading</c>. Neither moves the timer phase: a paused game time is still a
    /// Running timer, unlike <see cref="Pause"/>.
    /// </summary>
    public const string PauseGameTime = "pausegametime";

    public const string UnpauseGameTime = "unpausegametime";

    /// <summary>
    /// Sets game time outright, the way <c>timer.SetGameTime</c> did in the old scripts. Sent while
    /// game time is paused it moves the clock LiveSplit froze, so the new value is what game time
    /// carries on from at the unpause.
    /// </summary>
    public const string SetGameTime = "setgametime";

    // One line back.
    public const string GetCurrentSplitName = "getcurrentsplitname";
    public const string GetUpcomingSplitName = "getupcomingsplitname";
    public const string GetPreviousSplitName = "getprevioussplitname";

    /// <summary>
    /// The final segment's name — but only once the timer is running; before that the server
    /// answers "-", the same as it answers the other two name queries at split index -1. It is the
    /// one name that does not move as the run goes on, so it is what tells two routes through the
    /// same game apart. What a build means by "last" is not guaranteed, so a run it does not match
    /// is only set aside while some other run does match it.
    /// </summary>
    public const string GetLastSplitName = "getlastsplitname";

    public const string GetSplitIndex = "getsplitindex";
    public const string GetCurrentTimerPhase = "getcurrenttimerphase";

    /// <summary>
    /// The run's game time as the server's <c>PreciseTimeFormatter</c> writes it, which is a
    /// <see cref="TimeSpan"/>'s own text: <c>00:01:14.8000000</c>, or <c>-</c> for no time at all.
    /// Asked while game time is paused it answers the frozen value, so it is stable to read, add to
    /// and write back. A build that does not know the command answers nothing at all.
    /// </summary>
    public const string GetCurrentGameTime = "getcurrentgametime";

    public const string Ping = "ping";

    private static readonly HashSet<string> QueryCommands = new(StringComparer.Ordinal)
    {
        GetCurrentSplitName, GetUpcomingSplitName, GetPreviousSplitName, GetLastSplitName,
        GetSplitIndex, GetCurrentTimerPhase, GetCurrentGameTime, Ping,
    };

    /// <summary>
    /// A query unanswered for this long is taken as one this server does not implement. It is not
    /// a connection failure, and the connection is left exactly as it was.
    /// </summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly List<byte> _pendingBytes = new();
    private readonly byte[] _readBuffer = new byte[512];
    private readonly HashSet<string> _unanswered = new(StringComparer.Ordinal);

    private Channel<Operation> _queue = NewQueue();
    private CancellationTokenSource? _cts;
    private Task? _worker;

    /// <summary>
    /// A read started for an earlier query that timed out. It is kept rather than cancelled, so the
    /// socket is never torn down mid-receive and a late reply is collected instead of corrupting
    /// the next one.
    /// </summary>
    private Task<int>? _pendingRead;

    private volatile LiveSplitStatus _status = LiveSplitStatus.Disconnected;
    private int _connectFailures;
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

    /// <summary>Why the last connection attempt failed, for the panel's status line.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// The last thing worth saying that did not drop the connection — a query this server does not
    /// answer, almost always. Cleared when a connection comes up.
    /// </summary>
    public string? LastNote { get; private set; }

    /// <summary>True while the client wants a connection: connected, or waiting to retry.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Every command actually written to LiveSplit, in order. Debug aid and test hook.</summary>
    public int CommandsSent { get; private set; }

    /// <summary>
    /// How many attempts never opened a connection at all: nothing was listening, or the machine
    /// refused it. A socket that dies once the connection is up is a drop and is not counted, so
    /// this really is "LiveSplit's server was not there". Read from the render thread, which is how
    /// the client tells a failure the user asked for from the reconnect loop's own retries.
    /// </summary>
    public int ConnectFailures => Volatile.Read(ref _connectFailures);

    /// <summary>Raised on the worker thread once a connection is up and the handshake is done.</summary>
    public event Action? Established;

    /// <summary>The queries this server has been asked and never answered, for the panel.</summary>
    public string[] Unanswered
    {
        get { lock (_gate) return _unanswered.ToArray(); }
    }

    /// <summary>True when this server answers a command at all, i.e. it is not one it ignores.</summary>
    public bool Answers(string command)
    {
        lock (_gate) return !_unanswered.Contains(command);
    }

    /// <summary>One line for the panel: the status, and the reason it is not connected.</summary>
    public string StatusLine => _status switch
    {
        LiveSplitStatus.Connected => $"Connected to LiveSplit at {Host}:{Port}",
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
    /// Sends one query and waits for its single line. Null when nothing is connected, when this
    /// server has already shown it does not answer this command, or when the answer did not arrive
    /// within <see cref="QueryTimeout"/>. A null is "unknown", never "the connection is gone".
    /// </summary>
    public async Task<string?> QueryAsync(string command)
    {
        if (_disposed || !IsConnected) return null;

        lock (_gate)
        {
            if (_unanswered.Contains(command)) return null;
        }

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

    /// <summary>
    /// A duration as LiveSplit's time parser reads it: seconds with six decimal places, so a
    /// microsecond survives the trip and nothing is rounded into the run. A negative one keeps its
    /// minus, which the server's parser reads and which is how time is added rather than taken off.
    /// </summary>
    public static string FormatTime(long microseconds) =>
        (microseconds / 1_000_000m).ToString("0.000000", CultureInfo.InvariantCulture);

    /// <summary>The whole <c>addloadingtimes</c> line for a correction of this many microseconds.</summary>
    public static string AddLoadingTimesCommand(long microseconds) =>
        $"{AddLoadingTimes} {FormatTime(microseconds)}";

    /// <summary>
    /// A game time as LiveSplit's parser reads it: <c>h:mm:ss.fffffff</c>, hours counted straight
    /// through rather than rolled into days, because the parser splits on colons and would choke on
    /// a <c>d.hh</c> the way <see cref="TimeSpan"/>'s own text writes a run past midnight.
    /// </summary>
    public static string FormatGameTime(TimeSpan time)
    {
        string sign = time < TimeSpan.Zero ? "-" : string.Empty;
        var size = time < TimeSpan.Zero ? time.Negate() : time;
        return string.Format(
            CultureInfo.InvariantCulture, "{0}{1}:{2:00}:{3:00}.{4:0000000}",
            sign, (int)size.TotalHours, size.Minutes, size.Seconds, size.Ticks % TimeSpan.TicksPerSecond);
    }

    /// <summary>The whole <c>setgametime</c> line for the clock this run should be showing.</summary>
    public static string SetGameTimeCommand(TimeSpan time) => $"{SetGameTime} {FormatGameTime(time)}";

    /// <summary>
    /// Reads back what <see cref="GetCurrentGameTime"/> answered. A colon means a clock and is read
    /// as one; a bare number can only be seconds, so it is read as seconds rather than handed to
    /// <see cref="TimeSpan.TryParse(string, out TimeSpan)"/>, which would call it days. Anything
    /// else — LiveSplit's <c>-</c> for "no time", or the silence of a build without the command —
    /// is false, and the caller corrects the run some other way rather than guessing.
    /// </summary>
    public static bool TryParseGameTime(string? reply, out TimeSpan time)
    {
        time = default;
        string text = (reply ?? string.Empty).Trim();
        if (text.Length == 0 || text == "-") return false;

        if (text.Contains(':'))
        {
            return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out time);
        }

        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal seconds))
        {
            return false;
        }

        time = TimeSpan.FromTicks((long)(seconds * TimeSpan.TicksPerSecond));
        return true;
    }

    // ---------------------------------------------------------------- worker

    private async Task WorkerAsync(CancellationToken token)
    {
        int attempt = 0;

        while (!token.IsCancellationRequested)
        {
            TcpClient? client = null;
            bool established = false;
            try
            {
                _status = LiveSplitStatus.Connecting;
                client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(Host, Port, token).ConfigureAwait(false);

                var stream = client.GetStream();
                lock (_gate)
                {
                    _pendingBytes.Clear();
                    _unanswered.Clear();
                }

                attempt = 0;
                LastError = null;
                LastNote = null;
                established = true;
                _status = LiveSplitStatus.Connected;

                // The whole handshake: one query every LiveSplit server has answered for as long
                // as it has had one. Silence is not a failure — the phase is simply unknown until
                // something asks again — so nothing here can decide the connection is dead.
                await AskAsync(stream, GetCurrentTimerPhase, token).ConfigureAwait(false);
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

                // Nothing was listening: the one failure the user can do something about, and the
                // one the panel puts a popup on.
                if (!established) Interlocked.Increment(ref _connectFailures);
            }
            finally
            {
                OrphanPendingRead();
                client?.Dispose();
                _status = LiveSplitStatus.Disconnected;
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
            if (operation.Reply is not null)
            {
                bool skip;
                lock (_gate) skip = _unanswered.Contains(operation.Command);
                if (skip)
                {
                    operation.Reply.TrySetResult(null);
                    continue;
                }
            }

            try
            {
                var line = await AskAsync(stream, operation.Command, token, operation.Reply is not null)
                    .ConfigureAwait(false);
                operation.Reply?.TrySetResult(line);
            }
            catch (Exception)
            {
                // A failed write or read is a real socket failure: this connection is done, the
                // worker reconnects, and the caller sees a null answer.
                operation.Reply?.TrySetResult(null);
                throw;
            }
        }
    }

    /// <summary>
    /// Writes one command and, when it is a query, reads its single line. Only a socket failure
    /// throws; a query the server ignores comes back null.
    /// </summary>
    private async Task<string?> AskAsync(
        NetworkStream stream, string command, CancellationToken token, bool expectReply = true)
    {
        // Anything already buffered arrived with no query outstanding — a late answer to a query
        // that timed out, or a server that volunteered a line — so it is not this query's reply.
        lock (_gate) _pendingBytes.Clear();

        await WriteAsync(stream, command, token).ConfigureAwait(false);
        CommandsSent++;

        if (!expectReply) return null;
        return await ReadLineAsync(stream, command, token).ConfigureAwait(false);
    }

    private static Task WriteAsync(NetworkStream stream, string command, CancellationToken token)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\r\n");
        return stream.WriteAsync(bytes, token).AsTask();
    }

    /// <summary>
    /// Reads one line, accepting either CRLF or a bare LF, and gives up after
    /// <see cref="QueryTimeout"/> with a null rather than an exception. The outstanding read is
    /// kept for the next call instead of being cancelled: cancelling a receive mid-flight is what
    /// would actually break the socket, and a reply that turns up late is dropped by the buffer
    /// clear in <see cref="AskAsync"/>.
    /// </summary>
    private async Task<string?> ReadLineAsync(NetworkStream stream, string command, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + QueryTimeout;

        while (true)
        {
            string? line = TakeLine();
            if (line is not null) return line;

            _pendingRead ??= stream.ReadAsync(_readBuffer, token).AsTask();

            var remaining = deadline - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                using var delay = CancellationTokenSource.CreateLinkedTokenSource(token);
                var finished = await Task.WhenAny(_pendingRead, Task.Delay(remaining, delay.Token))
                    .ConfigureAwait(false);
                delay.Cancel();
                if (finished != _pendingRead)
                {
                    NoteUnanswered(command);
                    return null;
                }
            }
            else if (!_pendingRead.IsCompleted)
            {
                NoteUnanswered(command);
                return null;
            }

            int read = await _pendingRead.ConfigureAwait(false);
            _pendingRead = null;
            if (read <= 0) throw new IOException("LiveSplit closed the connection");
            lock (_gate) _pendingBytes.AddRange(_readBuffer.AsSpan(0, read).ToArray());
        }
    }

    /// <summary>The first complete line in the buffer, CR trimmed, or null when there is none yet.</summary>
    private string? TakeLine()
    {
        lock (_gate)
        {
            int newline = _pendingBytes.IndexOf((byte)'\n');
            if (newline < 0) return null;

            var line = Encoding.ASCII.GetString(_pendingBytes.ToArray(), 0, newline).TrimEnd('\r');
            _pendingBytes.RemoveRange(0, newline + 1);
            return line;
        }
    }

    /// <summary>Remembers a command this server ignores, so it is asked once and never again.</summary>
    private void NoteUnanswered(string command)
    {
        bool first;
        lock (_gate) first = _unanswered.Add(command);
        if (first) LastNote = $"LiveSplit did not answer \"{command}\"; this build does not support it.";
    }

    /// <summary>
    /// Lets go of a read left over from a connection that has died. It is never awaited again, so
    /// its failure is observed here rather than left dangling on the finalizer thread.
    /// </summary>
    private void OrphanPendingRead()
    {
        var orphan = _pendingRead;
        _pendingRead = null;
        orphan?.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
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
