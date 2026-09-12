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
/// <para>
/// What the console <em>described</em> — the DESCRIBE reply, the autosplit rows, the planet list,
/// the unlock table's shape, whether the game has level flags — belongs to the game, not to the
/// process running it or to this connection. It is therefore kept for as long as the same game
/// keeps coming back: a quit to the XMB, a reboot and a dropped link all leave it alone, and the
/// panels draw it with their controls greyed out until the session is INGAME again. Only a
/// different game, a different qwark build or the user asking for a re-read replaces it. Anything
/// read out of the running process goes the moment that process does; see
/// <see cref="ClearGameViews"/>.
/// </para>
/// </summary>
public sealed class AppState : IDisposable
{
    /// <summary>
    /// Which game the described data on this object belongs to, and which module described it. A
    /// build that is replaced under a running game describes it differently, so it is part of the
    /// key rather than something to notice later.
    /// </summary>
    private readonly record struct DescribedKey(GameId Game, byte QwarkVersion, byte ProtocolVersion)
    {
        public bool IsEmpty => Game == GameId.None;
    }

    private readonly ConcurrentQueue<Action> _actions = new();
    private readonly List<Toast> _toasts = new();

    private GameId _lastGame = GameId.None;
    private string _lastTitle = string.Empty;
    private uint _lastGeneration;
    private byte _lastPlanet = 0xFF;
    private SessionState _lastState = SessionState.Xmb;
    private bool _lastPreviousPending;
    private DescribedKey _described;

    /// <summary>
    /// Set when the values on screen came out of a process that has gone: a reboot, a quit, a
    /// dropped connection. The next tick that finds the session INGAME reads them again, quietly.
    /// </summary>
    private bool _liveStale;

    private int _inFlight;

    /// <summary>
    /// When the last check-and-load through webMAN ran, on <see cref="Environment.TickCount64"/>,
    /// or 0 when none has. It is what rate-limits the reconnect loop's detour.
    /// </summary>
    private long _lastDetourMs;

