using System.Globalization;
using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>What LiveSplit last told us about itself. Replaced whole, so it is always self-consistent.</summary>
/// <param name="UpcomingSplit">
/// The name of the split after the current one, whether LiveSplit answered it or it was counted out
/// of the run file. Null means there is none, which the route reads as "do not split".
/// </param>
/// <param name="UpcomingSupported">
/// False once this LiveSplit build has shown it does not answer <c>getupcomingsplitname</c>. Most
/// installed builds do not, which is why the run file exists as the second way to the same name.
/// </param>
/// <param name="SplitIndex">
/// <c>getsplitindex</c>, which is -1 while the timer is not running. It is what lines the run file
/// up with LiveSplit.
/// </param>
/// <param name="UpcomingSource">Which of the two ways produced <paramref name="UpcomingSplit"/>.</param>
/// <param name="NamesKnown">
/// True when a splits file is in hand, so an empty upcoming name really is the end of the run
/// rather than a name nobody could look up.
/// </param>
public sealed record LiveSplitView(
    LiveSplitPhase Phase,
    string? CurrentSplit,
    string? UpcomingSplit,
    bool UpcomingSupported = true,
    int SplitIndex = -1,
    UpcomingNameSource UpcomingSource = UpcomingNameSource.None,
    bool NamesKnown = false)
{
    public static LiveSplitView Empty { get; } = new(LiveSplitPhase.Unknown, null, null);

    /// <summary>How the panel names where the route's comparison name came from.</summary>
    public string UpcomingSourceLabel => UpcomingSource switch
    {
        UpcomingNameSource.LiveSplit => "LiveSplit",
        UpcomingNameSource.SplitsFile => "splits file",
        _ => "nowhere",
    };
}

/// <summary>One line of the panel's rolling log: what arrived, and what was done about it.</summary>
public sealed record AutosplitLogEntry(uint Seq, uint TimeMs, AutosplitKind Kind, string Event, string Action, bool Acted);

/// <summary>
/// The decision half of "qwark detects, the client decides": it takes the run events the console
/// emits unconditionally, applies the user's settings and turns what survives into LiveSplit
/// commands. It keeps no timer of its own — LiveSplit owns the clock, and every correction below
/// is one of the old scripts' own moves, sent as a command rather than counted here.
/// <para>
/// Two things are not the user's decision. The game-time corrections the old LiveSplit scripts
/// applied in their <c>update</c> and <c>isLoading</c> blocks are what make a run's final time the
/// run's final time, so they are applied whenever the autosplitter is on and LiveSplit is
/// connected, whatever any checkbox says. A load pair and a flat row go out as
/// <c>addloadingtimes</c>. A normalised <em>pause</em> pair, which today is only Deadlocked's quit
/// to the XMB, is the one place game time is stopped and set outright: <c>pausegametime</c> at the
/// quit, then <c>setgametime</c> of the frozen clock plus the row's parameter and
/// <c>unpausegametime</c> when the game is back. That is exactly what
/// <c>rac4-LC-autosplitter.asl</c> did, and it is what makes a quit cost the run 14.8 s however
/// long the player was really in the XMB.
/// </para>
/// <para>
/// The timer phase, the split index and the split names are read back from LiveSplit on connect,
/// after every command sent and once a second, so a manual reset or undo in LiveSplit is followed
/// rather than fought, and the name the planet route needs is already in hand when a planet event
/// lands.
/// </para>
/// <para>
/// LiveSplit's server has no command that returns a split by index, and most installed builds do
/// not answer <c>getupcomingsplitname</c> at all, so the name the route compares comes from the
/// run's own <c>.lss</c> when LiveSplit will not say: see <see cref="LiveSplitRunLibrary"/>.
/// </para>
/// </summary>
public sealed class Autosplitter
{
    /// <summary>How many events the panel's log keeps.</summary>
    public const int LogLength = 100;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly Settings _settings;
    private readonly LiveSplitClient _liveSplit;
    private readonly Action<Action> _post;
    private readonly List<AutosplitLogEntry> _log = new();
    private readonly LiveSplitRunLibrary _runs = new();

