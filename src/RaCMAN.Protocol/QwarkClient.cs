using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RaCMAN.Protocol;

/// <summary>
/// The whole client side of PROTOCOL.md: one TCP connection with pipelined requests matched by
/// seq, a UDP telemetry receiver, an idle heartbeat and an auto-reconnect loop.
/// Nothing about the game is cached beyond the latest telemetry packet.
/// </summary>
public sealed class QwarkClient : IDisposable
{
    public const int DefaultPort = 9673;
    public const byte ClientProtocolVersion = 1;

    /// <summary>
    /// The qwark build this client was released beside. Unlike the protocol version it is not a
    /// wire contract: qwark bumps it whenever its feature tables change, so a console still running
    /// an older SPRX answers DESCRIBE with the old tables and the client quietly shows less than it
    /// should. Comparing it against HELLO is the only way to catch that.
    /// </summary>
    public const byte ExpectedQwarkBuild = 18;

    /// <summary>
    /// True when the console's module is older than the one shipped with this client. A newer
    /// build than expected is fine: the client is the side that is behind, and nothing breaks.
    /// </summary>
    public static bool IsStaleBuild(byte reported) => reported < ExpectedQwarkBuild;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<Frame>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// Serialises autosplit delivery: the UDP push, the gap fetch it may trigger and the safety
    /// poll all raise events through here, so <see cref="AutosplitEventReceived"/> only ever sees
    /// each sequence number once and always in order.
    /// </summary>
    private readonly SemaphoreSlim _autosplitGate = new(1, 1);

    private readonly object _gate = new();

    private Socket? _socket;
    private NetworkStream? _stream;
    private UdpClient? _udp;
    private CancellationTokenSource? _connectionCts;
    private Task? _reconnectTask;
    private CancellationTokenSource? _reconnectCts;

    private int _seq;
    private long _lastSendTicks;
    private volatile bool _transportUp;
    private volatile bool _wantConnection;
    private volatile TelemetryPacket? _latestTelemetry;
    private volatile SessionInfo? _latestSession;
    private long _lastTelemetryTicks;
    private volatile bool _telemetryViaTcp;

    /// <summary>
    /// When the announced quiet window runs out, on <see cref="Environment.TickCount64"/>. Zero
    /// when nothing has announced one.
    /// </summary>
    private long _quietUntilTicks;

    private long _reconnectAtTicks;
    private int _reconnectAttempt;
    private bool _disposed;

    private uint _lastAutosplitSeq;
    private volatile bool _autosplitPrimed;
    private volatile bool _autosplitAvailable = true;

    /// <summary>Fires after HELLO and SUBSCRIBE have succeeded, on connect and on every reconnect.</summary>
    public event Action<SessionInfo>? SessionEstablished;

    /// <summary>Fires once per lost connection, with the error that killed it when there was one.</summary>
    public event Action<Exception?>? Disconnected;

    public event Action<TelemetryPacket>? TelemetryReceived;

    /// <summary>
    /// One run event from the console, deduped by sequence number and raised in sequence order,
    /// however it arrived: the UDP push, the fetch that fills a gap the push exposed, or the
    /// safety poll. Events already in the ring when the connection opened are recorded, not
    /// raised, so a client never acts on a run that happened before it was watching.
    /// </summary>
    public event Action<AutosplitEvent>? AutosplitEventReceived;

    /// <summary>Attempt number and the delay before it, for the reconnect countdown.</summary>
    public event Action<int, TimeSpan>? Reconnecting;

    public string? Host { get; private set; }

    public int Port { get; private set; } = DefaultPort;

    public bool IsConnected => _transportUp;

    /// <summary>True while the client wants a connection: connected, or waiting to retry.</summary>
    public bool WantsConnection => _wantConnection;

    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// Awaited once before each automatic reconnect attempt, with the attempt number. This library
    /// has no idea what it does: whatever it throws is ignored and the attempt is made anyway, so a
    /// caller can put a step of its own in front of a retry (asking the console's plugin manager
    /// whether the module is still there, say) without this client knowing anything about it.
    /// Nothing calls it for the first connect, which is the caller's own to sequence.
    /// </summary>
    public Func<int, CancellationToken, Task>? BeforeReconnect { get; set; }

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TelemetryPacket? LatestTelemetry => _latestTelemetry;

    /// <summary>The newest SessionInfo seen, from telemetry, GET_STATE or HELLO.</summary>
    public SessionInfo? LatestSession => _latestSession;

    public int TelemetryPort { get; private set; }