    /// <summary>
    /// <paramref name="updatesAllowed"/> is off by default so that nothing which merely constructs
    /// an AppState — a test, a tool — can reach GitHub; only Program turns it on, and only for a
    /// run none of the headless flags apply to.
    /// </summary>
    public AppState(Settings settings, bool updatesAllowed = false)
    {
        Settings = settings;
        Client = new QwarkClient { AutoReconnect = settings.AutoReconnect };

        // The user's own files are in the data folder; the shipped mod library is beside the
        // executable, where the updater is free to replace it.
        Mods = new ModLibrary(AppPaths.InData(settings.ModsPath), AppPaths.ShippedMods);
        Watchlists = new WatchlistStore(AppPaths.Watchlists);
        ColourPresets = new ColourPresetStore(AppPaths.Colours);
        SaveFiles = new SaveFileLibrary(AppPaths.InData(settings.SaveFilesPath));
        WebMan = new WebManLoader();
        Rpcs3 = new Rpcs3Host(rootFolder: AppPaths.Rpcs3Root);
        LiveSplit = new LiveSplitClient();
        Autosplitter = new Autosplitter(settings, LiveSplit, Post);
        Updates = new UpdateService(settings, Post, AddToast, updatesAllowed);

        // The console pushes run events whether or not anyone is listening; the decision about
        // what any of them means is the autosplitter's, and it is the only subscriber.
        Client.AutosplitEventReceived += ev => Autosplitter.Handle(ev);

        // The reconnect loop knows nothing about webMAN and does not need to: it awaits whatever
        // this hook is before each attempt, and in webMAN mode that is the check-and-load that
        // brings a crashed module back.
        Client.BeforeReconnect = (_, token) => BeforeReconnectAsync(token);

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

            // Not a re-read of everything: if this is the game that was running when the link
            // went, its description is still on screen and still true. The tick decides.
            AdoptSession();
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
            _liveStale = true;

            // What the console described stays: the console still holds it, this client will be
            // back, and a layout that empties itself every time the link hiccups is the thing this
            // build set out to stop. Only what was read out of the running process goes.
            ClearGameViews();
            Panels.PanelState.DropCapture(this);
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

    /// <summary>
    /// The local qwark-rpcs3.exe, for the RPCS3 target. Nothing is started until the Connection
    /// panel (or the startup path) asks for it, so a PS3 session never spawns a process.
    /// </summary>
    public Rpcs3Host Rpcs3 { get; }

    /// <summary>The connection to LiveSplit's TCP server. Only the autosplitter drives it.</summary>
    public LiveSplitClient LiveSplit { get; }

    /// <summary>Turns the console's run events into LiveSplit commands, per the user's settings.</summary>
    public Autosplitter Autosplitter { get; }

    /// <summary>
    /// Looks for a newer RaCMAN Reloaded on GitHub and installs it. Inert unless this copy was
    /// installed by the installer and the run is one that may touch the network.
    /// </summary>
    public UpdateService Updates { get; }

    /// <summary>The SessionInfo the last HELLO returned, for the version readout. Null when offline.</summary>
    public SessionInfo? Hello { get; private set; }

    public TelemetryPacket? Telemetry { get; private set; }

    public SessionInfo Session => Telemetry?.Session ?? Client.LatestSession ?? SessionInfo.Empty;

    public bool Connected => Client.IsConnected;

    public bool Ingame => Connected && Session.State == SessionState.Ingame;

    /// <summary>
    /// The console refuses code patches (RPCS3). Read straight off the session flags, so every
    /// panel that greys a control out agrees with every other one and with the module.
    /// </summary>
    public bool CodePatchesUnsupported => Session.CodePatchesUnsupported;

    /// <summary>qwark reports it is driving an emulator rather than a console.</summary>
    public bool IsEmulator => Session.IsEmulator;

    /// <summary>
    /// True while the connected console runs a qwark build older than the one this client shipped
    /// with. Nothing refuses to work, but its feature tables are the previous build's, so every
    /// panel is a warning away from lying and the header says so.
    /// </summary>
    public bool QwarkStale => Connected && Hello is not null && QwarkClient.IsStaleBuild(Hello.QwarkVersion);

    /// <summary>
    /// The game everything described here belongs to: the running one while a game is running, and
    /// the last one that ran otherwise, because the description outlives the session. Panels key
    /// off this rather than off telemetry's own game, so a quit to the XMB does not empty them.
    /// </summary>
    public GameId DescribedGame => _described.Game;

    /// <summary>The title id that game was running under, for the per-title layout overrides.</summary>
    public string DescribedTitle { get; private set; } = string.Empty;

    public DescribeResult Describe { get; private set; } = DescribeResult.Empty;

    /// <summary>
    /// AUTOSPLIT_DESCRIBE for the running game: what each reason code means. Empty when the game
    /// has no watcher, which the op answers UNSUPPORTED and the panel reads as "nothing to offer".
    /// </summary>
    public AutosplitEventDesc[] AutosplitEvents { get; private set; } = Array.Empty<AutosplitEventDesc>();

    /// <summary>
    /// SAVEFILE_INFO for the running game, revision 1.9: whether the console has a savefile
    /// helper for it, whether that helper is in and running, and how big a save is.
    /// <see cref="SaveFileInfo.None"/> until it has been asked, and again for a game with no
    /// helper or a console that refuses code patches.
    /// </summary>
    public SaveFileInfo SaveFile { get; private set; } = SaveFileInfo.None;

    /// <summary>
    /// The console answered BUSY to SAVEFILE_INFO: since revision 1.11 it waits for a starting
    /// game to finish loading its modules before it writes the helper into it, and refuses the
    /// block until then. It lasts a second or two after a game appears, so it is what separates
    /// "not yet" from <see cref="SaveFileInfo.None"/>'s "this game has none at all", and the Save
    /// files panel keeps asking while it holds.
    /// </summary>
    public bool SaveFileNotReady { get; private set; }

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

    // ---------------------------------------------------------------- the webMAN detour

    /// <summary>
    /// Records that a check-and-load through webMAN has just run, wherever it ran: the Connect
    /// button's own sequence counts for the rate limit below, so pressing Connect and then losing
    /// the console a second later does not send the SPRX twice.
    /// </summary>
    public void NoteWebManDetour() => Interlocked.Exchange(ref _lastDetourMs, Environment.TickCount64);

    /// <summary>
    /// What the reconnect loop does before an attempt. In webMAN mode it asks webMAN whether qwark
    /// is still loaded and sends it when it is not, which is how a console whose module crashed
    /// comes back without anybody pressing anything; in standalone mode, and for the RPCS3 helper
    /// on this PC, there is nothing to ask and it returns at once. At most one detour per
    /// <see cref="Ps3Connect.DetourInterval"/>: the attempts in between are plain reconnects.
    /// </summary>
    private Task BeforeReconnectAsync(CancellationToken token)
    {
        if (Settings.StandaloneConnection || Settings.Rpcs3Target) return Task.CompletedTask;
        if (Client.Host is not { Length: > 0 } host) return Task.CompletedTask;

        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastDetourMs);
        if (!Ps3Connect.ShouldDetour(last == 0 ? null : last, now)) return Task.CompletedTask;

        Interlocked.Exchange(ref _lastDetourMs, now);
        return ReloadThroughWebManAsync(host, token);
    }

