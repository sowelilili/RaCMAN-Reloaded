using System.Collections.Concurrent;
using RaCMAN.Protocol;

namespace RaCMAN.App;

public enum ToastKind
{
    Info,
    Success,
    Error,
}

public sealed class Toast
{
    public required string Text { get; init; }

    public ToastKind Kind { get; init; } = ToastKind.Info;

    public float Remaining { get; set; } = 6f;
}

/// <summary>
/// Everything the render thread reads: the client, the latest telemetry, the last fetched lists
/// and a toast queue. Network results arrive through a concurrent queue drained once per frame,
/// so no ImGui call ever happens off the render thread.
/// </summary>
public sealed class AppState : IDisposable
{
    private readonly ConcurrentQueue<Action> _actions = new();
    private readonly List<Toast> _toasts = new();

    private GameId _lastGame = GameId.None;
    private string _lastTitle = string.Empty;
    private uint _lastGeneration;
    private byte _lastPlanet = 0xFF;
    private SessionState _lastState = SessionState.Xmb;
    private bool _lastPreviousPending;
    private int _inFlight;

    public AppState(Settings settings)
    {
        Settings = settings;
        Client = new QwarkClient { AutoReconnect = settings.AutoReconnect };
        Mods = new ModLibrary(ResolvePath(settings.ModsPath));
        Watchlists = new WatchlistStore(ResolvePath("watchlists"));
        SaveFiles = new SaveFileLibrary(ResolvePath(settings.SaveFilesPath));
        WebMan = new WebManLoader();

        Client.SessionEstablished += info => Post(() =>
        {
            Hello = info;
            AddToast($"Connected: {(string.IsNullOrEmpty(info.TitleId) ? "no game" : info.TitleId)}", ToastKind.Success);
            ForceRefresh();
        });

        Client.Disconnected += ex => Post(() =>
        {
            AddToast(ex is null ? "Disconnected" : $"Disconnected: {ex.Message}", ToastKind.Error);
            Hello = null;
            _lastGame = GameId.None;
            _lastTitle = string.Empty;
            _lastGeneration = 0;
            _lastState = SessionState.Xmb;
            _lastPlanet = 0xFF;
            ResetPanels();
        });
    }

    public QwarkClient Client { get; }

    public Settings Settings { get; }

    public ModLibrary Mods { get; }

    public WatchlistStore Watchlists { get; }

    /// <summary>The PC-side savefile library, <c>savefiles/&lt;TITLEID&gt;/&lt;category&gt;/</c>.</summary>
    public SaveFileLibrary SaveFiles { get; }

    public WebManLoader WebMan { get; }

    /// <summary>The SessionInfo the last HELLO returned, for the version readout. Null when offline.</summary>
    public SessionInfo? Hello { get; private set; }

    public TelemetryPacket? Telemetry { get; private set; }

    public SessionInfo Session => Telemetry?.Session ?? Client.LatestSession ?? SessionInfo.Empty;

    public bool Connected => Client.IsConnected;

    public bool Ingame => Connected && Session.State == SessionState.Ingame;

    public DescribeResult Describe { get; private set; } = DescribeResult.Empty;

    public string[] Planets { get; private set; } = Array.Empty<string>();

    public WatchEntry[] Watches { get; private set; } = Array.Empty<WatchEntry>();

    public FreezeEntry[] Freezes { get; private set; } = Array.Empty<FreezeEntry>();

    public PatchEntry[] Patches { get; private set; } = Array.Empty<PatchEntry>();

    public ModEntry[] ConsoleMods { get; private set; } = Array.Empty<ModEntry>();

    public ComboEntry[] Combos { get; private set; } = Array.Empty<ComboEntry>();

    public PositionList Positions { get; private set; } = PositionList.Empty;

    public UnlockList Unlocks { get; private set; } = UnlockList.Empty;

    /// <summary>Set once when UNLOCK_LIST answers UNSUPPORTED, so the toast is not repeated.</summary>
    public bool UnlocksUnsupported { get; private set; }

    public IReadOnlyList<LocalMod> LocalMods { get; private set; } = Array.Empty<LocalMod>();

    public Dictionary<byte, string[]> EnumOptions { get; } = new();

    public PreviousSession? Previous { get; private set; }

    public bool PreviousModalRequested { get; set; }

    public IReadOnlyList<Toast> Toasts => _toasts;

    public int InFlight => Volatile.Read(ref _inFlight);

    public string? LastError { get; private set; }

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    // ---------------------------------------------------------------- plumbing

    public void Post(Action action) => _actions.Enqueue(action);

