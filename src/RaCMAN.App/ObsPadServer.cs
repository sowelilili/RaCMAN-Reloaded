using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using RaCMAN.Protocol;
using StbImageSharp;

namespace RaCMAN.App;

/// <summary>
/// One telemetry packet's pad, copied out of it where the packet arrives: the mask, the four analog
/// axes in the protocol's own order (rx, ry, lx, ly), and whether the console was in a game.
/// Immutable, so the network thread can hand one to the server and forget about it.
/// </summary>
public readonly record struct PadSnapshot(uint Mask, float Rx, float Ry, float Lx, float Ly, bool Live)
{
    /// <summary>Nothing pressed, nothing live: what a stream sends before any packet has arrived.</summary>
    public static readonly PadSnapshot Idle = new(0, 0, 0, 0, 0, false);

    public static PadSnapshot From(SessionInfo session)
    {
        var analog = session.Analog;
        return new PadSnapshot(session.PadMask, At(analog, 0), At(analog, 1), At(analog, 2), At(analog, 3),
            session.State == SessionState.Ingame);
    }

    /// <summary>
    /// The one line a stream sends per packet: the mask, the axes in wire order, and the flag the
    /// page greys the pad on. Written by hand because it goes out thirty times a second.
    /// </summary>
    public string ToJson() =>
        $"{{\"m\":{Mask},\"a\":[{Axis(Rx)},{Axis(Ry)},{Axis(Lx)},{Axis(Ly)}],\"live\":{(Live ? "true" : "false")}}}";

    private static float At(float[] values, int index) =>
        values is not null && index < values.Length && float.IsFinite(values[index]) ? values[index] : 0f;

    private static string Axis(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Where the served page's skins come from: what the picker chose, and the library behind it.</summary>
public interface IPadSkins
{
    /// <summary>The skin the client has selected, for a request that names none.</summary>
    string Selected { get; }

    /// <summary>The named skin, or null when the library has no such folder.</summary>
    ControllerSkin? Find(string name);
}

/// <summary>The skin library as the client sees it: the picker's choice, and only folders it lists.</summary>
public sealed class SettingsPadSkins : IPadSkins
{
    private readonly Settings _settings;

    public SettingsPadSkins(Settings settings)
    {
        _settings = settings;
    }

    public string Selected => _settings.InputSkin;

    public ControllerSkin? Find(string name)
    {
        // Only a folder the library lists: the name comes off a query string, and a skin is a
        // folder name rather than a path.
        if (!SkinLibrary.List().Contains(name, StringComparer.Ordinal)) return null;

        try
        {
            return SkinLibrary.Load(name);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or ArgumentException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>What the listener is doing, for the Input display panel to say out loud.</summary>
public enum ObsPadState
{
    Off,
    Serving,

    /// <summary>The port would not open, almost always because something else holds it.</summary>
    Failed,
}

/// <summary>
/// The input display as a page OBS can take as a Browser Source. A loopback-only
/// <see cref="HttpListener"/> serves one embedded page, the selected skin's sheet and its parsed
/// rectangles, and a Server-Sent Events stream that carries the pad at telemetry rate.
/// <para>
/// Loopback only, always: the prefix is <c>http://127.0.0.1:&lt;port&gt;/</c>, which needs no URL
/// reservation for an ordinary user on Windows and no firewall rule anywhere, because Windows does
/// not filter loopback traffic.
/// </para>
/// <para>
/// Nothing in here touches ImGui or <see cref="AppState"/>: the app calls <see cref="Publish"/> with
/// a snapshot wherever telemetry arrives, and the server's own writer tasks push it to every open
/// stream. Every write is bounded, so a browser source that was closed without saying so cannot
/// stall the others or the console's thread.
/// </para>
/// </summary>
public sealed class ObsPadServer : IDisposable
{
    /// <summary>One above qwark's own port, so the two are recognisably a pair.</summary>
    public const int DefaultPort = QwarkClient.DefaultPort + 1;

    /// <summary>How many browser sources may watch at once. More than anyone streams with.</summary>
    public const int MaxStreams = 8;

    /// <summary>The SSE event name that tells the page to fetch its sheet and rectangles again.</summary>
    public const string SkinEvent = "skin";

    /// <summary>A comment line, so a CEF with no proxy in front of it does not time the stream out.</summary>
    private const string KeepAliveComment = ": keep-alive\n\n";

    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(5);

    /// <summary>A reader that has not taken a frame in this long is gone, whatever its socket says.</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long telemetry may be missing before the pad is greyed. Longer than the gap the client
    /// leaves before it falls back to polling the console over TCP, so that fallback does not flash
    /// the overlay grey on the way through.
    /// </summary>
    private const long StaleMs = 2000;

    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromMilliseconds(500);

    private static byte[]? _page;

    private readonly IPadSkins _skins;
    private readonly ConcurrentDictionary<PadStream, byte> _streams = new();
    private readonly object _gate = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private PadSnapshot _latest = PadSnapshot.Idle;
    private long _lastPublishMs;
    private bool _enabled;
    private int _port = DefaultPort;
    private bool _disposed;

    public ObsPadServer(IPadSkins skins)
    {
        _skins = skins;
    }

    public ObsPadState State { get; private set; } = ObsPadState.Off;

    /// <summary>Why the listener would not start, or null. Shown on the Input display panel.</summary>
    public string? Problem { get; private set; }

    /// <summary>The port the settings ask for, whether or not it opened.</summary>
    public int Port => _port;

    /// <summary>How many browser sources are watching, for the panel and the tests.</summary>
    public int Streams => _streams.Count;

    /// <summary>What to paste into a browser source.</summary>
    public static string UrlFor(int port) => $"http://127.0.0.1:{port}/pad";

    // ---------------------------------------------------------------- what the app calls

    /// <summary>
    /// Starts, stops or moves the listener so it matches the settings. Called once a frame from the
    /// render thread; a run where nothing changed does nothing at all, and a changed port is also
    /// the retry for a port that was taken.
    /// </summary>
    public void Apply(bool enabled, int port)
    {
        if (_disposed) return;
        if (enabled == _enabled && port == _port) return;

        _enabled = enabled;
        _port = port;

        Stop();
        if (enabled) Start(port);
    }

    /// <summary>
    /// One telemetry packet's pad. Called on the network thread: it copies the snapshot, queues one
    /// frame per open stream and returns, and the writer tasks do the waiting.
    /// </summary>
    public void Publish(PadSnapshot snapshot)
    {
        lock (_gate) _latest = snapshot;
        Interlocked.Exchange(ref _lastPublishMs, Environment.TickCount64);

        if (_streams.IsEmpty) return;
        Broadcast(Frame(null, snapshot.ToJson()));
    }

    /// <summary>The picker moved: every page fetches its sheet and rectangles again, without a refresh.</summary>
    public void SkinChanged()
    {
        if (_streams.IsEmpty) return;
        Broadcast(Frame(SkinEvent, "{}"));
    }

    // ---------------------------------------------------------------- the wire formats

    /// <summary>
    /// One SSE frame: the event name when it is not the default one, the data line, and the blank
    /// line that ends the frame. Line-based and LF-terminated, whatever this platform's newline is.
    /// </summary>
    public static string Frame(string? name, string data) =>
        (string.IsNullOrEmpty(name) ? string.Empty : $"event: {name}\n") + $"data: {data}\n\n";

    /// <summary>
    /// The skin as the page reads it, so nothing browser-side has to parse skin.txt: the rectangles
    /// by name, the base size the canvas takes, the analog pitch and the sheet's own pixel size.
    /// The two lists are the draw plan — which mask bit lights which sprite, and which sprite each
    /// stick shows — so the pressed/idle naming quirk stays in <see cref="ControllerSkin"/>.
    /// </summary>
    public static string SkinJson(ControllerSkin skin, int sheetWidth, int sheetHeight)
    {
        var sprites = new Dictionary<string, SkinRectJson>(StringComparer.Ordinal);
        foreach (var (name, sprite) in skin.Sprites)
        {
            sprites[name] = new SkinRectJson(sprite.DrawX, sprite.DrawY, sprite.SpriteX, sprite.SpriteY,
                sprite.Width, sprite.Height);
        }

        var buttons = new List<SkinButtonJson>();
        foreach (var (name, button) in PadSprites)
        {
            if (skin.TryGet(name, out _)) buttons.Add(new SkinButtonJson((uint)button, name));
        }

        var sticks = new List<SkinStickJson>();
        foreach (var (name, button, x, y) in PadSticks)
        {
            // TryGetStick owns the quirk: the shipped skins name the highlighted cell "l3" and the
            // idle one "l3Press", and the page only ever sees the answer.
            if (!skin.TryGetStick(name, pressed: false, out _)) continue;
            if (!skin.TryGetStick(name, pressed: true, out _)) continue;

            sticks.Add(new SkinStickJson((uint)button, name + "Press", name, x, y));
        }

        var payload = new SkinJsonPayload(skin.Name, skin.Base.Width, skin.Base.Height, skin.AnalogPitch,
            sheetWidth, sheetHeight, sprites, buttons, sticks);

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>The sprites a mask bit lights, in the order the input display blits them.</summary>
    private static readonly (string Sprite, PadButton Button)[] PadSprites =
    {
        ("dpadUp", PadButton.Up),
        ("dpadRight", PadButton.Right),
        ("dpadDown", PadButton.Down),
        ("dpadLeft", PadButton.Left),
        ("triangle", PadButton.Triangle),
        ("circle", PadButton.Circle),
        ("cross", PadButton.Cross),
        ("square", PadButton.Square),
        ("select", PadButton.Select),
        ("start", PadButton.Start),
        ("l1", PadButton.L1),
        ("l2", PadButton.L2),
        ("r1", PadButton.R1),
        ("r2", PadButton.R2),
    };

    /// <summary>The two sticks, with the analog[] indices each one is offset by.</summary>
    private static readonly (string Stick, PadButton Button, int X, int Y)[] PadSticks =
    {
        ("l3", PadButton.L3, 2, 3),
        ("r3", PadButton.R3, 0, 1),
    };

    private sealed record SkinRectJson(
        [property: JsonPropertyName("dx")] int DrawX,
        [property: JsonPropertyName("dy")] int DrawY,
        [property: JsonPropertyName("sx")] int SpriteX,
        [property: JsonPropertyName("sy")] int SpriteY,
        [property: JsonPropertyName("w")] int Width,
        [property: JsonPropertyName("h")] int Height);

    private sealed record SkinButtonJson(
        [property: JsonPropertyName("bit")] uint Bit,
        [property: JsonPropertyName("sprite")] string Sprite);

    private sealed record SkinStickJson(
        [property: JsonPropertyName("bit")] uint Bit,
        [property: JsonPropertyName("idle")] string Idle,
        [property: JsonPropertyName("pressed")] string Pressed,
        [property: JsonPropertyName("x")] int AxisX,
        [property: JsonPropertyName("y")] int AxisY);

    private sealed record SkinJsonPayload(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height,
        [property: JsonPropertyName("analogPitch")] int AnalogPitch,
        [property: JsonPropertyName("sheetWidth")] int SheetWidth,
        [property: JsonPropertyName("sheetHeight")] int SheetHeight,
        [property: JsonPropertyName("sprites")] Dictionary<string, SkinRectJson> Sprites,
        [property: JsonPropertyName("buttons")] List<SkinButtonJson> Buttons,
        [property: JsonPropertyName("sticks")] List<SkinStickJson> Sticks);

    // ---------------------------------------------------------------- the listener

    private void Start(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");

        try
        {
            listener.Start();
        }
        catch (Exception ex) when (ex is HttpListenerException or SocketException or ObjectDisposedException
                                       or PlatformNotSupportedException)
        {
            // Almost always another copy of the client, or another program on the same port. The
            // panel says so and the rest of the client carries on.
            listener.Close();
            State = ObsPadState.Failed;
            Problem = ex.Message;
            return;
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        State = ObsPadState.Serving;
        Problem = null;

        var token = _cts.Token;
        _ = Task.Run(() => AcceptAsync(listener, token));
        _ = Task.Run(() => WatchdogAsync(token));
    }

    private void Stop()
    {
        _cts?.Cancel();

        foreach (var stream in _streams.Keys) Drop(stream);

        try
        {
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        State = ObsPadState.Off;
    }

    private async Task AcceptAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException
                                           or InvalidOperationException)
            {
                // The listener was stopped, which is the only way out of this loop.
                return;
            }

            _ = Task.Run(() => HandleAsync(context), token);
        }
    }

    /// <summary>
    /// Greys the overlay when telemetry stops. Nothing arrives while the console is off, the link is
    /// down or the game is being launched, so without this a stream would sit on the last pose it
    /// was sent as if the pad were still being held that way.
    /// </summary>
    private async Task WatchdogAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(WatchdogPeriod, token).ConfigureAwait(false);
                if (Environment.TickCount64 - Interlocked.Read(ref _lastPublishMs) < StaleMs) continue;

                PadSnapshot stale;
                lock (_gate)
                {
                    if (!_latest.Live) continue;

                    _latest = _latest with { Live = false };
                    stale = _latest;
                }

                Broadcast(Frame(null, stale.ToJson()));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped deliberately.
        }
    }

    // ---------------------------------------------------------------- the routes

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string? wanted = context.Request.QueryString["skin"];

            switch (path)
            {
                case "/":
                case "/pad":
                case "/index.html":
                    await SendAsync(context, 200, "text/html; charset=utf-8", Page()).ConfigureAwait(false);
                    break;

                case "/skin.json":
                    await SendSkinJsonAsync(context, wanted).ConfigureAwait(false);
                    break;

                case "/skin.png":
                    await SendSheetAsync(context, wanted).ConfigureAwait(false);
                    break;

                case "/events":
                    OpenStream(context);
                    break;

                default:
                    await SendAsync(context, 404, "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("no such page")).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException
                                       or InvalidOperationException)
        {
            // The browser went away mid-request. Nothing to report and nothing to clean up.
        }
    }

    private async Task SendSkinJsonAsync(HttpListenerContext context, string? wanted)
    {
        var skin = Resolve(wanted);
        if (skin is null)
        {
            await SendAsync(context, 404, "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("no skin")).ConfigureAwait(false);
            return;
        }

        var (width, height) = SheetSize(skin);
        await SendAsync(context, 200, "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(SkinJson(skin, width, height))).ConfigureAwait(false);
    }

    private async Task SendSheetAsync(HttpListenerContext context, string? wanted)
    {
        var skin = Resolve(wanted);
        byte[]? bytes = null;

        if (skin is not null)
        {
            try
            {
                bytes = await File.ReadAllBytesAsync(skin.ImagePath).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or DirectoryNotFoundException)
            {
                bytes = null;
            }
        }

        if (bytes is null)
        {
            await SendAsync(context, 404, "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("no sheet")).ConfigureAwait(false);
            return;
        }

        await SendAsync(context, 200, MediaType(skin!.ImageFileName), bytes).ConfigureAwait(false);
    }

    /// <summary>The route is named for the usual case; the type follows the file the skin names.</summary>
    private static string MediaType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            _ => "application/octet-stream",
        };

    /// <summary>
    /// The skin a request asks for, falling back to the selected one. A name that is not one folder
    /// name is refused before the library is asked: the name comes off a query string.
    /// </summary>
    private ControllerSkin? Resolve(string? wanted)
    {
        if (wanted is { Length: > 0 } name && IsFolderName(name) && _skins.Find(name) is { } picked) return picked;

        string selected = _skins.Selected;
        return selected.Length > 0 && IsFolderName(selected) ? _skins.Find(selected) : null;
    }

    private static bool IsFolderName(string name) =>
        name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && name != "." && name != "..";

    /// <summary>
    /// The sheet's pixel size, which skin.txt does not carry. Decoded rather than guessed, because a
    /// skin may name any image the client can load, and this is asked for once per page.
    /// </summary>
    private static (int Width, int Height) SheetSize(ControllerSkin skin)
    {
        try
        {
            using var stream = File.OpenRead(skin.ImagePath);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            if (image.Width > 0 && image.Height > 0) return (image.Width, image.Height);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or ArgumentException or DirectoryNotFoundException)
        {
            // The page reads the sheet's own size off the image it loads; this is only the hint.
        }

        return (0, 0);
    }

    private static async Task SendAsync(HttpListenerContext context, int status, string contentType, byte[] body)
    {
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = body.Length;

        // An overlay must show what the console is doing now, and a skin can be edited under it.
        response.Headers["Cache-Control"] = "no-store";

        await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        response.Close();
    }

    /// <summary>The page itself, out of the assembly: one file, no external assets, read once.</summary>
    private static byte[] Page()
    {
        if (_page is not null) return _page;

        using var stream = typeof(ObsPadServer).Assembly.GetManifestResourceStream("RaCMAN.App.obspad.html");
        if (stream is null) return _page = Encoding.UTF8.GetBytes("<!doctype html><title>pad</title>no page");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return _page = memory.ToArray();
    }

    // ---------------------------------------------------------------- the streams

    private sealed class PadStream
    {
        public required HttpListenerResponse Response { get; init; }

        public required Channel<string> Queue { get; init; }

        public required CancellationTokenSource Cts { get; init; }
    }

    private void OpenStream(HttpListenerContext context)
    {
        var response = context.Response;

        if (_streams.Count >= MaxStreams)
        {
            // Better one source that says why than eight that stutter.
            response.StatusCode = 503;
            response.ContentType = "text/plain; charset=utf-8";
            response.Close();
            return;
        }

        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-store";
        response.SendChunked = true;
        response.KeepAlive = true;

        var stream = new PadStream
        {
            Response = response,

            // Lossy on purpose: a reader that cannot keep up misses frames rather than holding up
            // the thread the console's telemetry arrives on.
            Queue = Channel.CreateBounded<string>(new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            }),
            Cts = new CancellationTokenSource(),
        };

        PadSnapshot latest;
        lock (_gate) latest = _latest;

        // Something to draw at once, rather than a blank overlay until the next packet.
        stream.Queue.Writer.TryWrite(Frame(null, latest.ToJson()));

        _streams[stream] = 0;
        _ = Task.Run(() => PumpAsync(stream));
    }

    private void Broadcast(string frame)
    {
        foreach (var stream in _streams.Keys) stream.Queue.Writer.TryWrite(frame);
    }

    private async Task PumpAsync(PadStream stream)
    {
        var token = stream.Cts.Token;
        var output = stream.Response.OutputStream;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (stream.Queue.Reader.TryRead(out var payload))
                {
                    await WriteAsync(output, payload, token).ConfigureAwait(false);
                    continue;
                }

                // Nothing queued. Wait for the next packet, and say something before the wait is
                // long enough for a proxy-less CEF to decide the stream is dead.
                if (!await IdleAsync(stream, token).ConfigureAwait(false))
                {
                    await WriteAsync(output, KeepAliveComment, token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException
                                       or OperationCanceledException or TimeoutException
                                       or InvalidOperationException)
        {
            // The reader is gone, or too slow to be one. Either way this stream is over.
        }
        finally
        {
            Drop(stream);
        }
    }

    /// <summary>
    /// Waits for the next frame. True when one can be read; false when the wait ran out, which is
    /// the keep-alive's cue. The waiter is cancelled rather than abandoned, so a stream that is
    /// never written to again leaves nothing behind.
    /// </summary>
    private static async Task<bool> IdleAsync(PadStream stream, CancellationToken token)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(KeepAlive);

        try
        {
            return await stream.Queue.Reader.WaitToReadAsync(idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// One frame onto the wire, under a timeout: a browser source that was closed without closing
    /// its socket leaves a write that never completes, and that must not hold a task forever.
    /// </summary>
    private static async Task WriteAsync(System.IO.Stream output, string payload, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await output.WriteAsync(bytes, token).AsTask().WaitAsync(WriteTimeout, token).ConfigureAwait(false);
        await output.FlushAsync(token).WaitAsync(WriteTimeout, token).ConfigureAwait(false);
    }

    private void Drop(PadStream stream)
    {
        if (!_streams.TryRemove(stream, out _)) return;

        stream.Cts.Cancel();
        stream.Queue.Writer.TryComplete();

        try
        {
            // Abort rather than Close: there is nothing to flush to a reader that has gone, and a
            // graceful close would wait for one that is merely slow.
            stream.Response.Abort();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Already closed.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _enabled = false;
        Stop();
    }
}