    /// <summary>
    /// The detour itself. Only the load is worth a toast: a module webMAN still lists, a webMAN
    /// that cannot be reached and a load that fails are all the same thing from here, a console
    /// that is not answering, and the reconnect countdown on the Connection panel already says so.
    /// A failure is left to the caller to swallow, which is what the hook does with it.
    /// </summary>
    private async Task ReloadThroughWebManAsync(string host, CancellationToken token)
    {
        // Only an answer that says no slot holds it sends the module across. A slot that holds one
        // is a module to leave alone, and a webMAN that cannot be asked (the console is off, which
        // is the usual reason a reconnect loop is running at all) is not an invitation to upload.
        var status = await WebMan.PluginStatusAsync(host, cancellationToken: token).ConfigureAwait(false);
        if (status.State != VshPluginState.NotLoaded) return;

        string sprx = Ps3Connect.ResolveSprx(Settings.SprxPath);
        if (!File.Exists(sprx)) return;

        Post(() => AddToast($"No VSH slot holds {WebManLoader.SprxName}: loading it"));
        await WebMan.LoadAsync(host, sprx, Settings.WebManSlot, cancellationToken: token).ConfigureAwait(false);
        await Task.Delay(Ps3Connect.LoadWait, token).ConfigureAwait(false);
        Post(() => AddToast($"{WebManLoader.SprxName} loaded through webMAN; reconnecting", ToastKind.Success));
    }

    // ---------------------------------------------------------------- plumbing

    public void Post(Action action) => _actions.Enqueue(action);

    public void AddToast(string text, ToastKind kind = ToastKind.Info)
    {
        _toasts.Add(new Toast { Text = text, Kind = kind });
        if (_toasts.Count > 8) _toasts.RemoveRange(0, _toasts.Count - 8);
        if (kind == ToastKind.Error) LastError = text;
    }

    /// <summary>Runs one request off the render thread and turns any non-OK status into a toast.</summary>
    public void Run(Func<Task> operation, string? successMessage = null) =>
        Run(operation, successMessage, quiet: false);

    /// <summary>
    /// The same, for work nobody asked for: the automatic re-reads after a reboot, the panels'
    /// timed table reads, the probes. A status the console is entitled to answer while a game is
    /// starting or ending (NOT_INGAME), or one it uses to say this game has no such table
    /// (UNSUPPORTED, UNKNOWN_OP), says nothing at all, because the user did nothing to be told
    /// about. Every other failure still toasts, and so does every request behind a click.
    /// </summary>
    public void RunQuiet(Func<Task> operation) => Run(operation, null, quiet: true);