    public void AddToast(string text, ToastKind kind = ToastKind.Info)
    {
        _toasts.Add(new Toast { Text = text, Kind = kind });
        if (_toasts.Count > 8) _toasts.RemoveRange(0, _toasts.Count - 8);
        if (kind == ToastKind.Error) LastError = text;
    }

    /// <summary>Runs one request off the render thread and turns any non-OK status into a toast.</summary>
    public void Run(Func<Task> operation, string? successMessage = null)
    {
        Interlocked.Increment(ref _inFlight);
        _ = Task.Run(async () =>
        {
            try
            {
                await operation().ConfigureAwait(false);
                if (successMessage is not null) Post(() => AddToast(successMessage, ToastKind.Success));
            }
            catch (QwarkStatusException ex)
            {
                Post(() => AddToast($"{ex.Opcode}: {ex.Status}", ToastKind.Error));
            }
            catch (Exception ex)
            {
                Post(() => AddToast($"{ex.GetType().Name}: {ex.Message}", ToastKind.Error));
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        });
    }

    public void Run<T>(Func<Task<T>> operation, Action<T> onSuccess, string? successMessage = null)
    {
        Run(async () =>
        {
            var result = await operation().ConfigureAwait(false);
            Post(() => onSuccess(result));
        }, successMessage);
    }

    // ---------------------------------------------------------------- per-frame

    public void Tick(float deltaSeconds)
    {
        while (_actions.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AddToast($"UI error: {ex.Message}", ToastKind.Error);
            }
        }

        Telemetry = Client.LatestTelemetry;

        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            _toasts[i].Remaining -= deltaSeconds;
            if (_toasts[i].Remaining <= 0) _toasts.RemoveAt(i);
        }

        if (!Connected) return;

        var session = Session;

        // A different game, or the same game rebooted, invalidates every list.
        if (session.Game != _lastGame || !string.Equals(session.TitleId, _lastTitle, StringComparison.Ordinal))
        {
            _lastGame = session.Game;
            _lastTitle = session.TitleId;
            _lastGeneration = session.Generation;
            _lastPlanet = 0xFF;
            _lastState = session.State;
            ResetPanels();
            RefreshAll();
        }
        else if (session.Generation != _lastGeneration)
        {
            _lastGeneration = session.Generation;
            _lastPlanet = 0xFF;
            _lastState = session.State;
            AddToast($"Game rebooted (generation {session.Generation})");

            // Same title, new process: the descriptors still hold but everything read out of the
            // old process is now a lie, so it goes before the re-read lands.
            ClearGameViews();
            RefreshAll();
        }
        else if (session.State != _lastState)
        {
            var previous = _lastState;
            _lastState = session.State;

            if (previous == SessionState.Ingame)
            {
                // Side panels never outlive the game: anything read out of game memory goes the
                // moment the session leaves INGAME, and the panels then show the state instead.
                _lastPlanet = 0xFF;
                ClearGameViews();
            }
            else if (session.State == SessionState.Ingame)
            {
                // Back in game: the lists are readable again, so fetch them once. Positions are
                // left to the planet check below, which fires because _lastPlanet is 0xFF.
                RefreshUnlocks(quiet: true);
                RefreshWatches();
                RefreshFreezes();
                RefreshPatches();
            }
        }

        if (session.CurrentPlanet != _lastPlanet && session.State == SessionState.Ingame)
        {
            _lastPlanet = session.CurrentPlanet;
            RefreshPositions();
        }

        if (session.PreviousPending && !_lastPreviousPending)
        {
            Run(() => Client.PreviousListAsync(), previous =>
            {
                Previous = previous;
                PreviousModalRequested = true;
            });
        }
        else if (!session.PreviousPending && _lastPreviousPending)
        {
            Previous = null;
            PreviousModalRequested = false;
        }

