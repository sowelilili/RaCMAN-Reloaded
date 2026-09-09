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
        ColourPresets = new ColourPresetStore(ResolvePath("colours"));
        SaveFiles = new SaveFileLibrary(ResolvePath(settings.SaveFilesPath));
        WebMan = new WebManLoader();
        LiveSplit = new LiveSplitClient();
        Autosplitter = new Autosplitter(settings, LiveSplit, Post);

        // The console pushes run events whether or not anyone is listening; the decision about
        // what any of them means is the autosplitter's, and it is the only subscriber.
        Client.AutosplitEventReceived += ev => Autosplitter.Handle(ev);

        if (settings.Autosplit.Enabled) LiveSplit.Start(settings.Autosplit.Host, settings.Autosplit.Port);

        Client.SessionEstablished += info => Post(() =>
        {
            Hello = info;
            AddToast($"Connected: {(string.IsNullOrEmpty(info.TitleId) ? "no game" : info.TitleId)}", ToastKind.Success);

            // The panels would silently show the old module's feature tables, so say it up front.
            if (QwarkClient.IsStaleBuild(info.QwarkVersion))
            {
                AddToast("qwark.sprx on the console is older than this client; see the Connection panel");
            }

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

    /// <summary>Named colour presets for the games' COLOR features, one file per game.</summary>
    public ColourPresetStore ColourPresets { get; }

    /// <summary>The PC-side savefile library, <c>savefiles/&lt;TITLEID&gt;/&lt;category&gt;/</c>.</summary>
    public SaveFileLibrary SaveFiles { get; }

    public WebManLoader WebMan { get; }

    /// <summary>The connection to LiveSplit's TCP server. Only the autosplitter drives it.</summary>
    public LiveSplitClient LiveSplit { get; }

    /// <summary>Turns the console's run events into LiveSplit commands, per the user's settings.</summary>
    public Autosplitter Autosplitter { get; }

    /// <summary>The SessionInfo the last HELLO returned, for the version readout. Null when offline.</summary>
    public SessionInfo? Hello { get; private set; }

    public TelemetryPacket? Telemetry { get; private set; }

    public SessionInfo Session => Telemetry?.Session ?? Client.LatestSession ?? SessionInfo.Empty;

    public bool Connected => Client.IsConnected;

    public bool Ingame => Connected && Session.State == SessionState.Ingame;

    /// <summary>
    /// True while the connected console runs a qwark build older than the one this client shipped
    /// with. Nothing refuses to work, but its feature tables are the previous build's, so every
    /// panel is a warning away from lying and the header says so.
    /// </summary>
    public bool QwarkStale => Connected && Hello is not null && QwarkClient.IsStaleBuild(Hello.QwarkVersion);

    public DescribeResult Describe { get; private set; } = DescribeResult.Empty;

    /// <summary>
    /// AUTOSPLIT_DESCRIBE for the running game: what each reason code means. Empty when the game
    /// has no watcher, which the op answers UNSUPPORTED and the panel reads as "nothing to offer".
    /// </summary>
    public AutosplitEventDesc[] AutosplitEvents { get; private set; } = Array.Empty<AutosplitEventDesc>();

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

    /// <summary>Set when LEVELFLAGS_GET answers UNSUPPORTED (e.g. Deadlocked), so the nav can hide it.</summary>
    public bool LevelFlagsUnsupported { get; private set; }

    public IReadOnlyList<LocalMod> LocalMods { get; private set; } = Array.Empty<LocalMod>();

    public Dictionary<byte, string[]> EnumOptions { get; } = new();

    public PreviousSession? Previous { get; private set; }

    public bool PreviousModalRequested { get; set; }

    /// <summary>
    /// Set by the Settings panel when the theme or the debug switch changes; the window picks it
    /// up on the next frame and re-applies the ImGui style and the clear colour.
    /// </summary>
    public bool ThemeDirty { get; set; }

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
                // The status is always shown; which request carried it is debug-only detail.
                Post(() => AddToast(
                    Panels.Ui.Debug ? $"{ex.Opcode}: {ex.Status}" : $"The console refused that: {ex.Status}",
                    ToastKind.Error));
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

        // The autosplitter reads settings per game, so it follows telemetry rather than the panel.
        Autosplitter.Game = Session.Game;
        Autosplitter.Tick(deltaSeconds);

        // With autosplitting off nothing acts on a run event, so the console is not polled for one.
        Client.AutosplitSafetyPoll = Settings.Autosplit.Enabled;

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
            AddToast(Panels.Ui.Debug ? $"Game rebooted (generation {session.Generation})" : "Game rebooted");

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
                // Back in game: re-read everything. DESCRIBE especially, because the fetch on the
                // game-change tick happened during BOOTING, when qwark still held the previous
                // game's feature table; only at INGAME is the table the running game's. Without
                // this a game switch leaves the old game's cheats on screen.
                RefreshAll();
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
        AutosplitEvents = Array.Empty<AutosplitEventDesc>();
        Autosplitter.Descriptors = AutosplitEvents;
        Planets = Array.Empty<string>();
        Autosplitter.PlanetNames = Planets;
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
        LevelFlagsUnsupported = false;
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

        Run(() => Client.PlanetListAsync(), planets =>
        {
            Planets = planets;
            Autosplitter.PlanetNames = planets;
        });

        RefreshAutosplitEvents();
        RefreshPositions();
        RefreshWatches();
        RefreshFreezes();
        RefreshPatches();
        RefreshMods();
        RefreshCombos();
        RefreshLevelFlagsSupport();
    }

    /// <summary>
    /// Probes LEVELFLAGS_GET once so the nav can hide the Level flags panel for a game that has no
    /// flag table (Deadlocked). UNSUPPORTED means hide; anything else (bytes, or BAD_ARG for a
    /// placeholder planet) means the game has them.
    /// </summary>
    public void RefreshLevelFlagsSupport()
    {
        if (!Connected) return;

        byte planet = Session.CurrentPlanet;
        Run(async () =>
        {
            try
            {
                await Client.LevelFlagsGetAsync(planet).ConfigureAwait(false);
                Post(() => LevelFlagsUnsupported = false);
            }
            catch (QwarkStatusException ex)
            {
                bool unsupported = ex.Status == Status.Unsupported;
                Post(() => LevelFlagsUnsupported = unsupported);
            }
        });
    }

    /// <summary>
    /// Re-reads AUTOSPLIT_DESCRIBE. UNSUPPORTED is the normal answer for a game qwark has no
    /// autosplitter for, and for a module older than revision 1.4 the op is unknown; both mean the
    /// panel has no per-game rows to draw, and neither is worth a toast.
    /// </summary>
    public void RefreshAutosplitEvents()
    {
        if (!Connected) return;

        Run(async () =>
        {
            try
            {
                var events = await Client.AutosplitDescribeAsync().ConfigureAwait(false);
                Post(() =>
                {
                    AutosplitEvents = events;
                    Autosplitter.Descriptors = events;
                });
            }
            catch (QwarkStatusException ex) when (ex.Status is Status.Unsupported or Status.UnknownOp)
            {
                Post(() =>
                {
                    AutosplitEvents = Array.Empty<AutosplitEventDesc>();
                    Autosplitter.Descriptors = AutosplitEvents;
                });
            }
        });
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
                    if (!UnlocksUnsupported) AddToast("This game has no unlock table.");
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
        string line = $"{session.State} | {title} | {session.Game.DisplayName()}";
        if (!Panels.Ui.Debug) return line;

        string tick = session.Tick > 0 ? $"tick {session.Tick}" : "tick -";
        return line + $" | generation {session.Generation} | {tick}"
                    + $" | qwark v{session.QwarkVersion} protocol {session.ProtocolVersion}";
    }

    public void Dispose()
    {
        LiveSplit.Dispose();
        Client.Dispose();
    }
}