    /// <summary>The open half of every normalised pair: the reason code against its start time.</summary>
    private readonly Dictionary<byte, uint> _openPairs = new();

    private readonly object _gate = new();

    private volatile LiveSplitView _view = LiveSplitView.Empty;
    private GameId _game = GameId.None;
    private int _refreshing;
    private int _received;
    private int _acted;
    private int _adjustments;
    private double _sincePoll;

    /// <param name="post">
    /// Hands a result back to the render thread; the panel reads what it sets. In the app this is
    /// <see cref="AppState.Post"/>, so nothing the network threads produce is read half-written.
    /// </param>
    public Autosplitter(Settings settings, LiveSplitClient liveSplit, Action<Action> post)
    {
        _settings = settings;
        _liveSplit = liveSplit;
        _post = post;

        // A fresh connection knows nothing about the run: find the splits files and ask LiveSplit
        // which of them it has open, before the first event arrives. Off the worker thread, which
        // has a socket to pump and must not wait on fifty files being read.
        _liveSplit.Established += () => _ = Task.Run(async () =>
        {
            await ReloadRunsAsync().ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        });
    }

    /// <summary>The running game, from telemetry. Chooses the settings entry and the planet route.</summary>
    public GameId Game
    {
        get => _game;
        set
        {
            if (_game == value) return;
            _game = value;

            // A half-finished load belongs to the game that started it.
            lock (_gate) _openPairs.Clear();
        }
    }

    /// <summary>The AUTOSPLIT_DESCRIBE rows for the running game; empty when it has no watcher.</summary>
    public IReadOnlyList<AutosplitEventDesc> Descriptors { get; set; } = Array.Empty<AutosplitEventDesc>();

    /// <summary>PLANET_LIST, only so the log can name the planet an event carried.</summary>
    public IReadOnlyList<string> PlanetNames { get; set; } = Array.Empty<string>();

    public LiveSplitView View => _view;

    /// <summary>
    /// The splits files this client can see and the one LiveSplit is believed to have open. Read
    /// by the panel on the render thread; only ever written off it.
    /// </summary>
    public LiveSplitRunLibrary Runs => _runs;

    /// <summary>Events received from the console, whatever was done with them.</summary>
    public int Received => Volatile.Read(ref _received);

    /// <summary>Events that became at least one LiveSplit command.</summary>
    public int Acted => Volatile.Read(ref _acted);

    /// <summary>How many game-time corrections have gone out, of either kind.</summary>
    public int Adjustments => Volatile.Read(ref _adjustments);

    public AutosplitSettings Options => _settings.Autosplit;

    public AutosplitGameSettings GameOptions => _settings.Autosplit.For(Game);

    /// <summary>The log, newest last. A copy, because the render thread reads it while events land.</summary>
    public AutosplitLogEntry[] Log()
    {
        lock (_gate) return _log.ToArray();
    }

    public void ClearLog()
    {
        lock (_gate) _log.Clear();
    }

    /// <summary>Whether this event's checkbox is ticked, or its console-side default when it has none.</summary>
    public bool IsEnabled(AutosplitEventDesc desc) => GameOptions.EventEnabled(desc.Label, desc.EnabledByDefault);

    /// <summary>The descriptor for a code-1 event, when the running game has one; the planet route needs it.</summary>
    public AutosplitEventDesc? PlanetDescriptor
    {
        get
        {
            foreach (var desc in Descriptors)
            {
                if (desc.PlanetRoute || desc.Code == AutosplitEvent.PlanetEnteredCode) return desc;
            }

            return null;
        }
    }

    // ---------------------------------------------------------------- decisions