        _lastPreviousPending = session.PreviousPending;
    }

    public void ResetPanels()
    {
        Describe = DescribeResult.Empty;
        Planets = Array.Empty<string>();
        Watches = Array.Empty<WatchEntry>();
        Freezes = Array.Empty<FreezeEntry>();
        Patches = Array.Empty<PatchEntry>();
        ConsoleMods = Array.Empty<ModEntry>();
        Combos = Array.Empty<ComboEntry>();
        LocalMods = Array.Empty<LocalMod>();
        EnumOptions.Clear();
        Previous = null;
        PreviousModalRequested = false;
        ClearGameViews();
        Panels.PanelState.ResetAll();
    }

    /// <summary>
    /// Drops everything that was read out of the running game's memory: position slots, the
    /// unlock table, the level-flag bytes, the moby rows and the memory dump. The descriptors,
    /// the planet list and the console-owned tables (watches, freezes, mods) are not in here:
    /// those describe the game, not the process, and qwark keeps them across a reboot.
    /// </summary>
    public void ClearGameViews()
    {
        Positions = PositionList.Empty;
        Unlocks = UnlockList.Empty;
        UnlocksUnsupported = false;
        Panels.PanelState.ClearGameData();
    }

    /// <summary>
    /// Re-reads everything from the console. There is no client state to restore, so this is also
    /// the whole of the reconnect path. The change trackers move to the current session so the
    /// next Tick does not immediately fetch everything a second time.
    /// </summary>
    public void ForceRefresh()
    {
        var session = Session;
        _lastGame = session.Game;
        _lastTitle = session.TitleId;
        _lastGeneration = session.Generation;
        _lastState = session.State;
        _lastPlanet = 0xFF;
        RefreshAll();
    }

    public void RefreshAll()
    {
        if (!Connected) return;

        Run(() => Client.DescribeAsync(), describe =>
        {
            Describe = describe;
            EnumOptions.Clear();
            foreach (var feature in describe.Features.Where(f => f.Kind == FeatureKind.Enum))
            {
                byte id = feature.Id;
                Run(() => Client.FeatureOptionsAsync(id), options => EnumOptions[id] = options);
            }
        });

        Run(() => Client.PlanetListAsync(), planets => Planets = planets);
        RefreshPositions();
        RefreshWatches();
        RefreshFreezes();
        RefreshPatches();
        RefreshMods();
        RefreshCombos();
    }

    public void RefreshPositions()
    {
        if (Connected) Run(() => Client.PosListAsync(), positions => Positions = positions);
    }

    /// <summary>
    /// Re-reads UNLOCK_LIST. UNSUPPORTED is normal for a game with no unlock table, so it is
    /// reported once and then the panel just says so; every other status still toasts.
    /// </summary>
    public void RefreshUnlocks(bool quiet = false)
    {
        if (!Connected) return;

        Run(async () =>
        {
            try
            {
                var list = await Client.UnlockListAsync().ConfigureAwait(false);
                Post(() =>
                {
                    Unlocks = list;
                    UnlocksUnsupported = false;
                });
            }
            catch (QwarkStatusException ex) when (ex.Status == Status.Unsupported)
            {
                Post(() =>
                {
                    Unlocks = UnlockList.Empty;
                    if (!UnlocksUnsupported) AddToast("UNLOCK_LIST: Unsupported for this game");
                    UnlocksUnsupported = true;
                });
            }
            catch (QwarkStatusException ex) when (quiet && ex.Status == Status.NotIngame)
            {
                // An auto-refresh that lands between states says nothing.
            }
        });
    }

    public void RefreshWatches()
    {
        if (Connected) Run(() => Client.WatchListAsync(), watches => Watches = watches);
    }

    public void RefreshFreezes()
    {
        if (Connected) Run(() => Client.FreezeListAsync(), freezes => Freezes = freezes);
    }

    public void RefreshPatches()
    {
        if (Connected) Run(() => Client.PatchListAsync(), patches => Patches = patches);
    }

    public void RefreshCombos()
    {
        if (Connected) Run(() => Client.ComboListAsync(), combos => Combos = combos);
    }

    public void RefreshMods()
    {
        if (!Connected) return;

        Run(() => Client.ModListAsync(), mods => ConsoleMods = mods);
        RescanLocalMods();
    }

    public void RescanLocalMods()
    {
        string title = Session.TitleId;
        if (string.IsNullOrEmpty(title))
        {
            LocalMods = Array.Empty<LocalMod>();
            return;
        }

        try
        {
            LocalMods = Mods.Scan(title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddToast($"Mod library: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>The live value of a watch out of the latest telemetry packet.</summary>
    public WatchValue? WatchValueFor(byte id)
    {
        var telemetry = Telemetry;
        if (telemetry is null) return null;
        foreach (var watch in telemetry.Watches)
        {
            if (watch.Id == id) return watch;
        }

        return null;
    }

    public string StatusLine()
    {
        if (!Connected)
        {
            if (!Client.WantsConnection) return "Disconnected";
            var remaining = Client.ReconnectRemaining;
            return remaining > TimeSpan.Zero
                ? $"Reconnecting in {remaining.TotalSeconds:0.0} s (attempt {Client.ReconnectAttempt})"
                : $"Reconnecting (attempt {Client.ReconnectAttempt})";
        }

        var session = Session;
        string title = string.IsNullOrEmpty(session.TitleId) ? "no title" : session.TitleId;
        string tick = session.Tick > 0 ? $"tick {session.Tick}" : "tick -";
        return $"{session.State} | {title} | {session.Game} | generation {session.Generation} | {tick} | qwark v{session.QwarkVersion} protocol {session.ProtocolVersion}";
    }

    public void Dispose()
    {
        Client.Dispose();
    }
}