    /// <summary>Milliseconds since the last telemetry snapshot (UDP or TCP), or -1 when not connected.</summary>
    public long TelemetryAgeMs =>
        _transportUp ? Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref _lastTelemetryTicks)) : -1;

    /// <summary>True when the latest snapshot came from the TCP GET_STATE fallback, i.e. UDP telemetry isn't arriving.</summary>
    public bool TelemetryViaTcp => _telemetryViaTcp;

    /// <summary>
    /// How long qwark's announced silence has left to run, or <see cref="TimeSpan.Zero"/> when
    /// there is none. A snapshot with <see cref="SessionInfo.IsQuiet"/> set carries the window in
    /// <see cref="SessionInfo.QuietMs"/> and this counts it down.
    /// </summary>
    public TimeSpan QuietRemaining
    {
        get
        {
            long until = Interlocked.Read(ref _quietUntilTicks);
            if (until == 0) return TimeSpan.Zero;
            long left = until - Environment.TickCount64;
            return left <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(left);
        }
    }

    /// <summary>
    /// True while qwark has asked for silence, i.e. the console is starting a game. Nothing is
    /// wrong: the connection is up, telemetry has stopped on purpose, and this client is not
    /// asking for any either until the window runs out.
    /// </summary>
    public bool TelemetryQuiet => QuietRemaining > TimeSpan.Zero;

    public int ReconnectAttempt => Volatile.Read(ref _reconnectAttempt);

    /// <summary>The newest autosplit sequence number this client has seen. 0 before the first one.</summary>
    public uint LastAutosplitSeq => Volatile.Read(ref _lastAutosplitSeq);

    /// <summary>
    /// False once the console has answered UNKNOWN_OP for AUTOSPLIT_EVENTS, i.e. it runs a module
    /// from before revision 1.4. The poll then stops rather than asking again every second.
    /// </summary>
    public bool AutosplitAvailable => _autosplitAvailable;

    /// <summary>
    /// The backstop for a lost UDP push: AUTOSPLIT_EVENTS at 1 Hz while a game is running, or at
    /// 10 Hz when telemetry is already coming over TCP, because that says UDP is being eaten. It
    /// only runs while something is subscribed to <see cref="AutosplitEventReceived"/>; a client
    /// with the autosplitter switched off turns it back off here and stops asking.
    /// </summary>
    public bool AutosplitSafetyPoll { get; set; } = true;

    /// <summary>How long until the next reconnect attempt; zero when not waiting.</summary>
    public TimeSpan ReconnectRemaining
    {
        get
        {
            long at = Interlocked.Read(ref _reconnectAtTicks);
            if (at == 0) return TimeSpan.Zero;
            long left = at - Environment.TickCount64;
            return left <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(left);
        }
    }

    // ---------------------------------------------------------------- lifecycle

    public async Task ConnectAsync(string host, int port = DefaultPort, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        StopReconnectLoop();
        await TeardownAsync(null, raiseEvent: false).ConfigureAwait(false);

        Host = host;
        Port = port;
        _wantConnection = true;

        try
        {
            await EstablishAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A failed first connect still arms the reconnect loop when the caller asked for it.
            if (_wantConnection && AutoReconnect) StartReconnectLoop();
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        _wantConnection = false;
        StopReconnectLoop();
        await TeardownAsync(null, raiseEvent: false).ConfigureAwait(false);
    }

    private async Task EstablishAsync(CancellationToken cancellationToken)
    {
        string host = Host ?? throw new InvalidOperationException("no host to connect to");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var cts = new CancellationTokenSource();

        try
        {
            await socket.ConnectAsync(host, Port, cancellationToken).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: false);

            lock (_gate)
            {
                _socket = socket;
                _stream = stream;
                _udp = udp;
                _connectionCts = cts;
                _transportUp = true;
            }

            TelemetryPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

            // Until the ring has been read once, a push carries a sequence number this client has
            // no baseline for; dropping those is what stops a reconnect replaying an old run.
            _autosplitPrimed = false;
            _autosplitAvailable = true;
            Volatile.Write(ref _lastAutosplitSeq, 0);

            Interlocked.Exchange(ref _lastSendTicks, Environment.TickCount64);
            // Start the age clock now; the poll loop waits this out before falling back to TCP.
            Interlocked.Exchange(ref _lastTelemetryTicks, Environment.TickCount64);
            Interlocked.Exchange(ref _quietUntilTicks, 0);
            _telemetryViaTcp = false;

            _ = Task.Run(() => ReceiveLoopAsync(stream, cts.Token));
            _ = Task.Run(() => TelemetryLoopAsync(udp, cts.Token));
            _ = Task.Run(() => HeartbeatLoopAsync(cts.Token));
            _ = Task.Run(() => StatePollLoopAsync(cts.Token));

            var info = await HelloAsync(cancellationToken).ConfigureAwait(false);
            await SubscribeAsync((ushort)TelemetryPort, cancellationToken).ConfigureAwait(false);
            await PrimeAutosplitAsync(cancellationToken).ConfigureAwait(false);
            _ = Task.Run(() => AutosplitPollLoopAsync(cts.Token));

            Interlocked.Exchange(ref _reconnectAtTicks, 0);
            Volatile.Write(ref _reconnectAttempt, 0);
            SessionEstablished?.Invoke(info);
        }
        catch
        {
            cts.Cancel();
            socket.Dispose();
            udp.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                    _stream = null;
                    _udp = null;
                    _connectionCts = null;
                    _transportUp = false;
                }
            }

            throw;
        }
    }

    private Task TeardownAsync(Exception? error, bool raiseEvent)
    {
        Socket? socket;
        NetworkStream? stream;
        UdpClient? udp;
        CancellationTokenSource? cts;
        bool wasUp;

        lock (_gate)
        {
            socket = _socket;
            stream = _stream;
            udp = _udp;
            cts = _connectionCts;
            wasUp = _transportUp;

            _socket = null;
            _stream = null;
            _udp = null;
            _connectionCts = null;
            _transportUp = false;
        }

        try { cts?.Cancel(); } catch { /* already gone */ }
        try { stream?.Dispose(); } catch { /* already gone */ }
        try { socket?.Dispose(); } catch { /* already gone */ }
        try { udp?.Dispose(); } catch { /* already gone */ }
        cts?.Dispose();

        var failure = error ?? new IOException("connection closed");
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(failure);
        }

        // Nothing about the console survives the connection: the client re-reads everything on
        // the next HELLO, so a stale session block must not keep colouring the UI meanwhile.
        _latestTelemetry = null;
        _latestSession = null;
        Interlocked.Exchange(ref _quietUntilTicks, 0);

        if (wasUp && raiseEvent) Disconnected?.Invoke(error);
        return Task.CompletedTask;
    }

    private void HandleConnectionLost(Exception? error)
    {
        if (!_transportUp) return;

        _ = TeardownAsync(error, raiseEvent: true);

        if (_wantConnection && AutoReconnect) StartReconnectLoop();
    }

    private void StartReconnectLoop()
    {
        lock (_gate)
        {
            if (_reconnectTask is { IsCompleted: false }) return;
            _reconnectCts?.Dispose();
            _reconnectCts = new CancellationTokenSource();
            var token = _reconnectCts.Token;
            _reconnectTask = Task.Run(() => ReconnectLoopAsync(token), token);
        }
    }

    private void StopReconnectLoop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _reconnectCts;
            _reconnectCts = null;
            _reconnectTask = null;
        }

        try { cts?.Cancel(); } catch { /* nothing to do */ }
        cts?.Dispose();
        Interlocked.Exchange(ref _reconnectAtTicks, 0);
    }

    private async Task ReconnectLoopAsync(CancellationToken token)
    {
        int attempt = 0;
        while (!token.IsCancellationRequested && _wantConnection && AutoReconnect && !_transportUp)
        {
            attempt++;
            Volatile.Write(ref _reconnectAttempt, attempt);

            // 1 s, 2 s, 4 s, 8 s, then capped at 10 s.
            int seconds = attempt >= 5 ? 10 : 1 << (attempt - 1);
            var delay = TimeSpan.FromSeconds(Math.Min(seconds, 10));
            Interlocked.Exchange(ref _reconnectAtTicks, Environment.TickCount64 + (long)delay.TotalMilliseconds);
            Reconnecting?.Invoke(attempt, delay);

            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested || !_wantConnection) return;

            if (BeforeReconnect is { } before)
            {
                try
                {
                    await before(attempt, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // A hook that failed has nothing to say about whether the console is there;
                    // the attempt below is what decides, exactly as it would without one.
                }

                if (token.IsCancellationRequested || !_wantConnection) return;
            }

            try
            {
                await EstablishAsync(token).ConfigureAwait(false);
                Interlocked.Exchange(ref _reconnectAtTicks, 0);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Keep trying; the countdown restarts with a longer delay.
            }
        }
    }

    // ---------------------------------------------------------------- loops

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[Frame.HeaderSize];
        try
        {
            while (!token.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
                var (length, seq, code) = Frame.DecodeHeader(header);

                var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
                if (length > 0) await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);

                var frame = new Frame(seq, code, payload);
                if (_pending.TryRemove(seq, out var tcs)) tcs.TrySetResult(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // Torn down deliberately.
        }
        catch (ObjectDisposedException)
        {
            // Torn down deliberately.
        }
        catch (Exception ex)
        {
            HandleConnectionLost(ex);
        }
    }

    // Diagnostic: RACMAN_DROP_UDP=1 receives UDP telemetry but discards it, simulating a firewall
    // that eats the console's packets, so the TCP GET_STATE fallback can be exercised on localhost.
    private static readonly bool DropUdp =
        Environment.GetEnvironmentVariable("RACMAN_DROP_UDP") == "1";

    private async Task TelemetryLoopAsync(UdpClient udp, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(token).ConfigureAwait(false);
                if (DropUdp) continue;

                // The autosplit push shares this socket with telemetry, so it is picked off by its
                // magic and its length before the telemetry parser ever sees the bytes.
                if (AutosplitDatagram.Matches(result.Buffer))
                {
                    if (AutosplitDatagram.TryParse(result.Buffer, out var pushed))
                    {
                        await DeliverAutosplitAsync(pushed, token).ConfigureAwait(false);
                    }

                    continue;
                }

                TelemetryPacket packet;
                try
                {
                    packet = TelemetryPacket.Parse(result.Buffer);
                }
                catch (ProtocolException)
                {
                    continue;
                }

                _latestTelemetry = packet;
                _latestSession = packet.Session;
                NoteQuietWindow(packet.Session);
                Interlocked.Exchange(ref _lastTelemetryTicks, Environment.TickCount64);
                _telemetryViaTcp = false;
                TelemetryReceived?.Invoke(packet);
            }
        }
        catch (OperationCanceledException)
        {
            // Torn down deliberately.
        }
        catch (ObjectDisposedException)
        {
            // Torn down deliberately.
        }
        catch (SocketException)
        {
            // A UDP failure is not fatal to the command connection.
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                long idle = Environment.TickCount64 - Interlocked.Read(ref _lastSendTicks);
                if (idle < HeartbeatInterval.TotalMilliseconds) continue;

                // "Completely quiet" has to mean this too. The heartbeat is a TCP round trip, the
                // same cost as the poll above, and the connection does not need it for the few
                // seconds a game takes to start: nothing here is what tells us the link is alive.
                long age = Environment.TickCount64 - Interlocked.Read(ref _lastTelemetryTicks);
                if (ShouldStayQuiet(age)) continue;

                try
                {
                    await HeartbeatAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // The receive loop reports the disconnect; nothing to do here.
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Torn down deliberately.
        }
    }

    // ---------------------------------------------------------------- request plumbing

    private ushort NextSeq()
    {
        int next = Interlocked.Increment(ref _seq);
        return (ushort)(next & 0xFFFF);
    }

    private async Task<Frame> ExchangeAsync(Opcode opcode, byte[]? payload, CancellationToken cancellationToken)
    {
        var stream = _stream;
        if (stream is null || !_transportUp) throw new InvalidOperationException("not connected to qwark");

        ushort seq = NextSeq();
        var tcs = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(seq, tcs)) throw new InvalidOperationException($"sequence number {seq} is still in flight");

        try
        {
            var bytes = Frame.Request(opcode, seq, payload).Encode();

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastSendTicks, Environment.TickCount64);
            }
            finally
            {
                _sendLock.Release();
            }

            return await tcs.Task.WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(seq, out _);
            if (ex is IOException or SocketException) HandleConnectionLost(ex);
            throw;
        }
    }

    /// <summary>Sends one request and throws <see cref="QwarkStatusException"/> on any non-OK status.</summary>
    public async Task<byte[]> RequestAsync(Opcode opcode, byte[]? payload = null, CancellationToken cancellationToken = default)
    {
        var reply = await ExchangeAsync(opcode, payload, cancellationToken).ConfigureAwait(false);
        if (reply.Status != Status.Ok) throw new QwarkStatusException(opcode, reply.Status);
        return reply.Payload;
    }

    private delegate void PayloadFill(scoped ref SpanWriter writer);

    private static byte[] Bytes(int size, PayloadFill fill)
    {
        var buffer = new byte[size];
        var writer = new SpanWriter(buffer);
        fill(ref writer);
        return buffer;
    }

    private static byte[] Path(string path, byte? mode = null)
    {
        var encoded = Encoding.UTF8.GetBytes(path);
        if (encoded.Length > 511) throw new ArgumentException("paths are at most 511 bytes", nameof(path));
        if (mode is null) return encoded;

        var buffer = new byte[1 + encoded.Length];
        buffer[0] = mode.Value;
        encoded.CopyTo(buffer, 1);
        return buffer;
    }

    private static string[] ParseNameList(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        int count = r.ReadU8();
        var names = new string[count];
        for (int i = 0; i < count; i++) names[i] = r.ReadFixedString(24);
        return names;
    }

    private static byte[] ParseLengthPrefixed(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        int length = r.ReadU16();
        return r.ReadBytes(length);
    }

    /// <summary>
    /// Takes qwark at its word about the silence it is about to keep (revision 1.11). The bit is
    /// set for as long as the session is BOOTING, and the field is how much of the window is left,
    /// so every snapshot that carries it simply restates the deadline; a snapshot without it means
    /// the game is up and the window is over, however much of it was left.
    /// <para>
    /// A GET_STATE reply is the same bytes as a UDP packet, so a poll that happens to land inside
    /// the window is what teaches a client that missed the announcement to stop polling too.
    /// </para>
    /// </summary>
    private void NoteQuietWindow(SessionInfo session) =>
        Interlocked.Exchange(ref _quietUntilTicks,
            session.IsQuiet ? Environment.TickCount64 + session.QuietMs : 0);

    /// <summary>
    /// How long silence is read as a game starting before the client gives up on that reading and
    /// asks. Longer than any boot window qwark sets by default, and short enough that a console
    /// which has genuinely stopped talking is noticed in a few breaths rather than never.
    /// </summary>
    private const long QuietGraceMs = 20_000;

    /// <summary>
    /// Whether this client should be silent right now, given how old the last snapshot is.
    /// <para>
    /// Two reasons to be. One is a window qwark named, in a block that carried the quiet flag. The
    /// other is inference, and it is the one that matters in practice: qwark sends nothing at all
    /// while a game is starting, so the announcement it used to send would have been one of the
    /// packets the silence exists to avoid. What the client has instead is the last state it saw.
    /// Telemetry stopping while the console was in the XMB is a game being started; telemetry
    /// stopping while a game was running is UDP going missing, which is what the fallback is for.
    /// </para>
    /// </summary>
    private bool ShouldStayQuiet(long ageMs)
    {
        if (TelemetryQuiet) return true;

        var state = _latestSession?.State;
        if (state is null || state == SessionState.Ingame) return false;

        return ageMs < QuietGraceMs;
    }

    private static uint ParseU32(ReadOnlySpan<byte> payload) => new SpanReader(payload).ReadU32();

    private static byte[] DirName(string dirname)
    {
        return Bytes(32, (scoped ref SpanWriter w) => w.WriteFixedString(dirname, 32));
    }

    // ---------------------------------------------------------------- 5.1 session

    public async Task<SessionInfo> HelloAsync(CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(Opcode.Hello, new[] { ClientProtocolVersion }, cancellationToken).ConfigureAwait(false);
        var info = SessionInfo.Parse(payload);
        _latestSession = info;
        NoteQuietWindow(info);
        return info;
    }

    public Task HeartbeatAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.Heartbeat, null, cancellationToken);

    public Task NotifyAsync(string text, CancellationToken cancellationToken = default)
    {
        var encoded = Encoding.UTF8.GetBytes(text);
        if (encoded.Length > 255) encoded = encoded[..255];
        return RequestAsync(Opcode.Notify, encoded, cancellationToken);
    }

    public async Task<PreviousSession> PreviousListAsync(CancellationToken cancellationToken = default) =>
        PreviousSession.Parse(await RequestAsync(Opcode.PreviousList, null, cancellationToken).ConfigureAwait(false));

    public Task PreviousReapplyAsync(PreviousCategories categories, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.PreviousReapply, new[] { (byte)categories }, cancellationToken);

    public Task PreviousDismissAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.PreviousDismiss, null, cancellationToken);

    // ---------------------------------------------------------------- 5.2 telemetry

    public Task SubscribeAsync(ushort udpPort, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.Subscribe, Bytes(2, (scoped ref SpanWriter w) => w.WriteU16(udpPort)), cancellationToken);

    public Task UnsubscribeAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.Unsubscribe, null, cancellationToken);

    public async Task<TelemetryPacket> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(Opcode.GetState, null, cancellationToken).ConfigureAwait(false);
        var packet = TelemetryPacket.Parse(payload);
        _latestTelemetry = packet;
        _latestSession = packet.Session;
        NoteQuietWindow(packet.Session);
        Interlocked.Exchange(ref _lastTelemetryTicks, Environment.TickCount64);
        _telemetryViaTcp = true;
        TelemetryReceived?.Invoke(packet);
        return packet;
    }

    /// <summary>
    /// Keeps the UI live when UDP telemetry isn't arriving. While the console's UDP packets are
    /// flowing this stays idle; when they aren't (a firewall or NAT eating them) it pulls the same
    /// snapshot over TCP with GET_STATE at ~10 Hz, so readouts, toggle state, the selected slot and
    /// the pad mask still update. GET_STATE returns the identical bytes as the UDP packet.
    /// <para>
    /// The one silence it must not fill is the one qwark announced. While a game is starting the
    /// module stops sending on purpose, and a fallback that reads that as a lost packet would put
    /// ten requests a second on it at exactly the moment it has nothing to spare - which is what
    /// crashed the console. The window is waited out and the fallback resumes after it.
    /// </para>
    /// </summary>
    private async Task StatePollLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                long age = Environment.TickCount64 - Interlocked.Read(ref _lastTelemetryTicks);
                if (_transportUp && age > 400 && !ShouldStayQuiet(age))
                {
                    try
                    {
                        await GetStateAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // A transport failure is handled by the receive loop; just back off here.
                    }

                    await Task.Delay(100, token).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Torn down deliberately.
        }
    }

    // ---------------------------------------------------------------- 5.3 features

    public async Task<DescribeResult> DescribeAsync(CancellationToken cancellationToken = default) =>
        DescribeResult.Parse(await RequestAsync(Opcode.Describe, null, cancellationToken).ConfigureAwait(false));

    public Task FeatureSetAsync(byte id, uint value, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.FeatureSet, Bytes(5, (scoped ref SpanWriter w) => { w.WriteU8(id); w.WriteU32(value); }), cancellationToken);

    public Task FeatureTriggerAsync(byte id, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.FeatureTrigger, new[] { id }, cancellationToken);

    public Task FeatureSetAutoAsync(byte id, bool auto, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.FeatureSetAuto, new[] { id, auto ? (byte)1 : (byte)0 }, cancellationToken);

    public async Task<string[]> FeatureOptionsAsync(byte id, CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(Opcode.FeatureOptions, new[] { id }, cancellationToken).ConfigureAwait(false);
        return ParseNameList(payload);
    }

    // ---------------------------------------------------------------- 5.4 memory

    public Task<byte[]> MemReadAsync(uint address, uint length, CancellationToken cancellationToken = default)
    {
        if (length > 65536) throw new ArgumentOutOfRangeException(nameof(length), "MEM_READ is capped at 65536 bytes");
        return RequestAsync(Opcode.MemRead, Bytes(8, (scoped ref SpanWriter w) => { w.WriteU32(address); w.WriteU32(length); }), cancellationToken);
    }

    public Task MemWriteAsync(uint address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length > 65536) throw new ArgumentOutOfRangeException(nameof(data), "MEM_WRITE is capped at 65536 bytes");
        var payload = new byte[4 + data.Length];
        var w = new SpanWriter(payload);
        w.WriteU32(address);
        w.WriteBytes(data.Span);
        return RequestAsync(Opcode.MemWrite, payload, cancellationToken);
    }

    public async Task<byte> WatchAddAsync(uint address, byte size, CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(Opcode.WatchAdd, Bytes(5, (scoped ref SpanWriter w) => { w.WriteU32(address); w.WriteU8(size); }), cancellationToken).ConfigureAwait(false);
        return payload.Length > 0 ? payload[0] : throw new ProtocolException("WATCH_ADD returned no id");
    }

    public Task WatchRemoveAsync(byte id, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.WatchRemove, new[] { id }, cancellationToken);

    public async Task<WatchEntry[]> WatchListAsync(CancellationToken cancellationToken = default) =>
        WatchEntry.ParseList(await RequestAsync(Opcode.WatchList, null, cancellationToken).ConfigureAwait(false));

    public async Task<byte> FreezeAddAsync(uint address, byte size, ulong value, CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(Opcode.FreezeAdd, Bytes(16, (scoped ref SpanWriter w) =>
        {
            w.WriteU32(address);
            w.WriteU8(size);
            w.WriteZeros(3);
            w.WriteU64(value);
        }), cancellationToken).ConfigureAwait(false);
        return payload.Length > 0 ? payload[0] : throw new ProtocolException("FREEZE_ADD returned no id");
    }

    public Task FreezeRemoveAsync(byte id, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.FreezeRemove, new[] { id }, cancellationToken);

    public async Task<FreezeEntry[]> FreezeListAsync(CancellationToken cancellationToken = default) =>
        FreezeEntry.ParseList(await RequestAsync(Opcode.FreezeList, null, cancellationToken).ConfigureAwait(false));

    public Task PatchApplyAsync(IReadOnlyList<PatchWord> words, CancellationToken cancellationToken = default)
    {
        if (words.Count is 0 or > 64) throw new ArgumentOutOfRangeException(nameof(words), "a client patch is 1 to 64 words");
        return RequestAsync(Opcode.PatchApply, Bytes(4 + words.Count * 8, (scoped ref SpanWriter w) =>
        {
            w.WriteU16((ushort)words.Count);
            w.WriteU16(0);
            foreach (var word in words)
            {
                w.WriteU32(word.Address);
                w.WriteU32(word.Word);
            }
        }), cancellationToken);
    }

    public Task PatchRevertAsync(uint firstAddress, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.PatchRevert, Bytes(4, (scoped ref SpanWriter w) => w.WriteU32(firstAddress)), cancellationToken);

    public async Task<PatchEntry[]> PatchListAsync(CancellationToken cancellationToken = default) =>
        PatchEntry.ParseList(await RequestAsync(Opcode.PatchList, null, cancellationToken).ConfigureAwait(false));

    public Task ClearClientAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.ClearClient, null, cancellationToken);

    // ---------------------------------------------------------------- 5.5 positions and planets

    public const byte Selected = 0xFF;

    public Task PosSelectAsync(byte slot, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.PosSelect, new[] { slot }, cancellationToken);

    public Task PosSaveAsync(byte slot = Selected, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.PosSave, new[] { slot }, cancellationToken);

    public Task PosLoadAsync(byte slot = Selected, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.PosLoad, new[] { slot }, cancellationToken);

    public async Task<PositionList> PosListAsync(CancellationToken cancellationToken = default) =>
        PositionList.Parse(await RequestAsync(Opcode.PosList, null, cancellationToken).ConfigureAwait(false));

    public Task PosClearAsync(byte slot, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.PosClear, new[] { slot }, cancellationToken);

    public async Task<string[]> PlanetListAsync(CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(Opcode.PlanetList, null, cancellationToken).ConfigureAwait(false);
        return ParseNameList(payload);
    }

    public Task PlanetSelectAsync(byte planet, PlanetFlags flags, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.PlanetSelect, new[] { planet, (byte)flags }, cancellationToken);

    public Task PlanetLoadAsync(byte planet = Selected, byte flags = Selected, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.PlanetLoad, new[] { planet, flags }, cancellationToken);

    public Task DieAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.Die, null, cancellationToken);

    public async Task<MobyTableInfo> MobyTableAsync(CancellationToken cancellationToken = default) =>
        MobyTableInfo.Parse(await RequestAsync(Opcode.MobyTable, null, cancellationToken).ConfigureAwait(false));

    // ---------------------------------------------------------------- 5.6 unlocks and level flags

    public async Task<UnlockList> UnlockListAsync(CancellationToken cancellationToken = default) =>
        UnlockList.Parse(await RequestAsync(Opcode.UnlockList, null, cancellationToken).ConfigureAwait(false));

    public Task UnlockSetAsync(byte id, byte field, uint value, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.UnlockSet, Bytes(8, (scoped ref SpanWriter w) =>
        {
            w.WriteU8(id);
            w.WriteU8(field);
            w.WriteU16(0);
            w.WriteU32(value);
        }), cancellationToken);

    public async Task<byte[]> LevelFlagsGetAsync(byte planet, CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(Opcode.LevelFlagsGet, new[] { planet }, cancellationToken).ConfigureAwait(false);
        return ParseLengthPrefixed(payload);
    }

    public Task LevelFlagsResetAsync(byte planet, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.LevelFlagsReset, new[] { planet }, cancellationToken);

    /// <summary>
    /// Writes one byte at <paramref name="offset"/> into the concatenated region LEVELFLAGS_GET
    /// returns for that planet. Payload is `u8 planet, u8 value, u16 offset`, section 5.6.
    /// </summary>
    public Task LevelFlagsSetAsync(byte planet, ushort offset, byte value, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.LevelFlagsSet, Bytes(4, (scoped ref SpanWriter w) =>
        {
            w.WriteU8(planet);
            w.WriteU8(value);
            w.WriteU16(offset);
        }), cancellationToken);

    // ---------------------------------------------------------------- 5.7 mods

    public async Task<ModEntry[]> ModListAsync(CancellationToken cancellationToken = default) =>
        ModEntry.ParseList(await RequestAsync(Opcode.ModList, null, cancellationToken).ConfigureAwait(false));

    public Task ModLoadAsync(string dirname, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.ModLoad, DirName(dirname), cancellationToken);

    public Task ModUnloadAsync(string dirname, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.ModUnload, DirName(dirname), cancellationToken);

    public Task ModSetAutoAsync(string dirname, bool auto, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.ModSetAuto, Bytes(33, (scoped ref SpanWriter w) =>
        {
            w.WriteFixedString(dirname, 32);
            w.WriteU8(auto ? (byte)1 : (byte)0);
        }), cancellationToken);

    public Task ModRescanAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.ModRescan, null, cancellationToken);

    public async Task<string> ModInfoAsync(string dirname, CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(Opcode.ModInfo, DirName(dirname), cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload).TrimEnd('\0');
    }

    // ---------------------------------------------------------------- 5.8 files

    public async Task<uint> FileOpenAsync(string path, FileMode mode, CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(Opcode.FileOpen, Path(path, (byte)mode), cancellationToken).ConfigureAwait(false);
        return ParseU32(payload);
    }

    public Task FileWriteAsync(uint handle, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length > 65536) throw new ArgumentOutOfRangeException(nameof(data), "FILE_WRITE is capped at 65536 bytes");
        var payload = new byte[4 + data.Length];
        var w = new SpanWriter(payload);
        w.WriteU32(handle);
        w.WriteBytes(data.Span);
        return RequestAsync(Opcode.FileWrite, payload, cancellationToken);
    }

    public Task<byte[]> FileReadAsync(uint handle, uint length, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.FileRead, Bytes(8, (scoped ref SpanWriter w) => { w.WriteU32(handle); w.WriteU32(length); }), cancellationToken);

    public Task FileCloseAsync(uint handle, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.FileClose, Bytes(4, (scoped ref SpanWriter w) => w.WriteU32(handle)), cancellationToken);

    public Task FileDeleteAsync(string path, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.FileDelete, Path(path), cancellationToken);

    public async Task<DirEntry[]> DirListAsync(string path, CancellationToken cancellationToken = default) =>
        DirEntry.ParseList(await RequestAsync(Opcode.DirList, Path(path), cancellationToken).ConfigureAwait(false));

    public Task DirCreateAsync(string path, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.DirCreate, Path(path), cancellationToken);

    public Task DirDeleteAsync(string path, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.DirDelete, Path(path), cancellationToken);

    /// <summary>
    /// FILE_RENAME, revision 1.10: <c>u16 from_len</c>, the old path, then the new one as the rest
    /// of the payload. The console refuses a destination that already exists, so a caller that
    /// means to replace a file deletes it first.
    /// </summary>
    public Task FileRenameAsync(string from, string to, CancellationToken cancellationToken = default)
    {
        var fromBytes = Encoding.UTF8.GetBytes(from);
        var toBytes = Encoding.UTF8.GetBytes(to);
        if (fromBytes.Length == 0 || fromBytes.Length > 511) throw new ArgumentOutOfRangeException(nameof(from));
        if (toBytes.Length == 0 || toBytes.Length > 511) throw new ArgumentOutOfRangeException(nameof(to));

        var payload = new byte[2 + fromBytes.Length + toBytes.Length];
        var w = new SpanWriter(payload);
        w.WriteU16((ushort)fromBytes.Length);
        w.WriteBytes(fromBytes);
        w.WriteBytes(toBytes);
        return RequestAsync(Opcode.FileRename, payload, cancellationToken);
    }

    public async Task<uint> UserIdAsync(CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(Opcode.UserId, null, cancellationToken).ConfigureAwait(false);
        return ParseU32(payload);
    }

    /// <summary>The chunk both file helpers use. FILE_READ and FILE_WRITE cap at 65536 bytes.</summary>
    public const int FileChunkSize = 65536;

    /// <summary>
    /// FILE_OPEN read, FILE_READ in 64 KB chunks until a short read says end of file, FILE_CLOSE.
    /// <paramref name="progress"/> is told the running byte count after every chunk.
    /// </summary>
    public async Task<byte[]> ReadFileAsync(
        string path,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        uint handle = await FileOpenAsync(path, FileMode.Read, cancellationToken).ConfigureAwait(false);
        try
        {
            using var buffer = new MemoryStream();
            while (true)
            {
                var chunk = await FileReadAsync(handle, FileChunkSize, cancellationToken).ConfigureAwait(false);
                if (chunk.Length > 0) buffer.Write(chunk, 0, chunk.Length);
                progress?.Report(buffer.Length);

                // A read shorter than what was asked for is end of file, section 5.8.
                if (chunk.Length < FileChunkSize) break;
            }

            return buffer.ToArray();
        }
        finally
        {
            // The handle must go back even when the read failed, so this one never carries the
            // caller's token: a cancelled read would otherwise leak the console-side handle.
            await FileCloseAsync(handle, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>FILE_OPEN write-truncate, FILE_WRITE in 64 KB chunks, FILE_CLOSE.</summary>
    public async Task WriteFileAsync(
        string path,
        ReadOnlyMemory<byte> data,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        uint handle = await FileOpenAsync(path, FileMode.WriteTruncate, cancellationToken).ConfigureAwait(false);
        try
        {
            for (int offset = 0; offset < data.Length; offset += FileChunkSize)
            {
                int length = Math.Min(FileChunkSize, data.Length - offset);
                await FileWriteAsync(handle, data.Slice(offset, length), cancellationToken).ConfigureAwait(false);
                progress?.Report(offset + length);
            }

            if (data.Length == 0) progress?.Report(0);
        }
        finally
        {
            await FileCloseAsync(handle, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- 5.12 save files

    /// <summary>
    /// The chunk the savefile helpers move. SAVEFILE_READ and SAVEFILE_WRITE cap at 65536 bytes,
    /// the same cap the file ops have.
    /// </summary>
    public const int SaveFileChunkSize = 65536;

    /// <summary>
    /// SAVEFILE_INFO, revision 1.9. Answers UNSUPPORTED where code cannot be patched (RPCS3) and
    /// NOT_INGAME outside a game; a game qwark simply has no helper for is an OK answer with
    /// <see cref="SaveFileInfo.Supported"/> false.
    /// </summary>
    public async Task<SaveFileInfo> SaveFileInfoAsync(CancellationToken cancellationToken = default) =>
        SaveFileInfo.Parse(await RequestAsync(Opcode.SaveFileInfo, null, cancellationToken).ConfigureAwait(false));

    public Task<byte[]> SaveFileReadAsync(uint offset, uint length, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.SaveFileRead,
            Bytes(8, (scoped ref SpanWriter w) => { w.WriteU32(offset); w.WriteU32(length); }),
            cancellationToken);

    public Task SaveFileWriteAsync(uint offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length == 0 || data.Length > SaveFileChunkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(data),
                $"SAVEFILE_WRITE takes 1 to {SaveFileChunkSize} bytes");
        }

        var payload = new byte[4 + data.Length];
        var w = new SpanWriter(payload);
        w.WriteU32(offset);
        w.WriteBytes(data.Span);
        return RequestAsync(Opcode.SaveFileWrite, payload, cancellationToken);
    }

    /// <summary>
    /// Reads the whole aside buffer in 64 KB chunks. <paramref name="size"/> comes from
    /// SAVEFILE_INFO; the console trims the last chunk itself, so the loop asks for a round
    /// chunk every time and stops when it has the lot.
    /// <para>
    /// Every chunk is awaited before the next is asked for, and a reply that is empty or longer
    /// than what was asked for throws rather than being pieced into the buffer: the caller gets
    /// all of the save or an exception, never a partly filled array.
    /// </para>
    /// </summary>
    public async Task<byte[]> SaveFileDownloadAsync(
        uint size,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[size];
        uint offset = 0;

        while (offset < size)
        {
            uint want = Math.Min((uint)SaveFileChunkSize, size - offset);
            var chunk = await SaveFileReadAsync(offset, want, cancellationToken).ConfigureAwait(false);
            if (chunk.Length == 0)
            {
                throw new ProtocolException($"SAVEFILE_READ returned nothing at offset {offset}");
            }

            if (chunk.Length > want)
            {
                throw new ProtocolException(
                    $"SAVEFILE_READ returned {chunk.Length} bytes at offset {offset}, {want} were asked for");
            }

            chunk.CopyTo(buffer, (int)offset);
            offset += (uint)chunk.Length;
            progress?.Report(offset);
        }

        return buffer;
    }

    /// <summary>
    /// Writes a whole save into the aside buffer in 64 KB chunks, from offset 0, strictly in
    /// order: each write is answered before the next one is sent, so the console's buffer is
    /// filled front to back and the caller knows the whole of it is there when this returns.
    /// </summary>
    public async Task SaveFileUploadAsync(
        ReadOnlyMemory<byte> data,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        for (int offset = 0; offset < data.Length; offset += SaveFileChunkSize)
        {
            int length = Math.Min(SaveFileChunkSize, data.Length - offset);
            await SaveFileWriteAsync((uint)offset, data.Slice(offset, length), cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(offset + length);
        }
    }

    // ------------------------------------------- 5.13 the library on the console

    /// <summary>
    /// Where the console keeps its savefile library. The client builds these paths itself for
    /// FILE_OPEN, FILE_DELETE and FILE_RENAME; the layout is section 5.13's, not a guess.
    /// </summary>
    public const string SaveFileConsoleRoot = "/dev_hdd0/qwark/savefiles";

    /// <summary>The suffix the console keeps a file's CRC32 in, beside the file.</summary>
    public const string SaveFileSumExtension = ".sum";

    public static string SaveFileConsoleFolder(string titleId, string category) =>
        $"{SaveFileConsoleRoot}/{titleId}/{category}";

    public static string SaveFileConsolePath(string titleId, string category, string name) =>
        $"{SaveFileConsoleFolder(titleId, category)}/{name}";

    /// <summary>SAVEFILE_CATEGORIES: the folders under the running game's title.</summary>
    public async Task<string[]> SaveFileCategoriesAsync(CancellationToken cancellationToken = default) =>
        ParseFixedStringList(
            await RequestAsync(Opcode.SaveFileCategories, null, cancellationToken).ConfigureAwait(false),
            ConsoleSaveFile.NameLength);

    /// <summary>SAVEFILE_LIST: every <c>.sav</c> in a category, with its size and its CRC32.</summary>
    public async Task<ConsoleSaveFile[]> SaveFileListAsync(string category, CancellationToken cancellationToken = default) =>
        ConsoleSaveFile.ParseList(
            await RequestAsync(Opcode.SaveFileList, FixedName(category), cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// SAVEFILE_STORE: the console raises the set-aside itself, waits for the helper and copies
    /// the aside buffer into its own file. Poll SAVEFILE_INFO for the progress and the outcome.
    /// </summary>
    public Task SaveFileStoreAsync(string category, string name, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.SaveFileStore, FixedPair(category, name), cancellationToken);

    /// <summary>
    /// SAVEFILE_RESTORE: the console copies its own file into the aside buffer and only then asks
    /// the game to take it. Poll SAVEFILE_INFO the same way.
    /// </summary>
    public Task SaveFileRestoreAsync(string category, string name, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.SaveFileRestore, FixedPair(category, name), cancellationToken);

    public Task SaveFileCategoryAsync(SaveFileCategoryOp op, string name, CancellationToken cancellationToken = default)
    {
        var payload = new byte[1 + ConsoleSaveFile.NameLength];
        var w = new SpanWriter(payload);
        w.WriteU8((byte)op);
        w.WriteFixedString(name, ConsoleSaveFile.NameLength);
        return RequestAsync(Opcode.SaveFileCategory, payload, cancellationToken);
    }

    private static byte[] FixedName(string name) =>
        Bytes(ConsoleSaveFile.NameLength,
            (scoped ref SpanWriter w) => w.WriteFixedString(name, ConsoleSaveFile.NameLength));

    private static byte[] FixedPair(string first, string second) =>
        Bytes(2 * ConsoleSaveFile.NameLength, (scoped ref SpanWriter w) =>
        {
            w.WriteFixedString(first, ConsoleSaveFile.NameLength);
            w.WriteFixedString(second, ConsoleSaveFile.NameLength);
        });

    /// <summary>A <c>u8 n</c> followed by n fixed-width names, the shape half of section 5 uses.</summary>
    private static string[] ParseFixedStringList(ReadOnlySpan<byte> payload, int width)
    {
        if (payload.Length < 1) return Array.Empty<string>();

        int count = payload[0];
        var names = new List<string>(count);
        var r = new SpanReader(payload[1..]);
        for (int i = 0; i < count && payload.Length >= 1 + (i + 1) * width; i++)
        {
            names.Add(r.ReadFixedString(width));
        }

        return names.ToArray();
    }

    // ---------------------------------------------------------------- 5.9 combos

    public Task ComboSetAsync(ComboAction action, uint mask, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.ComboSet, Bytes(8, (scoped ref SpanWriter w) =>
        {
            w.WriteU8((byte)action);
            w.WriteZeros(3);
            w.WriteU32(mask);
        }), cancellationToken);

    public async Task<ComboEntry[]> ComboListAsync(CancellationToken cancellationToken = default) =>
        ComboEntry.ParseList(await RequestAsync(Opcode.ComboList, null, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// COMBO_SUSPEND, revision 1.8: holds every combo the console has off, or hands them back.
    /// Capture reads the pad out of telemetry, and the console is watching that same pad, so
    /// without the hold the buttons being recorded also fire whatever is already stored there.
    /// <para>
    /// The console expires a hold on its own two minutes after it was set, so a client that dies
    /// mid-capture cannot leave the combos off; the panel still sends the 0 when it is done.
    /// </para>
    /// </summary>
    public Task ComboSuspendAsync(bool suspend, CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.ComboSuspend, Bytes(1, (scoped ref SpanWriter w) =>
        {
            w.WriteU8((byte)(suspend ? 1 : 0));
        }), cancellationToken);

    // ---------------------------------------------------------------- 5.10 config

    public Task ConfigReloadAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.ConfigReload, null, cancellationToken);

    public Task ConfigSaveAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Opcode.ConfigSave, null, cancellationToken);

    // ---------------------------------------------------------------- 5.11 autosplitting

    /// <summary>
    /// AUTOSPLIT_EVENTS: every event the console has emitted with a sequence number above
    /// <paramref name="sinceSeq"/>, oldest first, at most 64. Zero asks for whatever the ring holds.
    /// </summary>
    public async Task<AutosplitEventsReply> AutosplitEventsAsync(
        uint sinceSeq,
        CancellationToken cancellationToken = default)
    {
        var payload = await RequestAsync(
            Opcode.AutosplitEvents,
            Bytes(4, (scoped ref SpanWriter w) => w.WriteU32(sinceSeq)),
            cancellationToken).ConfigureAwait(false);
        return AutosplitEventsReply.Parse(payload);
    }

    /// <summary>
    /// AUTOSPLIT_DESCRIBE: what the running game's reason codes mean and which of them are on by
    /// default. UNSUPPORTED when the game has no watcher, which the caller reads as "no rows".
    /// </summary>
    public async Task<AutosplitEventDesc[]> AutosplitDescribeAsync(CancellationToken cancellationToken = default) =>
        AutosplitEventDesc.ParseList(
            await RequestAsync(Opcode.AutosplitDescribe, null, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Reads the ring once on connect and records where the console has got to without raising
    /// anything: the events in it belong to a run this client was not watching.
    /// </summary>
    private async Task PrimeAutosplitAsync(CancellationToken token)
    {
        try
        {
            var reply = await AutosplitEventsAsync(0, token).ConfigureAwait(false);
            Volatile.Write(ref _lastAutosplitSeq, reply.HighestSeq);
            _autosplitPrimed = true;
        }
        catch (QwarkStatusException ex)
        {
            // A module from before revision 1.4 knows neither the op nor the push, so stop asking.
            if (ex.Status == Status.UnknownOp) _autosplitAvailable = false;
            _autosplitPrimed = true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
        {
            // The connection is in trouble; the poll loop primes again once it settles.
        }
    }

    /// <summary>
    /// Raises one pushed event, or (with null) whatever a poll turns up. A pushed sequence number
    /// more than one past the last one seen means a datagram was lost, so the missing events are
    /// fetched over TCP and raised first: subscribers always see the run in order.
    /// </summary>
    private async Task DeliverAutosplitAsync(AutosplitEvent? pushed, CancellationToken token)
    {
        // Without a baseline a push cannot be told from an old event; the prime and poll cover it.
        if (pushed is not null && !_autosplitPrimed) return;

        await _autosplitGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (pushed is not { } ev)
            {
                await FetchAutosplitAsync(token).ConfigureAwait(false);
                return;
            }

            uint last = Volatile.Read(ref _lastAutosplitSeq);
            if (ev.Seq <= last) return;   // one of the three copies of a push already handled

            // Hold the push back when the fetch that should precede it failed, rather than
            // delivering it out of order: the next poll replays the whole gap in sequence.
            if (ev.Seq > last + 1 && !await FetchAutosplitAsync(token).ConfigureAwait(false)) return;

            RaiseAutosplit(ev);
        }
        finally
        {
            _autosplitGate.Release();
        }
    }

    /// <summary>
    /// Asks for everything past the last sequence number seen and raises it in order. False when
    /// the request failed, which is not fatal: the next poll asks again from the same point.
    /// Callers hold <see cref="_autosplitGate"/>.
    /// </summary>
    private async Task<bool> FetchAutosplitAsync(CancellationToken token)
    {
        try
        {
            var reply = await AutosplitEventsAsync(Volatile.Read(ref _lastAutosplitSeq), token).ConfigureAwait(false);
            foreach (var ev in reply.Events) RaiseAutosplit(ev);

            // latest_seq also accounts for events the ring dropped before this client asked.
            if (reply.LatestSeq > Volatile.Read(ref _lastAutosplitSeq))
            {
                Volatile.Write(ref _lastAutosplitSeq, reply.LatestSeq);
            }

            return true;
        }
        catch (QwarkStatusException ex)
        {
            if (ex.Status == Status.UnknownOp) _autosplitAvailable = false;
            return false;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException
                                       or InvalidOperationException or ProtocolException)
        {
            return false;
        }
    }

    private void RaiseAutosplit(AutosplitEvent ev)
    {
        if (ev.Seq <= Volatile.Read(ref _lastAutosplitSeq)) return;

        Volatile.Write(ref _lastAutosplitSeq, ev.Seq);

        try
        {
            AutosplitEventReceived?.Invoke(ev);
        }
        catch (Exception)
        {
            // A subscriber that throws is its own problem: this runs on the telemetry receive
            // loop, and losing that would take the whole connection's live state with it.
        }
    }

    /// <summary>
    /// The backstop for a lost push. A few ms of latency is fine here: the datagram is what makes
    /// a split prompt, and this only exists so a blocked or dropped one still lands. It runs at
    /// 10 Hz when telemetry is already falling back to TCP, because that says UDP is not arriving.
    /// </summary>
    private async Task AutosplitPollLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_telemetryViaTcp ? 100 : 1000, token).ConfigureAwait(false);

                // No subscriber means nothing is watching a run, and polling a console once a
                // second for events nobody reads would also keep an otherwise idle connection
                // from ever reaching the heartbeat.
                if (!AutosplitSafetyPoll || AutosplitEventReceived is null) continue;
                if (!_transportUp || !_autosplitAvailable) continue;
                if (_latestSession is not { State: SessionState.Ingame }) continue;

                if (!_autosplitPrimed)
                {
                    await PrimeAutosplitAsync(token).ConfigureAwait(false);
                    continue;
                }

                await DeliverAutosplitAsync(null, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Torn down deliberately.
        }
        catch (ObjectDisposedException)
        {
            // Torn down deliberately.
        }
    }

    // ---------------------------------------------------------------- disposal

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wantConnection = false;
        StopReconnectLoop();
        TeardownAsync(null, raiseEvent: false).GetAwaiter().GetResult();
        _sendLock.Dispose();
        _autosplitGate.Dispose();
    }
}