    /// <summary>
    /// One event from the console. Called on a network thread: it decides, sends what it decided
    /// and logs, and never touches the UI.
    /// </summary>
    public void Handle(AutosplitEvent ev)
    {
        Interlocked.Increment(ref _received);

        var desc = DescriptorFor(ev);
        string what = Describe(ev, desc);

        if (!_settings.Autosplit.Enabled)
        {
            Record(ev, what, "ignored: the autosplitter is off", false);
            return;
        }

        if (!_liveSplit.IsConnected)
        {
            Record(ev, what, "ignored: LiveSplit is not connected", false);
            return;
        }

        var game = GameOptions;

        // The game-time correction goes first: for a FLAT split row the old script took its
        // frames off before it split, and LiveSplit has to see the two in that order.
        bool acted = ApplyTiming(ev, desc, what, out bool logged);

        switch (ev.Kind)
        {
            case AutosplitKind.Start:
                if (!game.Start) Record(ev, what, "ignored: starting the timer is switched off", false);
                else acted |= HandleStart(ev, what);
                break;

            case AutosplitKind.Split:
                acted |= HandleSplit(ev, desc, what, game);
                break;

            case AutosplitKind.Reset:
                if (!game.Reset) Record(ev, what, "ignored: resetting the timer is switched off", false);
                else acted |= Act(ev, what, LiveSplitClient.Reset);
                break;

            case AutosplitKind.Pause:
            case AutosplitKind.Resume:
                acted |= HandlePauseResume(ev, desc, what, logged);
                break;

            case AutosplitKind.LoadStart:
            case AutosplitKind.LoadEnd:
                if (!logged) Record(ev, what, NoTimingReason(desc), false);
                break;

            default:
                Record(ev, what, $"ignored: unknown event kind {(byte)ev.Kind}", false);
                break;
        }

        if (acted) Interlocked.Increment(ref _acted);
    }

    // ---------------------------------------------------------------- game time