    private void Run(Func<Task> operation, string? successMessage, bool quiet)
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
                when (quiet && ex.Status is Status.NotIngame or Status.Unsupported or Status.UnknownOp)
            {
                // Background work: the session moved under it, or this game has no such table.
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

    /// <summary>The same, with the quiet rule chosen at the call site rather than by name.</summary>
    public void Run<T>(Func<Task<T>> operation, Action<T> onSuccess, bool quiet)
    {
        Run(async () =>
        {
            var result = await operation().ConfigureAwait(false);
            Post(() => onSuccess(result));
        }, null, quiet);
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

        // The autosplitter reads settings per game, and it follows the described game rather than
        // telemetry's own: a Deadlocked quit takes the session to the XMB with a pause still open,
        // and the resume that comes back has to be judged by the game that opened it.
        Autosplitter.Game = DescribedGame;
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

        // The process the values came out of, which a quit, a boot and a reboot all replace. The
        // title going empty is the trip to the XMB, and it is a session change like any other:
        // nothing described is touched here, only what was read out of the game.
        bool otherProcess = session.Game != _lastGame
                            || !string.Equals(session.TitleId, _lastTitle, StringComparison.Ordinal);
        bool rebooted = !otherProcess && session.Generation != _lastGeneration;

        if (otherProcess || rebooted)
        {
            // The same title under a new generation is a reboot, which the console handles and the
            // user does not have to be told about; with debug information on it says which one.
            if (rebooted && Panels.Ui.Debug) AddToast($"Game rebooted (generation {session.Generation})");

            _lastGame = session.Game;
            _lastTitle = session.TitleId;
            _lastGeneration = session.Generation;
            _lastPlanet = 0xFF;
            _liveStale = true;
            ClearGameViews();
        }

        if (session.State != _lastState)
        {
            var previous = _lastState;
            _lastState = session.State;

            if (previous == SessionState.Ingame)
            {
                // Side panels never outlive the game: anything read out of game memory goes the
                // moment the session leaves INGAME, and the panels then draw the description they
                // already have with their controls greyed out.
                _lastPlanet = 0xFF;
                _liveStale = true;
                ClearGameViews();
            }
            else if (session.State == SessionState.Ingame)
            {
                _liveStale = true;
            }
        }

        // Nothing is described until the game is up. At the XMB qwark has no game to describe, and
        // during BOOTING its feature table is still the last process's, which is what used to put
        // the previous game's cheats on screen and a "Describe: Unsupported" toast under them.
        if (session.State == SessionState.Ingame && session.Game != GameId.None)
        {
            var key = new DescribedKey(session.Game, session.QwarkVersion, session.ProtocolVersion);
            if (key != _described)
            {
                // A different game, or a module replaced under this one: nothing on screen belongs
                // to it. This is one of the three ways the description is ever thrown away, and
                // the read that follows is what adopts the new one.
                if (!_described.IsEmpty) ResetPanels();
                _liveStale = false;
                RefreshAll();
            }
            else if (_liveStale)
            {
                // The same game again, whatever generation and whatever happened to the link: the
                // layout on screen is still this game's, so only the values are read again.
                _liveStale = false;
                DescribedTitle = session.TitleId;
                RefreshLive();
            }
        }

        if (session.CurrentPlanet != _lastPlanet && session.State == SessionState.Ingame)
        {
            _lastPlanet = session.CurrentPlanet;
            RefreshPositions(quiet: true);
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

    /// <summary>
    /// Everything: the description, the console-owned tables and the panels' own drafts. Only a
    /// different game, a different qwark build or the user's own "Re-read everything" reaches
    /// here. A quit, a reboot and a dropped connection do not, which is the whole of "what the
    /// console described belongs to the game".
    /// </summary>
    public void ResetPanels()
    {
        ForgetDescribed();
        Watches = Array.Empty<WatchEntry>();
        Freezes = Array.Empty<FreezeEntry>();
        Patches = Array.Empty<PatchEntry>();
        ConsoleMods = Array.Empty<ModEntry>();
        Combos = Array.Empty<ComboEntry>();
        LocalMods = Array.Empty<LocalMod>();
        Previous = null;
        PreviousModalRequested = false;
        ClearGameViews();
        Panels.PanelState.ResetAll(this);
    }

    /// <summary>Drops the description itself, and with it the key that says whose it was.</summary>
    private void ForgetDescribed()
    {
        _described = default;
        DescribedTitle = string.Empty;
        Describe = DescribeResult.Empty;
        AutosplitEvents = Array.Empty<AutosplitEventDesc>();
        SaveFile = SaveFileInfo.None;
        SaveFileNotReady = false;
        Autosplitter.Descriptors = AutosplitEvents;
        Autosplitter.Game = GameId.None;
        Planets = Array.Empty<string>();
        Autosplitter.PlanetNames = Planets;
        EnumOptions.Clear();
        Unlocks = UnlockList.Empty;
        UnlocksUnsupported = false;
        LevelFlagsUnsupported = false;
    }

    /// <summary>
    /// Takes the running game as the one the description belongs to. The autosplitter is moved
    /// with it rather than following telemetry, so the two never disagree about which game's
    /// settings and route are in force.
    /// </summary>
    private void AdoptDescribed(DescribedKey key, string title)
    {
        _described = key;
        DescribedTitle = title;
        Autosplitter.Game = key.Game;
    }

    /// <summary>
    /// Drops everything that was read out of the running game's memory: position slots, the unlock
    /// rows, the level-flag bytes, the moby rows and the memory dump. What the console described is
    /// not in here — the feature table, the planet list, the autosplit rows, the unlock table's
    /// categories and slot descriptors — because that describes the game rather than the process,
    /// and the same game is usually what comes back.
    /// </summary>
    public void ClearGameViews()
    {
        Positions = PositionList.Empty;

        // The categories and the slot descriptors are the table's shape, which the console
        // described for this game; only the rows came out of the process that is going away.
        Unlocks = Unlocks with { Unlocks = Array.Empty<Unlock>() };
        Panels.PanelState.ClearGameData();
    }

    /// <summary>
    /// What the reconnect does: move the change trackers to the session that just came up and let
    /// the next Tick decide. If it is the game that was running when the link went, its
    /// description is still on screen and still true, and only the values are read again.
    /// </summary>
    public void AdoptSession()
    {
        var session = Session;
        _lastGame = session.Game;
        _lastTitle = session.TitleId;
        _lastGeneration = session.Generation;
        _lastState = session.State;
        _lastPlanet = 0xFF;
        _liveStale = true;
    }

    /// <summary>
    /// Throws the description away and reads all of it back: the Connection panel's "Re-read
    /// everything" and the Settings panel's reload, and nothing automatic. The user asked, so the
    /// statuses that come back are the user's to see.
    /// </summary>
    public void ForceRefresh()
    {
        ResetPanels();
        AdoptSession();
        RefreshAll(quiet: false);
        _liveStale = false;
    }

    /// <summary>
    /// The description and the values together, and the one place the description is read. The
    /// game it belongs to is adopted here, because DESCRIBE only answers for the running game
    /// while a game is running: outside INGAME it is refused or answered with the last one's
    /// table, so nothing is asked and the panels keep what they have.
    /// </summary>
    public void RefreshAll(bool quiet = true)
    {
        if (!Connected) return;

        if (Ingame)
        {
            var session = Session;
            var key = new DescribedKey(session.Game, session.QwarkVersion, session.ProtocolVersion);

            // Adopted before the answer comes back, so the next tick does not ask a second time.
            AdoptDescribed(key, session.TitleId);

            Run(async () =>
            {
                try
                {
                    var describe = await Client.DescribeAsync().ConfigureAwait(false);
                    Post(() =>
                    {
                        Describe = describe;
                        EnumOptions.Clear();
                        foreach (var feature in describe.Features.Where(f => f.Kind == FeatureKind.Enum))
                        {
                            byte id = feature.Id;
                            Run(() => Client.FeatureOptionsAsync(id), options => EnumOptions[id] = options, quiet);
                        }
                    });
                }
                catch (QwarkStatusException)
                {
                    // Almost always the game ending between the decision and the request. Nothing
                    // was described, so the key goes back and the next tick that finds a game asks
                    // again; a refusal while the game is still up is left rather than retried at
                    // frame rate, and the Connection panel's re-read is there for it.
                    Post(() =>
                    {
                        if (!Ingame && _described == key) _described = default;
                    });

                    throw;
                }
            }, null, quiet);

            Run(() => Client.PlanetListAsync(), planets =>
            {
                Planets = planets;
                Autosplitter.PlanetNames = planets;
            }, quiet);

            RefreshAutosplitEvents();
            RefreshSaveFileInfo();
            RefreshLevelFlagsSupport(quiet);
        }

        RefreshLive(quiet);
    }

    /// <summary>
    /// The values, not the layout: what the console holds right now for a game whose description
    /// is already on screen. This is what a reboot, a re-entry to INGAME and a reconnect run, and
    /// nobody pressed anything to ask for it, so it is quiet by default.
    /// </summary>
    public void RefreshLive(bool quiet = true)
    {
        if (!Connected) return;

        // The slots come out of the running process and are refused outside it; everything below
        // is the console's own and answers whatever the session is doing.
        if (Ingame) RefreshPositions(quiet);
        RefreshWatches(quiet);
        RefreshFreezes(quiet);
        RefreshPatches(quiet);
        RefreshMods(quiet);
        RefreshCombos(quiet);
    }

    /// <summary>
    /// Probes LEVELFLAGS_GET once so the nav can hide the Level flags panel for a game that has no
    /// flag table (Deadlocked). UNSUPPORTED means hide; anything else (bytes, or BAD_ARG for a
    /// placeholder planet) means the game has them.
    /// </summary>
    public void RefreshLevelFlagsSupport(bool quiet = true)
    {
        // Only INGAME: anywhere else the op answers NOT_INGAME, which says nothing about whether
        // the game has flags and would flip the answer back for a game that has none.
        if (!Ingame) return;

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
        }, null, quiet);
    }

    /// <summary>
    /// Re-reads AUTOSPLIT_DESCRIBE. UNSUPPORTED is the normal answer for a game qwark has no
    /// autosplitter for, and for a module older than revision 1.4 the op is unknown; both mean the
    /// panel has no per-game rows to draw, and neither is worth a toast.
    /// </summary>
    public void RefreshAutosplitEvents()
    {
        if (!Connected) return;

        RunQuiet(async () =>
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

    /// <summary>
    /// Re-reads SAVEFILE_INFO, revision 1.9. UNSUPPORTED is the normal answer on a console that
    /// refuses code patches, and for a module older than this revision the op is unknown; both
    /// mean the Save files panel has nothing to drive, and neither is worth a toast. BUSY is the
    /// answer while the game is still loading its modules and qwark has not written the helper
    /// into it yet (revision 1.11): a second or two after a game appears, and the panel asks
    /// again every second until it is in.
    /// </summary>
    public void RefreshSaveFileInfo()
    {
        if (!Connected) return;

        RunQuiet(async () =>
        {
            try
            {
                var info = await Client.SaveFileInfoAsync().ConfigureAwait(false);
                Post(() =>
                {
                    SaveFile = info;
                    SaveFileNotReady = false;
                });
            }
            catch (QwarkStatusException ex) when (ex.Status is Status.Busy)
            {
                Post(() =>
                {
                    SaveFile = SaveFileInfo.None;
                    SaveFileNotReady = true;
                });
            }
            catch (QwarkStatusException ex) when (ex.Status is Status.Unsupported or Status.UnknownOp
                                                             or Status.NotIngame)
            {
                Post(() =>
                {
                    SaveFile = SaveFileInfo.None;
                    SaveFileNotReady = false;
                });
            }
        });
    }

    public void RefreshPositions(bool quiet = false)
    {
        if (Connected) Run(() => Client.PosListAsync(), positions => Positions = positions, quiet);
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

    public void RefreshWatches(bool quiet = false)
    {
        if (Connected) Run(() => Client.WatchListAsync(), watches => Watches = watches, quiet);
    }

    public void RefreshFreezes(bool quiet = false)
    {
        if (Connected) Run(() => Client.FreezeListAsync(), freezes => Freezes = freezes, quiet);
    }

    public void RefreshPatches(bool quiet = false)
    {
        if (Connected) Run(() => Client.PatchListAsync(), patches => Patches = patches, quiet);
    }

    public void RefreshCombos(bool quiet = false)
    {
        if (Connected) Run(() => Client.ComboListAsync(), combos => Combos = combos, quiet);
    }

    public void RefreshMods(bool quiet = false)
    {
        if (!Connected) return;

        Run(() => Client.ModListAsync(), mods => ConsoleMods = mods, quiet);
        RescanLocalMods();
    }

    /// <summary>
    /// The local library for the game whose description is on screen, which at the XMB is the game
    /// that just quit rather than no game at all.
    /// </summary>
    public void RescanLocalMods()
    {
        string title = Session.TitleId is { Length: > 0 } running ? running : DescribedTitle;
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
        string line = $"{session.State.DisplayName()} | {title} | {session.Game.DisplayName()}";

        // qwark stops sending while the console hands over to a game, and this client stops asking
        // with it. Nothing is updating and nothing is wrong, so the line says which of the two it
        // is rather than leaving a frozen readout to speak for itself.
        if (Client.TelemetryQuiet) line += " | the game is starting";

        if (!Panels.Ui.Debug) return line;

        string tick = session.Tick > 0 ? $"tick {session.Tick}" : "tick -";
        return line + $" | generation {session.Generation} | {tick}"
                    + $" | qwark v{session.QwarkVersion} protocol {session.ProtocolVersion}";
    }

    public void Dispose()
    {
        LiveSplit.Dispose();
        Client.Dispose();

        // Last, so the client has already dropped its connection when the helper is asked to go.
        Rpcs3.Dispose();
    }
}
