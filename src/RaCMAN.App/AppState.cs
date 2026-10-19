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
/// panels draw it with their controls greyed out until the session is INGAME again. A different
/// game, a different qwark build or the user asking for a re-read replaces it, and so does a
/// module that describes the game differently under the same build number, which is what every
/// new connection reads DESCRIBE once to find out (<see cref="CheckDescription"/>). Anything read
/// out of the running process goes the moment that process does; see <see cref="ClearGameViews"/>.
/// </para>
/// </summary>
public sealed class AppState : IDisposable
{
    /// <summary>
    /// Which game the described data on this object belongs to, and which module described it. A
    /// build that is replaced under a running game describes it differently, so it is part of the
    /// key rather than something to notice later. A module replaced by another of the same build
    /// number is not in it, and cannot be: that is what <see cref="CheckDescription"/> is for.
    /// </summary>
    private readonly record struct DescribedKey(GameId Game, byte QwarkVersion, byte ProtocolVersion)
    {
        public bool IsEmpty => Game == GameId.None;

        public static DescribedKey Of(SessionInfo session) =>
            new(session.Game, session.QwarkVersion, session.ProtocolVersion);
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

    /// <summary>
    /// Set when a background read was held back because the console was busy with a launch. The
    /// first tick that finds the session INGAME again reads back what was held, once.
    /// </summary>
    private bool _heldBack;

    /// <summary>
    /// Set by every connection that comes up, and cleared by the first DESCRIBE that connection
    /// sends, a check or a key change's read. Only a new connection can bring a new module: the
    /// console has to restart, or the module be reloaded, for one to replace another, and either
    /// takes the link down with it. So the description is read again once per connection, at the
    /// first tick that finds the game INGAME on it, and never on a quit or a reboot.
    /// </summary>
    private bool _describeOwed;

    /// <summary>A check's DESCRIBE is out: one at a time, so a slow answer is never asked twice.</summary>
    private bool _describeChecking;

    /// <summary>
    /// Counts the connections that have come up, so an answer that left on one is never taken for
    /// the next one's: the module on the other end may not be the same.
    /// </summary>
    private int _connection;

    /// <summary>
    /// Moves on every time the description is adopted or dropped, so a check that left before one
    /// of those compares nothing with a description it was not asked about.
    /// </summary>
    private int _describedEpoch;

    private int _inFlight;

    /// <summary>
    /// When the last check-and-load through webMAN ran, on <see cref="Environment.TickCount64"/>,
    /// or 0 when none has. It is what rate-limits the reconnect loop's detour.
    /// </summary>
    private long _lastDetourMs;

    /// <summary>
    /// Set once the standalone module update has been started on this connection, whether it went
    /// through or not. One attempt per connection: a console that refused the file ops, or lost
    /// them half way, will refuse them again a frame later, and the next connection is soon enough
    /// to try again.
    /// </summary>
    private bool _sprxUpdateTried;

    /// <summary>
    /// Which qwark the staged module was written to. The restart notice belongs to that console and
    /// must not follow the client to another one, or to the RPCS3 helper on this PC.
    /// </summary>
    private string _sprxUpdateTarget = string.Empty;

    /// <summary>
    /// <paramref name="updatesAllowed"/> is off by default so that nothing which merely constructs
    /// an AppState — a test, a tool — can reach GitHub; only Program turns it on, and only for a
    /// run none of the headless flags apply to.
    /// </summary>
    public AppState(Settings settings, bool updatesAllowed = false)
    {
        Settings = settings;

        // The chosen target's reconnect switch; SwitchTarget hands the client the other one's.
        Client = new QwarkClient { AutoReconnect = settings.AutoReconnect };

        // The user's own files are in the data folder; the shipped mod library is beside the
        // executable, where the updater is free to replace it. A retired mod an older release left
        // there is the one thing in it that is not offered (ModLibrary.IsShippedFolder).
        Mods = new ModLibrary(AppPaths.InData(settings.ModsPath), AppPaths.ShippedMods,
            retired: (title, folder) => LegacyModExclusions.Shipped.Excludes(title, folder));
        Watchlists = new WatchlistStore(AppPaths.Watchlists);
        ColourPresets = new ColourPresetStore(AppPaths.Colours);
        PositionNames = new PositionNameStore(AppPaths.Positions);
        SaveFiles = new SaveFileLibrary(AppPaths.InData(settings.SaveFilesPath));
        PremadeSaveFiles = new PremadeSaveFiles(AppPaths.ShippedSaveFiles);
        WebMan = new WebManLoader();
        Rpcs3 = new Rpcs3Host(qwarkPort: settings.Rpcs3.HelperPort, rootFolder: AppPaths.Rpcs3Root);
        Rpcs3Patch = new Rpcs3PatchController(this, Rpcs3Environment.Current);
        Rpcs3Mods = new Rpcs3ModsController(this, Rpcs3Environment.Current);
        LiveSplit = new LiveSplitClient();
        Autosplitter = new Autosplitter(settings, LiveSplit, Post);

        // Nothing is pointed at LiveSplit at startup any more. Whether this client goes looking
        // for the server is the probe's question, it is asked every frame rather than once here,
        // and it is answered without a popup while the answer is "not yet"; the Connect button on
        // the Autosplitter panel is the other way in, for a user who has the switch off.
        LiveSplitProbe = new LiveSplitProbe(settings, LiveSplit, Post);
        Updates = new UpdateService(settings, Post, AddToast, updatesAllowed);
        ObsPad = new ObsPadServer(new SettingsPadSkins(settings));

        // The pad an overlay draws is the pad in the packet, so it is taken where the packet
        // arrives rather than a frame later: this runs on the network thread, copies six values
        // and queues them, and touches nothing else on this object.
        Client.TelemetryReceived += packet => ObsPad.Publish(PadSnapshot.From(packet.Session));

        // The console pushes run events whether or not anyone is listening; the decision about
        // what any of them means is the autosplitter's, and it is the only subscriber.
        Client.AutosplitEventReceived += ev => Autosplitter.Handle(ev);

        // The reconnect loop knows nothing about webMAN and does not need to: it awaits whatever
        // this hook is before each attempt, and in webMAN mode that is the check-and-load that
        // brings a crashed module back.
        Client.BeforeReconnect = (_, token) => BeforeReconnectAsync(token);

        Client.SessionEstablished += info => Post(() =>
        {
            Hello = info;
            AddToast($"Connected: {(string.IsNullOrEmpty(info.TitleId) ? "no game" : info.TitleId)}", ToastKind.Success);

            // A new connection, so the standalone update gets its one attempt again.
            _sprxUpdateTried = false;

            // And the manip events start again from whatever the console holds now: the module on
            // the other end may be a new one, whose event numbers mean nothing next to the old
            // one's (see ManipEvents).
            _manipEvents.Forget();

            // The VSH figures are the console's at the time it answered, and this may be another
            // console or a module that has restarted: nothing of the last connection's is shown,
            // and the first tick that finds flags.VSH_MEMORY on this one asks at once.
            ForgetVshMemory();
            _vshMemoryLive = true;
            _sinceVshMemory = VshMemoryPollSeconds;

            // The restart notice is done with when the console comes back running the build that
            // was put on it, and it belongs to that console: connecting to another one, or to the
            // helper on this PC, has nothing staged on it to restart for.
            if (QwarkUpdateStaged != 0
                && (!QwarkClient.IsStaleBuild(info.QwarkVersion)
                    || !string.Equals(ConnectedTo, _sprxUpdateTarget, StringComparison.OrdinalIgnoreCase)))
            {
                QwarkUpdateStaged = 0;
            }

            // The panels would silently show the old module's feature tables, so say it up front.
            if (QwarkClient.IsStaleBuild(info.QwarkVersion) && QwarkUpdateStaged == 0)
            {
                AddToast("qwark.sprx on the console is older than this client; see the Connection panel");
            }

            // Not a re-read of everything: if this is the game that was running when the link
            // went, its description is still on screen and probably still true. The tick decides,
            // and the one DESCRIBE this connection owes is how it finds out whether the module
            // that answered is one that describes the game the same way. The build number cannot
            // say: a module rebuilt under the same one, as every build is until it is released,
            // reports the same key with different rows.
            _connection++;
            _describeOwed = true;
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
            ForgetVshMemory();

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

    /// <summary>What this PC calls the console's position slots, one file per game.</summary>
    public PositionNameStore PositionNames { get; }

    /// <summary>The PC-side savefile library, <c>savefiles/&lt;TITLEID&gt;/&lt;category&gt;/</c>.</summary>
    public SaveFileLibrary SaveFiles { get; }

    /// <summary>The savefiles the release ships, beside the executable: read, loaded, copied, never written.</summary>
    public PremadeSaveFiles PremadeSaveFiles { get; }

    public WebManLoader WebMan { get; }

    /// <summary>
    /// The local qwark-rpcs3.exe, for the RPCS3 target. Nothing is started until the Connection
    /// panel (or the startup path) asks for it, so a PS3 session never spawns a process.
    /// </summary>
    public Rpcs3Host Rpcs3 { get; }

    /// <summary>
    /// qwark's own patches under RPCS3, the savefile helper and the code switches: what the
    /// Connection panel says about them, and the install behind its confirmation. Inert until that
    /// panel is drawn with the RPCS3 target.
    /// </summary>
    public Rpcs3PatchController Rpcs3Patch { get; }

    /// <summary>
    /// The mods as RPCS3 patches: what the Mods panel says about each one under RPCS3, and the
    /// writes behind its Enabled checkboxes. Inert until that panel is drawn for RPCS3.
    /// </summary>
    public Rpcs3ModsController Rpcs3Mods { get; }

    /// <summary>
    /// The one way RPCS3's patch file and patch_config.yml are written, shared by the install of
    /// qwark's patches and the Mods panel so neither writes the other's change away.
    /// </summary>
    public Rpcs3PatchWriter Rpcs3Writer { get; } = new();

    /// <summary>The connection to LiveSplit's TCP server. Only the autosplitter drives it.</summary>
    public LiveSplitClient LiveSplit { get; }

    /// <summary>Turns the console's run events into LiveSplit commands, per the user's settings.</summary>
    public Autosplitter Autosplitter { get; }

    /// <summary>
    /// Finds LiveSplit's server by itself while the autosplitter is on and the switch for it is
    /// ticked, and says nothing at all while it is not there. It is the only thing that connects
    /// on nobody's press; see <see cref="LiveSplitProbe"/> for why it is quiet.
    /// </summary>
    public LiveSplitProbe LiveSplitProbe { get; }

    /// <summary>
    /// Looks for a newer RaCMAN Reloaded on GitHub and installs it. Inert unless this copy was
    /// installed by the installer and the run is one that may touch the network.
    /// </summary>
    public UpdateService Updates { get; }

    /// <summary>
    /// Serves the input display as a page for OBS. Created here so telemetry can reach it, but
    /// nothing listens until <see cref="SyncObsPad"/> is called with the settings that say so,
    /// which is the window's own frame and never a test's.
    /// </summary>
    public ObsPadServer ObsPad { get; }

    /// <summary>
    /// Puts the OBS listener where the settings ask for it: on, off, or on another port. Once a
    /// frame, from the render thread, because the settings panel is what changes those.
    /// </summary>
    public void SyncObsPad() => ObsPad.Apply(Settings.ObsPadEnabled, Settings.ObsPadPort);

    /// <summary>The SessionInfo the last HELLO returned, for the version readout. Null when offline.</summary>
    public SessionInfo? Hello { get; private set; }

    public TelemetryPacket? Telemetry { get; private set; }

    public SessionInfo Session => Telemetry?.Session ?? Client.LatestSession ?? SessionInfo.Empty;

    public bool Connected => Client.IsConnected;

    public bool Ingame => Connected && Session.State == SessionState.Ingame;

    /// <summary>
    /// The console is INGAME on a title qwark has no game module for: it reports game 0 with the
    /// title id filled in (build 36). The memory ops work on such a session and every game op —
    /// DESCRIBE, the lists, the tables, the unlocks, the mods, the save files, the combos, the
    /// autosplit rows — answers UNSUPPORTED, so none of them is asked for and the panels that draw
    /// them are not in the nav. What is left is the Memory panel, which is the whole point.
    /// </summary>
    public bool UnknownGame => Ingame && Session.Game == GameId.None;

    /// <summary>
    /// True while the console is refusing everything but the control ops. PROTOCOL.md section
    /// 1.1: through BOOTING and through QUITTING qwark allocates no request buffers, so every
    /// list, describe, info and event read is drained and answered BUSY until the session is
    /// INGAME. Nothing automatic is sent while this is set — the burst of refusals a launch used to
    /// produce was a burst of toasts — and what was held back is read on the way back in.
    /// </summary>
    public bool ConsoleBusy => Connected && Session.State is SessionState.Booting or SessionState.Quitting;

    /// <summary>
    /// The console refuses code patches (RPCS3). Read straight off the session flags, so every
    /// panel that greys a control out agrees with every other one and with the module.
    /// </summary>
    public bool CodePatchesUnsupported => Session.CodePatchesUnsupported;

    /// <summary>
    /// qwark-rpcs3 finds the running game's code switches in memory (build 50), so the features
    /// that patch game code work under RPCS3 too. Off the session flags, like the one above.
    /// </summary>
    public bool CodeSwitches => Session.CodeSwitches;

    /// <summary>qwark reports it is driving an emulator rather than a console.</summary>
    public bool IsEmulator => Session.IsEmulator;

    /// <summary>
    /// True while the connected console runs a qwark build older than the one this client shipped
    /// with. Nothing refuses to work, but its feature tables are the previous build's, so every
    /// panel is a warning away from lying and the header says so.
    /// </summary>
    public bool QwarkStale => Connected && Hello is not null && QwarkClient.IsStaleBuild(Hello.QwarkVersion);

    /// <summary>
    /// The build this client has put on the console's boot path and the console has not loaded yet,
    /// or 0 when there is none. A VSH plugin is read once, at boot, so the file being in place says
    /// nothing until the console has been restarted: the notice therefore outlives the connection
    /// that wrote it, and only a HELLO reporting a build that is no longer stale clears it.
    /// </summary>
    public byte QwarkUpdateStaged { get; private set; }

    /// <summary>The one sentence the header and the Connection panel both show for that.</summary>
    public string QwarkUpdateNotice =>
        $"{WebManLoader.SprxName} {QwarkUpdateStaged} uploaded - reboot to update";

    /// <summary>Which qwark this client is talking to, as the staged notice keys off it.</summary>
    private string ConnectedTo => $"{Client.Host}:{Client.Port}";

    /// <summary>
    /// The game everything described here belongs to: the running one while a game is running, and
    /// the last one that ran otherwise, because the description outlives the session. Panels key
    /// off this rather than off telemetry's own game, so a quit to the XMB does not empty them.
    /// </summary>
    public GameId DescribedGame => _described.Game;

    /// <summary>The title id that game was running under, for the per-title layout overrides.</summary>
    public string DescribedTitle { get; private set; } = string.Empty;

    /// <summary>
    /// The title this side's per-title files belong to: the running one while a game is running,
    /// and the last described one otherwise, so a quit to the XMB does not swap the watchlists and
    /// the mod library for another title's. A title qwark has no game module for describes nothing,
    /// and this is what keeps its watchlists keyed by the id the console does report.
    /// </summary>
    public string CurrentTitle => Session.TitleId is { Length: > 0 } running ? running : DescribedTitle;

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
    /// helper or a module that refuses the block. Under RPCS3 (revision 1.15) <c>installed</c> is
    /// whether the RPCS3 patch put the helper in at this boot, which nothing else can.
    /// </summary>
    public SaveFileInfo SaveFile { get; private set; } = SaveFileInfo.None;

    public string[] Planets { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// The order PLANET_LIST says a menu lists <see cref="Planets"/> in (build 53): ids, every one
    /// once, in the order the game visits them. The ids in turn for a game whose numbering is its
    /// order, and for a module from before the console sent one.
    /// </summary>
    public int[] PlanetOrder { get; private set; } = Array.Empty<int>();

    public WatchEntry[] Watches { get; private set; } = Array.Empty<WatchEntry>();

    public FreezeEntry[] Freezes { get; private set; } = Array.Empty<FreezeEntry>();

    public PatchEntry[] Patches { get; private set; } = Array.Empty<PatchEntry>();

    public ModEntry[] ConsoleMods { get; private set; } = Array.Empty<ModEntry>();

    /// <summary>
    /// The folders DIR_LIST found in the console's mod folder for a title, with their whole names,
    /// or null until it has been read. MOD_LIST carries only the first 31 bytes of a folder name, so
    /// this is what the Mods panel pairs its rows with (<see cref="ModRows"/>).
    /// </summary>
    public ConsoleModFolders? ConsoleFolders { get; private set; }

    public ComboEntry[] Combos { get; private set; } = Array.Empty<ComboEntry>();

    public PositionList Positions { get; private set; } = PositionList.Empty;

    public UnlockList Unlocks { get; private set; } = UnlockList.Empty;

    /// <summary>Set once when UNLOCK_LIST answers UNSUPPORTED, so the toast is not repeated.</summary>
    public bool UnlocksUnsupported { get; private set; }

    /// <summary>Set when LEVELFLAGS_GET answers UNSUPPORTED (e.g. Deadlocked), so the nav can hide it.</summary>
    public bool LevelFlagsUnsupported { get; private set; }

    /// <summary>
    /// MANIP_LIST for the described game (revision 1.21): its category types, the selection, the
    /// two options and the last event, all of them the console's. Null hides the manips area: not
    /// read yet, or answered UNSUPPORTED (a game with no groups) or UNKNOWN_OP (a module older than
    /// build 53). Kept through a quit and a dropped link like the description, so the area stays
    /// on screen greyed out; a different game drops it.
    /// </summary>
    public ManipList? Manips { get; private set; }

    /// <summary>How many manip events this run has toasted, for the headless summary.</summary>
    public int ManipToasts { get; private set; }

    /// <summary>
    /// How many times this run has thrown the panels away, drafts and all (<see cref="ResetPanels"/>),
    /// for the headless summary: what tells a module change from a reconnect that kept everything.
    /// </summary>
    public int PanelResets { get; private set; }

    /// <summary>
    /// How many of the checks new connections owe (<see cref="CheckDescription"/>) have been
    /// answered, and how many of those found a description other than the one on screen.
    /// </summary>
    public int DescriptionChecks { get; private set; }

    public int DescriptionsReplaced { get; private set; }

    private readonly ManipEvents _manipEvents = new();

    /// <summary>Render-thread flags: a MANIP_LIST is out, and another was asked for meanwhile.</summary>
    private bool _manipsInFlight;

    private bool _manipsAgain;

    /// <summary>Seconds since the last timed MANIP_LIST; see <see cref="ManipPollSeconds"/>.</summary>
    private float _sinceManipPoll;

    /// <summary>
    /// How often MANIP_LIST is read while a game with manip groups is up, on whatever panel. qwark
    /// applies them, or refuses to, at the start of a run, and telemetry carries nothing that says
    /// one has started: in RaC1, RaC2 and UYA it is the end of a load into the first planet, which
    /// comes seconds after the planet number changed, or with no change at all when a save is loaded
    /// on that planet (how runners reset, and how the premade saves are used); in Deadlocked it is a
    /// flag no packet has. MANIP_LIST is answered on the console's network thread and is a few
    /// hundred bytes, so asking every second and a half costs nothing worth saving, and an event is
    /// toasted within that of happening.
    /// </summary>
    public const float ManipPollSeconds = 1.5f;

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

    // ---------------------------------------------------------------- the target

    /// <summary>
    /// The Connection panel's PS3 / RPCS3 choice. <see cref="Settings.Target"/> is the only value
    /// that changes: each target keeps its own section, so neither one's address, ports or folder
    /// is touched, and the client takes up the reconnect switch of the target it now serves. The
    /// connection that is up belongs to the other target, so it goes with the choice.
    /// </summary>
    public void SwitchTarget(bool rpcs3)
    {
        if (Settings.Rpcs3Target == rpcs3) return;

        Settings.Rpcs3Target = rpcs3;
        Settings.Save();
        Client.AutoReconnect = Settings.AutoReconnect;

        if (Client.WantsConnection)
        {
            Run(() => Client.DisconnectAsync());
            ResetPanels();
        }
    }

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
        if (Settings.Rpcs3Target || Settings.Ps3.StandaloneConnection) return Task.CompletedTask;
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

        string sprx = Ps3Connect.ResolveSprx(Settings.Ps3.SprxPath);
        if (!File.Exists(sprx)) return;

        Post(() => AddToast($"No VSH slot holds {WebManLoader.SprxName}: loading it"));
        await WebMan.LoadAsync(host, sprx, Settings.Ps3.WebManSlot, cancellationToken: token).ConfigureAwait(false);
        await Task.Delay(Ps3Connect.LoadWait, token).ConfigureAwait(false);
        Post(() => AddToast($"{WebManLoader.SprxName} loaded through webMAN; reconnecting", ToastKind.Success));
    }

    // ---------------------------------------------------------------- the standalone module update

    /// <summary>
    /// Standalone mode has no webMAN to send a new module through, and the console loads qwark.sprx
    /// itself at boot, so an out-of-date console is brought up to date by the running module writing
    /// the file that will replace it: <see cref="SprxUpdate"/> puts the shipped SPRX at the boot
    /// path, and the console loads it at its next start. Until then the module in memory is still
    /// the old one, which is why the stale warning stays and only its wording changes.
    /// <para>
    /// The conditions are all "would this be rude, or refused": webMAN mode already sends and loads
    /// the module itself and is left alone, a launch answers the file ops BUSY, a savefile transfer
    /// owns the link for as long as two megabytes take, and a console that is ahead of this client
    /// is never written over because only a stale build gets here at all.
    /// </para>
    /// </summary>
    private void MaybeUpdateConsoleModule(SessionInfo session)
    {
        if (_sprxUpdateTried || QwarkUpdateStaged != 0) return;
        if (Settings.Rpcs3Target || !Settings.Ps3.StandaloneConnection) return;
        if (!QwarkStale) return;
        if (session.State is not (SessionState.Xmb or SessionState.Ingame)) return;
        if (Panels.SaveFilesPanel.Busy || SaveFile.TransferPending) return;

        // No module beside this client is nothing to report: a build from a source tree has none,
        // and the user has done nothing to be told about.
        string sprx = Ps3Connect.ResolveSprx(Settings.Ps3.SprxPath);
        if (!File.Exists(sprx)) return;

        // Before the work starts, so a failure does not come back a frame later for another go.
        _sprxUpdateTried = true;
        UpdateConsoleModule(sprx);
    }

    /// <summary>
    /// Sends <paramref name="sprxPath"/> to the console's boot path through qwark's file ops. The
    /// automatic path above and the Connection panel's debug button both come here, and a failure
    /// at any step is one error toast that names the step and leaves the boot copy as it was.
    /// </summary>
    public void UpdateConsoleModule(string sprxPath)
    {
        // The shipped SPRX is the build this client expects, which is what the console will report
        // once it has restarted; that is the number the notice carries.
        byte staged = QwarkClient.ExpectedQwarkBuild;

        Run(async () =>
        {
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(sprxPath).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Post(() => AddToast(
                    $"Updating {WebManLoader.SprxName} on the console failed while reading {sprxPath}: {error.Message}",
                    ToastKind.Error));
                return;
            }

            try
            {
                var result = await SprxUpdate.RunAsync(Client, bytes,
                    new Progress<string>(message => Post(() =>
                    {
                        if (Panels.Ui.Debug) AddToast(message);
                    }))).ConfigureAwait(false);

                Post(() =>
                {
                    QwarkUpdateStaged = staged;
                    _sprxUpdateTarget = ConnectedTo;
                    AddToast(QwarkUpdateNotice, ToastKind.Success);
                    if (Panels.Ui.Debug && result.ReplacedACopy)
                    {
                        AddToast($"The module it replaced is at {result.BackupPath}");
                    }
                });
            }
            catch (SprxUpdateException error)
            {
                // The message already names the step and carries what the console said, so this is
                // the whole of what the user is told, once.
                Post(() => AddToast(
                    $"Updating {WebManLoader.SprxName} on the console failed while {error.Message}",
                    ToastKind.Error));
            }
        });
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
    /// starting or ending (NOT_INGAME, and the BUSY of section 1.1), or one it uses to say this
    /// game has no such table (UNSUPPORTED, UNKNOWN_OP), says nothing at all, because the user did
    /// nothing to be told about. Every other failure still toasts, and so does every request behind
    /// a click. <see cref="StatusToast"/> is the whole of that decision.
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
            {
                string? text = StatusToast.For(ex.Status, ex.Opcode, Session.State, quiet, Panels.Ui.Debug);

                // Nothing to say out loud. With debug information on it is still worth a line on
                // the console the client was started from: a run of these is what a launch looks
                // like from this side, and there is no other trace of one.
                if (text is null)
                {
                    if (Panels.Ui.Debug) Console.WriteLine($"quiet {ex.Opcode}: {ex.Status}");
                }
                else
                {
                    Post(() => AddToast(text, ToastKind.Error));
                }
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

        // Looking for LiveSplit is about this PC and not about the console, so it runs whether or
        // not a console is connected — a runner who opens LiveSplit before the game finds the link
        // already up when the game arrives.
        LiveSplitProbe.Tick(deltaSeconds);

        // With autosplitting off nothing acts on a run event, so the console is not polled for one.
        // When the ring can be read at all — INGAME, and a game the module knows — is QwarkClient's
        // own rule and is not repeated here.
        Client.AutosplitSafetyPoll = Settings.Autosplit.Enabled;

        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            _toasts[i].Remaining -= deltaSeconds;
            if (_toasts[i].Remaining <= 0) _toasts.RemoveAt(i);
        }

        if (!Connected)
        {
            // The Disconnect button and a change of target take the link down without raising
            // Disconnected, and the VSH figures go with the link however it went.
            ForgetVshMemory();
            return;
        }

        var session = Session;

        // Nothing to do with the game either: the console's own memory, read on a timer whatever
        // the session is doing (see VshMemoryPollSeconds).
        PollVshMemory(session, deltaSeconds);

        // Nothing to do with the game: a standalone console whose module is older than this client
        // has the new one written to its boot path, once, as soon as the session is somewhere the
        // file ops will be answered.
        MaybeUpdateConsoleModule(session);

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
            var key = DescribedKey.Of(session);
            if (key != _described)
            {
                // A different game, or a module replaced under this one: nothing on screen belongs
                // to it. This is one of the ways the description is ever thrown away, and the read
                // that follows is what adopts the new one.
                if (!_described.IsEmpty) ResetPanels();
                _liveStale = false;

                // RefreshAll asks for every one of them anyway.
                _heldBack = false;
                RefreshAll();
            }
            else
            {
                // A new connection owes one DESCRIBE, asked the first tick it finds the game: the
                // module that answered may not be the one that described what is on screen.
                bool check = _describeOwed && !_describeChecking;

                if (_liveStale)
                {
                    // The same game again, whatever generation and whatever happened to the link:
                    // the layout on screen is still this game's, so only the values are read again.
                    _liveStale = false;
                    DescribedTitle = session.TitleId;
                    RefreshLive();

                    // And the console-side probes a launch refused, which RefreshLive does not
                    // carry and nothing else would ask for again. Once: the flag is what stops a
                    // game that boots and quits and boots from reading them three times over. A
                    // check reads both of them itself.
                    if (check)
                    {
                        _heldBack = false;
                    }
                    else if (_heldBack)
                    {
                        _heldBack = false;
                        RefreshAutosplitEvents();
                        RefreshSaveFileInfo();
                    }
                    else if (CodePatchesUnsupported)
                    {
                        // Under RPCS3 the helper is in the game only if the RPCS3 patch was applied
                        // when this boot loaded the executable, so every boot has its own answer,
                        // and the restart the patch asks for is exactly this branch.
                        RefreshSaveFileInfo();
                    }
                }

                if (check && !HoldsBackgroundWork()) CheckDescription(key);
            }
        }
        else if (UnknownGame && _liveStale)
        {
            // A title qwark has no game module for. There is nothing to describe and nothing that
            // reads the game to read, but the watches, the freezes and the patches are the
            // console's own and answer here as they do anywhere else, so they are read the same
            // way. RefreshLive is what knows which of them to leave out.
            _liveStale = false;
            _heldBack = false;
            RefreshLive();
        }

        if (session.CurrentPlanet != _lastPlanet && session.State == SessionState.Ingame && !UnknownGame)
        {
            // 0xFF is "not known yet", which every session change sets; only a real move counts as
            // a load that may have applied, or refused, the selected manips. Read at once for the
            // games whose run starts as the planet changes; the timer below catches the rest.
            bool moved = _lastPlanet != 0xFF;
            _lastPlanet = session.CurrentPlanet;
            RefreshPositions(quiet: true);

            if (moved && Manips is not null)
            {
                RefreshManips();
                _sinceManipPoll = 0;
            }
        }

        // The timed read (see ManipPollSeconds). Only while the game has groups: a game that
        // answered UNSUPPORTED, and a module too old to know the op, have no list and are not asked.
        if (Manips is not null && Ingame && !UnknownGame)
        {
            _sinceManipPoll += deltaSeconds;
            if (_sinceManipPoll >= ManipPollSeconds)
            {
                _sinceManipPoll = 0;
                RefreshManips();
            }
        }
        else
        {
            _sinceManipPoll = 0;
        }

        // PREVIOUS_LIST is a bulk op, and section 4.1 sets the pending flag on the way through a
        // reboot, which is exactly when section 1.1 refuses one. So the ask waits for the console
        // to be answering again and the flag stays unhandled until it is; the modal is a second or
        // two later than it used to be, and nobody is told the console was busy. A previous session
        // is a game's features and mods, so a title qwark has no game module for never has one and
        // answers the op UNSUPPORTED.
        if (session.PreviousPending && !_lastPreviousPending && !ConsoleBusy && !UnknownGame)
        {
            _lastPreviousPending = true;
            Run(() => Client.PreviousListAsync(), previous =>
            {
                Previous = previous;
                PreviousModalRequested = true;
            });
        }
        else if (!session.PreviousPending && _lastPreviousPending)
        {
            _lastPreviousPending = false;
            Previous = null;
            PreviousModalRequested = false;
        }
    }

    /// <summary>
    /// Everything: the description, the console-owned tables and the panels' own drafts. Only a
    /// different game, a different qwark build, a module that describes the game differently or
    /// the user's own "Re-read everything" reaches here. A quit, a reboot and a dropped connection
    /// do not, which is the whole of "what the console described belongs to the game".
    /// </summary>
    public void ResetPanels()
    {
        PanelResets++;
        ForgetDescribed();
        Watches = Array.Empty<WatchEntry>();
        Freezes = Array.Empty<FreezeEntry>();
        Patches = Array.Empty<PatchEntry>();
        ConsoleMods = Array.Empty<ModEntry>();
        ConsoleFolders = null;
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
        _describedEpoch++;
        DescribedTitle = string.Empty;
        Describe = DescribeResult.Empty;
        AutosplitEvents = Array.Empty<AutosplitEventDesc>();
        SaveFile = SaveFileInfo.None;
        Autosplitter.Descriptors = AutosplitEvents;
        Autosplitter.Game = GameId.None;
        Planets = Array.Empty<string>();
        PlanetOrder = Array.Empty<int>();
        Autosplitter.PlanetNames = Planets;
        EnumOptions.Clear();
        Unlocks = UnlockList.Empty;
        UnlocksUnsupported = false;
        LevelFlagsUnsupported = false;
        Manips = null;
        _sinceManipPoll = 0;
    }

    /// <summary>
    /// Takes the running game as the one the description belongs to. The autosplitter is moved
    /// with it rather than following telemetry, so the two never disagree about which game's
    /// settings and route are in force.
    /// </summary>
    private void AdoptDescribed(DescribedKey key, string title)
    {
        _described = key;
        _describedEpoch++;
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
    /// description is still on screen, the values are read again and DESCRIBE is asked once to
    /// confirm the module still describes it that way.
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
        // Eleven requests would be eleven BUSYs and eleven toasts; the user asked, so they are told
        // once, in the words and the colour a single refused button gets, and nothing is sent. The
        // panels keep what they have, which is still this game's.
        if (ConsoleBusy)
        {
            AddToast(StatusToast.WhileBusy(Session.State), ToastKind.Error);
            return;
        }

        ResetPanels();
        AdoptSession();
        RefreshAll(quiet: false);
        _liveStale = false;
    }

    /// <summary>
    /// Whether a background read has to be held. Nothing automatic goes out while the console is
    /// busy with a launch, and the fact that something was held is remembered here so the tick that
    /// finds the session INGAME again can read it back. A user's own request is never held: it goes
    /// out, and whatever comes back is theirs to see.
    /// </summary>
    private bool HoldsBackgroundWork()
    {
        if (!ConsoleBusy) return false;

        _heldBack = true;
        return true;
    }

    /// <summary>
    /// The description and the values together, and the one place a description is read for a
    /// game that has none on screen; a new connection's check (<see cref="CheckDescription"/>) is
    /// the other DESCRIBE, and it only compares. The game it belongs to is adopted here, because
    /// DESCRIBE only answers for the running game while a game is running: outside INGAME it is
    /// refused or answered with the last one's table, so nothing is asked and the panels keep what
    /// they have. A title qwark has no game module for is the other half of that rule: it is
    /// INGAME and there is still nothing to describe, so none of these goes out either.
    /// </summary>
    public void RefreshAll(bool quiet = true)
    {
        if (!Connected) return;
        if (quiet && HoldsBackgroundWork()) return;

        if (Ingame && !UnknownGame)
        {
            var session = Session;
            var key = DescribedKey.Of(session);

            // Adopted before the answer comes back, so the next tick does not ask a second time.
            // And this is the DESCRIBE the connection owes, so it is not checked again behind it.
            AdoptDescribed(key, session.TitleId);
            _describeOwed = false;

            Run(async () =>
            {
                try
                {
                    var describe = await Client.DescribeAsync().ConfigureAwait(false);
                    Post(() => AdoptDescription(describe, quiet));
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

            RefreshModuleTables(quiet);
        }

        RefreshLive(quiet);
    }

    /// <summary>Takes a DESCRIBE answer as the description on screen, and reads its ENUM rows' options.</summary>
    private void AdoptDescription(DescribeResult describe, bool quiet)
    {
        Describe = describe;
        EnumOptions.Clear();
        RefreshEnumOptions(describe, quiet);
    }

    /// <summary>FEATURE_OPTIONS for each ENUM row of <paramref name="describe"/>, each one taken as it comes back.</summary>
    private void RefreshEnumOptions(DescribeResult describe, bool quiet)
    {
        foreach (var feature in describe.Features.Where(f => f.Kind == FeatureKind.Enum))
        {
            byte id = feature.Id;
            Run(() => Client.FeatureOptionsAsync(id), options => EnumOptions[id] = options, quiet);
        }
    }

    /// <summary>
    /// What the module keeps for the game beside DESCRIBE, and is read with it: the planet list,
    /// the autosplit rows, the savefile helper and whether the game has level flags. One small
    /// request each, and each answer is taken as it comes back.
    /// </summary>
    private void RefreshModuleTables(bool quiet)
    {
        Run(() => Client.PlanetListAsync(), planets =>
        {
            Planets = planets.Names;
            PlanetOrder = planets.Order;
            Autosplitter.PlanetNames = planets.Names;
        }, quiet);

        RefreshAutosplitEvents();
        RefreshSaveFileInfo();
        RefreshLevelFlagsSupport(quiet);
    }

    /// <summary>
    /// The DESCRIBE a new connection owes the description on screen, sent at the first tick that
    /// finds the game INGAME on it. A module replaced by another under the same build number keeps
    /// the key the description is held under, so nothing else ever notices it: the rows, the
    /// readout names and the flags on screen stayed the old module's until the client was
    /// restarted, which is how a new trophy-reset status line and a thirteenth readout name went
    /// unseen. The answer is compared rather than adopted (<see cref="CompareDescription"/>).
    /// <para>
    /// The module's other tables go out beside it, because they are the same blind spot and the
    /// first tick in the game is when they are answered: PLANET_LIST, AUTOSPLIT_DESCRIBE,
    /// SAVEFILE_INFO and the level-flag probe, one small request each. Nothing is reset for them;
    /// they are taken as they come back, as a key change's are, and no draft hangs off any of
    /// them. The ENUM rows' options wait for the comparison, since which rows there are is what it
    /// decides. MANIP_LIST is not here because RefreshLive reads it on every way into a game.
    /// </para>
    /// </summary>
    private void CheckDescription(DescribedKey key)
    {
        _describeOwed = false;
        _describeChecking = true;

        int connection = _connection;
        int epoch = _describedEpoch;

        RunQuiet(async () =>
        {
            try
            {
                var describe = await Client.DescribeAsync().ConfigureAwait(false);
                Post(() => CompareDescription(describe, key, connection, epoch));
            }
            catch (QwarkStatusException)
            {
                // The game ending between the decision and the request, as for RefreshAll: still
                // owed, and asked the next time this connection finds the game. A refusal while
                // the game is still up is left alone rather than asked again every frame.
                Post(() =>
                {
                    if (connection == _connection && !Ingame) _describeOwed = true;
                });

                throw;
            }
            finally
            {
                Post(() => _describeChecking = false);
            }
        });

        RefreshModuleTables(quiet: true);
    }

    /// <summary>
    /// What a check's answer means. The description on screen, field for field and with the
    /// toggles' AUTO bits left out (<see cref="DescribeResult.SameLayoutAs"/>), keeps everything as
    /// it is, the panels' drafts included: the check found what was already known. Anything else
    /// is a module change and is treated as a changed key is, except that the answer is already in
    /// hand and is adopted rather than asked for a second time.
    /// </summary>
    private void CompareDescription(DescribeResult describe, DescribedKey key, int connection, int epoch)
    {
        // The connection it was asked on has gone, or the description has been replaced or dropped
        // since: the answer is nobody's any more.
        if (connection != _connection || epoch != _describedEpoch || key != _described) return;

        DescriptionChecks++;
        if (describe.SameLayoutAs(Describe))
        {
            // The options are the module's too, and are asked for the rows the comparison has just
            // said are still there. Not once the game has gone: they stay as they are until it is
            // back, as everything else described does.
            if (Ingame && !UnknownGame) RefreshEnumOptions(Describe, quiet: true);
            return;
        }

        DescriptionsReplaced++;
        if (Panels.Ui.Debug) AddToast("qwark now describes this game differently: the panels start again");

        ResetPanels();

        // Out of the game already, or into another one: the next tick that finds a game describes
        // it from the start, as it would any game it had not seen.
        if (!Ingame || UnknownGame || DescribedKey.Of(Session) != key) return;

        AdoptDescribed(key, Session.TitleId);
        AdoptDescription(describe, quiet: true);
        RefreshModuleTables(quiet: true);
        RefreshLive();
    }

    /// <summary>
    /// The values, not the layout: what the console holds right now for a game whose description
    /// is already on screen. This is what a reboot, a re-entry to INGAME and a reconnect run, and
    /// nobody pressed anything to ask for it, so it is quiet by default.
    /// </summary>
    public void RefreshLive(bool quiet = true)
    {
        if (!Connected) return;
        if (quiet && HoldsBackgroundWork()) return;

        // The slots come out of the running process and are refused outside it; everything below
        // is the console's own and answers whatever the session is doing. On a title qwark has no
        // game module for the slots, the mods and the combos are all the game's and answer
        // UNSUPPORTED, which each of them knows about; the three in the middle are the memory
        // tools' and answer there like anywhere else.
        if (Ingame) RefreshPositions(quiet);
        RefreshWatches(quiet);
        RefreshFreezes(quiet);
        RefreshPatches(quiet);
        RefreshMods(quiet);
        RefreshCombos(quiet);

        // The selection and the options are the console's, and a boot may have forgotten the
        // selection (remember category off), so every way back into a game reads them again.
        RefreshManips();
    }

    // ---------------------------------------------------------------- manip categories

    /// <summary>The session a MANIP_LIST answer belongs to; see <see cref="ManipEvents.KeyFor"/>.</summary>
    private string ManipKey => ManipEvents.KeyFor(Session);

    /// <summary>
    /// Reads MANIP_LIST, quietly: on every way back into a game, after every manip request, after a
    /// planet change, after a load this client asked for, when the manips area comes on screen, and
    /// every <see cref="ManipPollSeconds"/> while a game with groups is up. Only INGAME, where the
    /// op answers for the game; at the XMB the area keeps what it has, greyed out. One read at a
    /// time: a read asked for while one is out goes out when that one is answered, so the answer
    /// after a change is never one that left before it. UNSUPPORTED and UNKNOWN_OP hide the area,
    /// and with it the timed reads.
    /// </summary>
    public void RefreshManips()
    {
        if (!Ingame || UnknownGame) return;

        if (_manipsInFlight)
        {
            _manipsAgain = true;
            return;
        }

        _manipsInFlight = true;
        string key = ManipKey;
        RunQuiet(async () =>
        {
            try
            {
                var list = await Client.ManipListAsync().ConfigureAwait(false);
                Post(() => AdoptManips(list, key));
            }
            catch (QwarkStatusException ex) when (ex.Status is Status.Unsupported or Status.UnknownOp)
            {
                Post(() =>
                {
                    if (key == ManipKey) Manips = null;
                });
            }
            catch (ProtocolException ex)
            {
                // Hidden rather than retried: the area is what polls, so a reply that cannot be
                // read is reported once and not again every second and a half.
                Post(() =>
                {
                    Manips = null;
                    AddToast($"Manip categories: {ex.Message}", ToastKind.Error);
                });
            }
            finally
            {
                Post(() =>
                {
                    _manipsInFlight = false;
                    if (!_manipsAgain) return;

                    _manipsAgain = false;
                    RefreshManips();
                });
            }
        });
    }

    /// <summary>
    /// Takes a MANIP_LIST answer and toasts its event if it is a new one. An answer from a session
    /// that has since gone is dropped; the next read is the new session's. Returns the event this
    /// answer toasted, or null when it toasted nothing.
    /// </summary>
    private ManipNotice? AdoptManips(ManipList list, string key)
    {
        if (key != ManipKey) return null;

        Manips = list;
        if (_manipEvents.Observe(list) is not { Toast: { } kind } notice) return null;

        AddToast(notice.Text, kind);
        ManipToasts++;
        return notice;
    }

    /// <summary>MANIP_SELECT, then a read so the dropdown shows what the console now holds.</summary>
    public void SelectManipGroup(byte group) => ManipRequest(() => Client.ManipSelectAsync(group));

    /// <summary>MANIP_OPTIONS with both options, then a read so the two checkboxes show the console's.</summary>
    public void SetManipOptions(bool autoReset, bool remember) =>
        ManipRequest(() => Client.ManipOptionsAsync(autoReset, remember));

    /// <summary>One manip request behind a click: its status is the user's to see, and a read follows whatever it was.</summary>
    private void ManipRequest(Func<Task> request) => Run(async () =>
    {
        try
        {
            await request().ConfigureAwait(false);
        }
        finally
        {
            Post(RefreshManips);
        }
    });

    /// <summary>
    /// Apply now. The console refuses with BUSY when the challenge-mode check fails, and its event
    /// says why, so a BUSY in a game is not reported as one: the list is read straight after and
    /// the event's own toast is the answer. A request that went through and brought no toast of its
    /// own (a per-run reapply is quiet on a load) says so, since somebody pressed a button; a
    /// refusal that brought no event says that much. Any other status is reported as any button's.
    /// </summary>
    public void ApplyManips()
    {
        if (Manips is not { } current) return;

        string group = current.GroupName(current.Selected);
        string key = ManipKey;
        Run(async () =>
        {
            bool refused = false;
            try
            {
                await Client.ManipApplyAsync().ConfigureAwait(false);
            }
            catch (QwarkStatusException ex) when (ex.Status == Status.Busy && Session.State == SessionState.Ingame)
            {
                refused = true;
            }
            catch
            {
                Post(RefreshManips);
                throw;
            }

            ManipList? after = null;
            try
            {
                after = await Client.ManipListAsync().ConfigureAwait(false);
            }
            catch (QwarkStatusException)
            {
                // The fallback below says what is known.
            }

            Post(() =>
            {
                if (after is null)
                {
                    AddToast(refused ? ManipEvents.NotApplied(group) : ManipEvents.Applied(group),
                        refused ? ToastKind.Error : ToastKind.Success);
                    return;
                }

                AdoptManips(after, key);

                // Whether the console's own event has said it, here or in a timed read that got to
                // it first: either way it was toasted once, and saying it again would be twice.
                bool spoke = after.EventSeq != current.EventSeq && ManipEvents.NoticeFor(after)?.Toast is not null;
                if (spoke) return;

                if (refused) AddToast(ManipEvents.NotApplied(group), ToastKind.Error);
                else AddToast(ManipEvents.Applied(group), ToastKind.Success);
            });
        });
    }

    // ---------------------------------------------------------------- the VSH's free memory

    /// <summary>
    /// How often VSH_MEMORY (revision 1.22) is read while the session carries flags.VSH_MEMORY.
    /// qwark takes its own reading about every ten seconds, so asking more often would only fetch
    /// the same figure again. The op is a control op, answered in every session state, so the read
    /// is not held through a launch the way the bulk reads are: a launch is when the figure moves.
    /// </summary>
    public const float VshMemoryPollSeconds = 10f;

    /// <summary>
    /// The last VSH_MEMORY answer on this connection: null before the first one and once the
    /// connection has gone. A later read that is refused leaves it here, so the panel can tell a
    /// console that has never answered from one that answered and then refused
    /// (<see cref="VshMemoryStatus"/>).
    /// </summary>
    public VshMemoryInfo? VshMemory { get; private set; }

    /// <summary>
    /// The status the last VSH_MEMORY read was refused with, or null when it went through or none
    /// has been sent on this connection. The read is nobody's click, so a refusal says no more in a
    /// toast than <see cref="RunQuiet"/> lets it; the Connection panel shows it in place of the
    /// figures instead, which is how a request without a button shows its status.
    /// </summary>
    public Status? VshMemoryStatus { get; private set; }

    /// <summary>
    /// Seconds since <see cref="VshMemory"/> arrived, on the frames' own clock, so the ages it carries
    /// can be shown as they are now rather than as they were when the console answered.
    /// </summary>
    public float VshMemoryAge { get; private set; }

    /// <summary>
    /// Set once the connection that is up has been taken in (SessionEstablished), and cleared when
    /// it goes. The transport is up, and HELLO has filled the session in, a moment before that, and
    /// a read sent in between would be answered for a connection this object has not seen yet.
    /// </summary>
    private bool _vshMemoryLive;

    /// <summary>A VSH_MEMORY is out: one at a time, so a slow answer is never asked twice.</summary>
    private bool _vshMemoryInFlight;

    /// <summary>
    /// Set when a reply could not be read. It is reported once and not asked for again on this
    /// connection: the module that sent it will send the same thing ten seconds later.
    /// </summary>
    private bool _vshMemoryUnreadable;

    /// <summary>Seconds since the last VSH_MEMORY went out, held at the interval once one is due.</summary>
    private float _sinceVshMemory;

    /// <summary>Drops the figures, the status and the age with the connection they came from.</summary>
    private void ForgetVshMemory()
    {
        VshMemory = null;
        VshMemoryStatus = null;
        VshMemoryAge = 0;
        _vshMemoryLive = false;
        _vshMemoryUnreadable = false;
    }

    /// <summary>
    /// The timed read. Only while the session carries flags.VSH_MEMORY: the PS3 module sets it from
    /// module start, qwark-rpcs3 never does, and a module from before the op never does either and
    /// would answer UNKNOWN_OP, so without the bit nothing is sent at all. Asked on the first tick
    /// that finds the bit on a connection, and every <see cref="VshMemoryPollSeconds"/> after that.
    /// </summary>
    private void PollVshMemory(SessionInfo session, float deltaSeconds)
    {
        if (VshMemory is not null) VshMemoryAge += deltaSeconds;
        _sinceVshMemory = Math.Min(_sinceVshMemory + deltaSeconds, VshMemoryPollSeconds);

        if (!_vshMemoryLive || !session.VshMemory || _vshMemoryInFlight || _vshMemoryUnreadable) return;
        if (_sinceVshMemory < VshMemoryPollSeconds) return;

        _sinceVshMemory = 0;
        RefreshVshMemory();
    }

    /// <summary>
    /// One VSH_MEMORY read, quietly, taken only by the connection it was asked on. A refusal is
    /// kept for the panel. It toasts only when it is news — a status other than the one the last
    /// read got — and only when <see cref="StatusToast"/> lets a background read say it at all: the
    /// same refusal every ten seconds is the panel's line, not a toast every ten seconds.
    /// </summary>
    private void RefreshVshMemory()
    {
        _vshMemoryInFlight = true;
        int connection = _connection;
        var previous = VshMemoryStatus;

        RunQuiet(async () =>
        {
            try
            {
                var info = await Client.VshMemoryAsync().ConfigureAwait(false);
                Post(() =>
                {
                    if (connection != _connection || !_vshMemoryLive) return;

                    VshMemory = info;
                    VshMemoryStatus = null;
                    VshMemoryAge = 0;
                });
            }
            catch (QwarkStatusException ex)
            {
                var status = ex.Status;
                Post(() =>
                {
                    if (connection == _connection && _vshMemoryLive) VshMemoryStatus = status;
                });

                if (status != previous) throw;
            }
            catch (ProtocolException ex)
            {
                // Hidden and reported once, as the manips area does with a reply it cannot read.
                Post(() =>
                {
                    if (connection != _connection || !_vshMemoryLive) return;

                    VshMemory = null;
                    _vshMemoryUnreadable = true;
                    AddToast($"VSH memory: {ex.Message}", ToastKind.Error);
                });
            }
            finally
            {
                Post(() => _vshMemoryInFlight = false);
            }
        });
    }

    /// <summary>
    /// Probes LEVELFLAGS_GET once so the nav can hide the Level flags panel for a game that has no
    /// flag table (Deadlocked). UNSUPPORTED means hide; anything else (bytes, or BAD_ARG for a
    /// placeholder planet) means the game has them.
    /// </summary>
    public void RefreshLevelFlagsSupport(bool quiet = true)
    {
        // Only INGAME: anywhere else the op answers NOT_INGAME, which says nothing about whether
        // the game has flags and would flip the answer back for a game that has none. A title with
        // no game module behind it has no flag table to probe for.
        if (!Ingame || UnknownGame) return;

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
        if (!Connected || UnknownGame) return;
        if (HoldsBackgroundWork()) return;

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
    /// Re-reads SAVEFILE_INFO, revision 1.9. UNSUPPORTED is what a module before revision 1.15
    /// answers where code cannot be patched, and for a module older than 1.9 the op is unknown;
    /// both mean the Save files panel has nothing to drive, and neither is worth a toast.
    /// </summary>
    public void RefreshSaveFileInfo()
    {
        if (!Connected || UnknownGame) return;
        if (HoldsBackgroundWork()) return;

        RunQuiet(async () =>
        {
            try
            {
                var info = await Client.SaveFileInfoAsync().ConfigureAwait(false);
                Post(() => SaveFile = info);
            }
            catch (QwarkStatusException ex) when (ex.Status is Status.Unsupported or Status.UnknownOp
                                                             or Status.NotIngame)
            {
                Post(() => SaveFile = SaveFileInfo.None);
            }
        });
    }

    /// <summary>
    /// True while a POS_LIST this client sent has not been answered. The Positions panel re-reads
    /// the slot table on a timer, and an interval shorter than the round trip would otherwise
    /// queue reads for ever; this is what it skips a tick on.
    /// </summary>
    public bool PositionsPending => Volatile.Read(ref _positionsInFlight) > 0;

    private int _positionsInFlight;

    public void RefreshPositions(bool quiet = false)
    {
        if (UnknownGame) return;
        if (quiet && HoldsBackgroundWork()) return;
        if (!Connected) return;

        Interlocked.Increment(ref _positionsInFlight);
        Run(async () =>
        {
            try
            {
                return await Client.PosListAsync().ConfigureAwait(false);
            }
            finally
            {
                // In a finally, so a refused read does not stop the next one from ever starting.
                Interlocked.Decrement(ref _positionsInFlight);
            }
        }, positions => Positions = positions, quiet);
    }

    /// <summary>
    /// Re-reads UNLOCK_LIST. UNSUPPORTED is normal for a game with no unlock table, so it is
    /// reported once and then the panel just says so; every other status still toasts.
    /// </summary>
    public void RefreshUnlocks(bool quiet = false)
    {
        if (!Connected || UnknownGame) return;
        if (quiet && HoldsBackgroundWork()) return;

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
        if (quiet && HoldsBackgroundWork()) return;
        if (Connected) Run(() => Client.WatchListAsync(), watches => Watches = watches, quiet);
    }

    public void RefreshFreezes(bool quiet = false)
    {
        if (quiet && HoldsBackgroundWork()) return;
        if (Connected) Run(() => Client.FreezeListAsync(), freezes => Freezes = freezes, quiet);
    }

    public void RefreshPatches(bool quiet = false)
    {
        if (quiet && HoldsBackgroundWork()) return;
        if (Connected) Run(() => Client.PatchListAsync(), patches => Patches = patches, quiet);
    }

    public void RefreshCombos(bool quiet = false)
    {
        if (UnknownGame) return;
        if (quiet && HoldsBackgroundWork()) return;
        if (Connected) Run(() => Client.ComboListAsync(), combos => Combos = combos, quiet);
    }

    public void RefreshMods(bool quiet = false)
    {
        if (!Connected || UnknownGame) return;
        if (quiet && HoldsBackgroundWork()) return;

        Run(() => Client.ModListAsync(), mods => ConsoleMods = mods, quiet);
        RefreshConsoleFolders(quiet);
        RescanLocalMods();
    }

    /// <summary>
    /// MOD_LIST again without reading the library: what the Mods panel under RPCS3 asks for while
    /// qwark-rpcs3 is still looking for mods in the game, and after it has written one.
    /// </summary>
    public void RefreshConsoleMods(bool quiet = false)
    {
        if (!Connected || UnknownGame) return;
        if (quiet && HoldsBackgroundWork()) return;

        Run(() => Client.ModListAsync(), mods => ConsoleMods = mods, quiet);
    }

    /// <summary>
    /// DIR_LIST of the console's mod folder for the title on screen: the folders' whole names,
    /// which MOD_LIST cuts to 31 bytes. Read with every <see cref="RefreshMods"/> (a rescan, a
    /// load, an upload, a reconnect), when the Mods panel opens and after an uninstall, never once
    /// a frame. A title with no mod folder on the console answers NOT_FOUND, which is no folders.
    /// </summary>
    public void RefreshConsoleFolders(bool quiet = false)
    {
        if (!Connected || UnknownGame) return;
        if (quiet && HoldsBackgroundWork()) return;

        string title = CurrentTitle;
        if (string.IsNullOrEmpty(title)) return;

        Run(async () =>
        {
            IReadOnlyList<string> folders;
            try
            {
                folders = ConsoleModMatch.FoldersIn(
                    await Client.DirListAsync(ModLibrary.ConsoleTitleFolder(title)).ConfigureAwait(false));
            }
            catch (QwarkStatusException ex) when (ex.Status == Status.NotFound)
            {
                folders = Array.Empty<string>();
            }

            Post(() => ConsoleFolders = new ConsoleModFolders(title, folders));
        }, null, quiet);
    }

    /// <summary>
    /// The Mods panel's rows for the title on screen: the library's mods, each with what the
    /// console has of it, and then every mod the console has that no library does.
    /// </summary>
    public IReadOnlyList<ModRow> ModRows()
    {
        var folders = ConsoleFolders is { } listed && listed.Title == CurrentTitle ? listed.Folders : null;
        return ModTable.Build(LocalMods, ConsoleMods, folders);
    }

    /// <summary>
    /// The local library for the game whose description is on screen, which at the XMB is the game
    /// that just quit rather than no game at all.
    /// </summary>
    public void RescanLocalMods()
    {
        string title = CurrentTitle;
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
        string line = $"{session.State.DisplayName()} | {title} | {session.GameName}";

        // The console's combo switch is on the header rather than only on the Combos panel: a pad
        // that has stopped doing anything is noticed from whichever panel the user is looking at.
        if (session.CombosOff) line += " | combos disabled";
        else line += " | combos enabled";

        if (!Panels.Ui.Debug) return line;

        string tick = session.Tick > 0 ? $"tick {session.Tick}" : "tick -";
        return line + $" | generation {session.Generation} | {tick}"
                    + $" | qwark v{session.QwarkVersion} protocol {session.ProtocolVersion}";
    }

    public void Dispose()
    {
        // Before the client, so no telemetry arrives at a stream that is being torn down.
        ObsPad.Dispose();

        LiveSplit.Dispose();
        Client.Dispose();

        // Last, so the client has already dropped its connection when the helper is asked to go.
        Rpcs3.Dispose();
    }
}