    /// <summary>
    /// The two corrections of revision 1.5, which no setting gates:
    /// <list type="bullet">
    /// <item>FLAT: the row's parameter comes off game time every time the event happens.</item>
    /// <item>
    /// NORMALISE on a load pair: the start of the pair is remembered, and at its end everything the
    /// pair lasted beyond the parameter comes off. A pair shorter than its parameter is left alone
    /// — the old scripts never gave a run time back, and neither does this.
    /// </item>
    /// </list>
    /// A NORMALISE <em>pause</em> pair is not measured at all: game time stops for it and the
    /// parameter is put on at the resume, so it is handled with the timer commands in
    /// <see cref="HandlePauseResume"/> rather than here.
    /// </summary>
    /// <param name="logged">True when this wrote a line of its own, so the caller does not repeat it.</param>
    /// <returns>True when a command went out.</returns>
    private bool ApplyTiming(AutosplitEvent ev, AutosplitEventDesc? desc, string what, out bool logged)
    {
        logged = false;
        if (desc is null || !desc.IsTiming) return false;

        switch (ev.Kind)
        {
            case AutosplitKind.Split:
                if (!desc.Flat) return false;
                logged = true;
                return SendAdjustment(ev, what, desc.ParamUs, $"a fixed {Seconds(desc.ParamUs)} off {desc.Label}");

            case AutosplitKind.LoadStart:
            case AutosplitKind.Pause:
                if (desc.Flat)
                {
                    logged = true;
                    return SendAdjustment(ev, what, desc.ParamUs, $"a fixed {Seconds(desc.ParamUs)} off {desc.Label}");
                }

                // A normalised pause stops the clock instead of being measured, and that is the
                // timer's business, not this method's.
                if (ev.Kind == AutosplitKind.Pause) return false;

                // The other half of the pair carries the same code, and the gap between the two
                // stamps is the load. Only the newest start is kept: a start with no end is a load
                // the console never finished reporting, and replacing it is what an ASL would do.
                lock (_gate) _openPairs[ev.Code] = ev.TimeMs;
                logged = true;
                Record(ev, what, $"timing: started, {Seconds(desc.ParamUs)} of it is free", false);
                return false;

            case AutosplitKind.Resume:
                return false;

            case AutosplitKind.LoadEnd:
            {
                uint start;
                bool open;
                lock (_gate) open = _openPairs.Remove(ev.Code, out start);
                if (!open || !desc.Normalise) return false;

                logged = true;
                long durationUs = (long)AutosplitEvent.Elapsed(start, ev.TimeMs) * 1000;
                long excess = durationUs - desc.ParamUs;
                if (excess > 0)
                {
                    return SendAdjustment(ev, what, excess,
                        $"{desc.Label} took {Seconds(durationUs)}, {Seconds(excess)} over its {Seconds(desc.ParamUs)}");
                }

                Record(ev, what, $"no adjustment: {Seconds(durationUs)} is within its {Seconds(desc.ParamUs)}", false);
                return false;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Takes time off the run's game time for a flat row or a normalised load. The amount is never
    /// negative here, so these corrections can only ever shorten a run, which is the direction
    /// every one of the old scripts moved it in their <c>update</c> and <c>isLoading</c> blocks.
    /// </summary>
    private bool SendAdjustment(AutosplitEvent ev, string what, long microseconds, string because)
    {
        if (microseconds <= 0) return false;

        string command = LiveSplitClient.AddLoadingTimesCommand(microseconds);
        _liveSplit.Send(command);
        Interlocked.Increment(ref _adjustments);
        Record(ev, what, $"{command} ({because})", true);
        return true;
    }

    /// <summary>Why a load or pause event did nothing, which is only ever one of two things.</summary>
    private static string NoTimingReason(AutosplitEventDesc? desc) => desc is null
        ? "ignored: this game describes no timing for that code"
        : "ignored: nothing of that code had started";

    private static string Seconds(long microseconds) =>
        $"{microseconds / 1_000_000.0:0.###} s";

    // ---------------------------------------------------------------- splits

    /// <summary>
    /// A run started. The timer only starts when LiveSplit says it is not running, so a start
    /// event mid-run (a level reload, a second console) leaves the timer alone. An unknown phase
    /// is treated as "not running": the phase query failed, and refusing to start a run because of
    /// that would be worse than a start LiveSplit ignores.
    /// </summary>
    private bool HandleStart(AutosplitEvent ev, string what)
    {
        var phase = _view.Phase;
        if (phase is LiveSplitPhase.NotRunning or LiveSplitPhase.Unknown)
        {
            return Act(ev, what, LiveSplitClient.StartTimer,
                phase == LiveSplitPhase.Unknown ? "the timer phase is not known yet" : null);
        }

        Record(ev, what, $"ignored: the timer is {phase}", false);
        return false;
    }

    /// <summary>
    /// A split candidate. The master switch, then the row's own checkbox, then — for the planet
    /// code alone — the route: the split names are the planets of the run, so the planet just
    /// entered has to be the one the <em>upcoming</em> split names, exactly as every old script
    /// compared <c>timer.Run[timer.CurrentSplitIndex + 1].Name</c>. Where that name came from —
    /// LiveSplit or the run file — makes no difference here.
    /// </summary>
    private bool HandleSplit(AutosplitEvent ev, AutosplitEventDesc? desc, string what, AutosplitGameSettings game)
    {
        if (!game.Split)
        {
            Record(ev, what, "ignored: splitting is switched off", false);
            return false;
        }

        if (desc is not null && !game.EventEnabled(desc.Label, desc.EnabledByDefault))
        {
            Record(ev, what, $"ignored: \"{desc.Label}\" is switched off", false);
            return false;
        }

        bool routed = game.PlanetRoute
                      && ev.Code == AutosplitEvent.PlanetEnteredCode
                      && (desc?.PlanetRoute ?? true);

        if (!routed) return Act(ev, what, LiveSplitClient.Split);

        var view = _view;
        string? name = view.UpcomingSplit;
        if (string.IsNullOrWhiteSpace(name))
        {
            // Two different silences: the run really has nothing after this split, or nobody could
            // say what comes next because neither LiveSplit nor a splits file would answer.
            Record(ev, what, view.UpcomingSupported || view.NamesKnown
                ? "no split: there is no upcoming split, so this is the last segment"
                : "no split: the run's split names are not known; load your splits in LiveSplit "
                  + "or pick the .lss file", false);
            return false;
        }

        if (AutosplitRoutes.Matches(Game, (int)ev.Arg, name))
        {
            return Act(ev, what, LiveSplitClient.Split, $"the next split \"{name}\" is this planet");
        }

        Record(ev, what, $"no split: the next split \"{name}\" is not on this planet's route", false);
        return false;
    }

    /// <summary>
    /// A pause that is not a normalised pair is still worth passing on, so a game that reports one
    /// without a parameter stops the timer the way it always did. A normalised pair is the old
    /// Deadlocked script's <c>isLoading</c> instead: game time stops for the quit and the row's
    /// parameter is put back on at the resume, so the run pays exactly that whatever the quit took.
    /// </summary>
    private bool HandlePauseResume(AutosplitEvent ev, AutosplitEventDesc? desc, string what, bool logged)
    {
        if (logged) return false;

        if (desc is { Normalise: true })
        {
            return ev.Kind == AutosplitKind.Pause
                ? StopGameTime(ev, desc, what)
                : StartGameTime(ev, desc, what);
        }

        return Act(ev, what, ev.Kind == AutosplitKind.Pause ? LiveSplitClient.Pause : LiveSplitClient.Resume);
    }

    /// <summary>
    /// The quit: game time stops while real time carries on, which is what an ASL's
    /// <c>isLoading</c> returning true does. The timer phase does not move — LiveSplit is still
    /// Running — so the optimistic phase this engine keeps is left exactly as it is.
    /// </summary>
    private bool StopGameTime(AutosplitEvent ev, AutosplitEventDesc desc, string what)
    {
        // The pair is remembered by code so the resume knows a quit of its own is open: a RESUME
        // that arrives with no PAUSE, because the client connected in the middle of one, must not
        // start a clock nobody here stopped.
        lock (_gate) _openPairs[ev.Code] = ev.TimeMs;

        _liveSplit.Send(LiveSplitClient.PauseGameTime);
        Record(ev, what, $"{LiveSplitClient.PauseGameTime} (game time stops; the resume puts "
                         + $"{Seconds(desc.ParamUs)} back on)", true);
        return true;
    }

    /// <summary>
    /// The game is back: the frozen clock is read, the row's parameter goes on it and game time
    /// starts again from there. Reading it is a query and a query is answered on the connection's
    /// own worker, so the rest of the pair runs off the thread that delivers console events — a
    /// build that does not answer would otherwise hold that thread for the query timeout.
    /// </summary>
    private bool StartGameTime(AutosplitEvent ev, AutosplitEventDesc desc, string what)
    {
        bool open;
        lock (_gate) open = _openPairs.Remove(ev.Code);
        if (!open)
        {
            Record(ev, what, NoTimingReason(desc), false);
            return false;
        }

        _ = Task.Run(() => StartGameTimeAsync(ev, desc, what));
        return true;
    }

    private async Task StartGameTimeAsync(AutosplitEvent ev, AutosplitEventDesc desc, string what)
    {
        long paramUs = desc.ParamUs;
        var owed = TimeSpan.FromTicks(paramUs * TimeSpan.TicksPerMicrosecond);

        string? reply = await _liveSplit.QueryAsync(LiveSplitClient.GetCurrentGameTime).ConfigureAwait(false);
        if (LiveSplitClient.TryParseGameTime(reply, out var frozen))
        {
            // The old script's two lines, in its order: set the clock while it is still stopped, so
            // the value cannot move under us, then let it run on from there.
            string set = LiveSplitClient.SetGameTimeCommand(frozen + owed);
            _liveSplit.Send(set);
            _liveSplit.Send(LiveSplitClient.UnpauseGameTime);
            Interlocked.Increment(ref _adjustments);
            Record(ev, what, $"{set} (+{Seconds(paramUs)}) then {LiveSplitClient.UnpauseGameTime}", true);
            return;
        }

        // This build will not say what game time is. The same amount goes on the other way round:
        // game time is real time less the loading times, so a negative loading time is time added.
        // It has to follow the unpause, which recomputes the loading times from the clock it froze
        // and would throw away anything added before it.
        _liveSplit.Send(LiveSplitClient.UnpauseGameTime);
        string add = LiveSplitClient.AddLoadingTimesCommand(-paramUs);
        _liveSplit.Send(add);
        Interlocked.Increment(ref _adjustments);
        Record(ev, what, $"{LiveSplitClient.UnpauseGameTime} then {add} (+{Seconds(paramUs)}; this "
                         + $"LiveSplit does not answer {LiveSplitClient.GetCurrentGameTime})", true);
    }

    private bool Act(AutosplitEvent ev, string what, string command, string? because = null)
    {
        _liveSplit.Send(command);
        Record(ev, what, because is null ? command : $"{command} ({because})", true);

        // What we just sent moved the timer. The read-back below is asynchronous, and the console
        // sends RESET and START back to back on a new game, so the phase is moved here first:
        // otherwise the START would still see the old run as Running and leave the timer stopped.
        var assumed = command switch
        {
            LiveSplitClient.Reset => LiveSplitPhase.NotRunning,
            LiveSplitClient.StartTimer => LiveSplitPhase.Running,
            LiveSplitClient.Pause => LiveSplitPhase.Paused,
            LiveSplitClient.Resume => LiveSplitPhase.Running,
            _ => _view.Phase,
        };
        _view = _view with { Phase = assumed };

        _ = RefreshAsync();
        return true;
    }

    private void Record(AutosplitEvent ev, string what, string action, bool acted)
    {
        var entry = new AutosplitLogEntry(ev.Seq, ev.TimeMs, ev.Kind, what, action, acted);
        lock (_gate)
        {
            _log.Add(entry);
            if (_log.Count > LogLength) _log.RemoveRange(0, _log.Count - LogLength);
        }
    }

    /// <summary>
    /// The row that names this event's reason code, or null when the game described none. The end
    /// of a pair is described by its start: a LOAD_END carries its LOAD_START row's code, and a
    /// RESUME its PAUSE row's, so both find the row that holds the parameter.
    /// </summary>
    public AutosplitEventDesc? DescriptorFor(AutosplitEvent ev)
    {
        var kind = ev.Kind switch
        {
            AutosplitKind.LoadEnd => AutosplitKind.LoadStart,
            AutosplitKind.Resume => AutosplitKind.Pause,
            _ => ev.Kind,
        };

        foreach (var desc in Descriptors)
        {
            if (desc.Kind == kind && desc.Code == ev.Code) return desc;
        }

        // A code named without its kind still names the split; code 0 is not a code at all.
        if (ev.Kind == AutosplitKind.Split && ev.Code != 0)
        {
            foreach (var desc in Descriptors)
            {
                if (desc.Code == ev.Code) return desc;
            }
        }

        return null;
    }

    /// <summary>How the log names an event: its label, and the planet when it carries one.</summary>
    public string Describe(AutosplitEvent ev, AutosplitEventDesc? desc)
    {
        string label = desc?.Label ?? (ev.Kind == AutosplitKind.Split ? $"code {ev.Code}" : ev.Kind.ToString());

        if (ev.Kind is AutosplitKind.LoadEnd or AutosplitKind.Resume) label = $"{label} (end)";
        if (ev.Kind != AutosplitKind.Split) return label;
        if (ev.Code != AutosplitEvent.PlanetEnteredCode) return ev.Arg == 0 ? label : $"{label} ({ev.Arg})";

        string planet = ev.Arg < (uint)PlanetNames.Count ? PlanetNames[(int)ev.Arg] : string.Empty;
        return string.IsNullOrEmpty(planet) ? $"{label} (planet {ev.Arg})" : $"{label}: {planet}";
    }

    // ---------------------------------------------------------------- LiveSplit state

    /// <summary>Drives the 1 Hz phase poll off the render loop, so LiveSplit's own changes are followed.</summary>
    public void Tick(double deltaSeconds)
    {
        if (!_settings.Autosplit.Enabled || !_liveSplit.IsConnected)
        {
            // Nothing known about a timer we are not talking to; the panel says so rather than
            // showing the phase and split names the last connection left behind.
            if (!_liveSplit.IsConnected) _view = LiveSplitView.Empty;
            _sincePoll = 0;
            return;
        }

        _sincePoll += deltaSeconds;
        if (_sincePoll < PollInterval.TotalSeconds) return;

        _sincePoll = 0;
        _ = RefreshAsync();
    }

    /// <summary>
    /// Re-reads the splits files: the one the user named, or LiveSplit's own recent list. Runs off
    /// the render thread, and answers what it found so the caller can say why nothing was.
    /// </summary>
    public Task<LiveSplitRunState> ReloadRunsAsync() => Task.Run(() =>
        _runs.Load(_settings.Autosplit.SplitsFile, _settings.Autosplit.LiveSplitFolder));

    /// <summary>
    /// Reads the timer phase, the split index and the split names back. One refresh runs at a time:
    /// a burst of events would otherwise queue six queries each behind the same connection. A query
    /// this LiveSplit does not answer costs one timeout on the first refresh and nothing afterwards.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;

        try
        {
            if (!_liveSplit.IsConnected) return;

            var phase = LiveSplitClient.ParsePhase(await Ask(LiveSplitClient.GetCurrentTimerPhase));
            string? current = Clean(await Ask(LiveSplitClient.GetCurrentSplitName));
            int index = ParseIndex(await Ask(LiveSplitClient.GetSplitIndex));
            string? previous = Clean(await Ask(LiveSplitClient.GetPreviousSplitName));
            string? last = Clean(await Ask(LiveSplitClient.GetLastSplitName));

            // Asked last, and its answer is only trusted when the build answers it at all: the
            // first ask is also how that is found out, and it costs one timeout to find out.
            string? live = Clean(await Ask(LiveSplitClient.GetUpcomingSplitName));
            bool upcomingSupported = _liveSplit.Answers(LiveSplitClient.GetUpcomingSplitName);

            _runs.Validate(index, current, previous, last);
            var runs = _runs.State;

            var (upcoming, source) = ResolveUpcoming(index, live, upcomingSupported);

            var view = new LiveSplitView(
                phase, current, upcoming, upcomingSupported, index, source, runs.HasNames);
            _post(() => _view = view);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }

        Task<string?> Ask(string command) => _liveSplit.QueryAsync(command);
    }

    /// <summary>
    /// The name the planet route compares, in the only order that can be right: LiveSplit's own
    /// answer when this build gives one, else the run file counted forward from the split index,
    /// else nothing at all — and nothing means the route cannot gate, so it does not split.
    /// </summary>
    private (string? Name, UpcomingNameSource Source) ResolveUpcoming(
        int index, string? live, bool upcomingSupported)
    {
        if (upcomingSupported)
        {
            // An empty answer from a build that answers is the last segment, not a missing name,
            // so the run file is not consulted behind its back.
            return live is null
                ? (null, UpcomingNameSource.None)
                : (live, UpcomingNameSource.LiveSplit);
        }

        string? fromFile = _runs.Upcoming(index);
        return fromFile is null ? (null, UpcomingNameSource.None) : (fromFile, UpcomingNameSource.SplitsFile);
    }

    /// <summary>LiveSplit answers a query it cannot serve with an empty line; that is "no split".</summary>
    private static string? Clean(string? reply)
    {
        var trimmed = reply?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed == "-" ? null : trimmed;
    }

    /// <summary>
    /// <c>getsplitindex</c>, which is -1 while the timer is not running. An answer that is not a
    /// number, or none at all, is treated the same way: nothing to line the run file up with.
    /// </summary>
    private static int ParseIndex(string? reply) =>
        int.TryParse((reply ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int index)
            ? index
            : -1;

    /// <summary>The panel's test buttons: one command, no decisions, logged as a manual action.</summary>
    public void SendManual(string command)
    {
        if (!_liveSplit.IsConnected) return;

        _liveSplit.Send(command);
        Record(default, "(manual)", command, false);
        _ = RefreshAsync();
    }
}
