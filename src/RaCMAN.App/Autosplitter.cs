using System.Globalization;
using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>What LiveSplit last told us about itself. Replaced whole, so it is always self-consistent.</summary>
/// <param name="UpcomingSplit">
/// <c>getupcomingsplitname</c>: the name of the split after the current one, which is the only
/// thing the planet route compares. Null means there is none, which the route reads as "do not
/// split", and on the last segment of a run that is exactly right.
/// </param>
/// <param name="SplitIndex"><c>getsplitindex</c>, which is -1 while the timer is not running.</param>
public sealed record LiveSplitView(
    LiveSplitPhase Phase,
    string? CurrentSplit,
    string? UpcomingSplit,
    int SplitIndex = -1)
{
    public static LiveSplitView Empty { get; } = new(LiveSplitPhase.Unknown, null, null);
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
/// to the XMB, is the one place game time is stopped and set outright: see
/// <see cref="StopGameTime"/> and <see cref="StartGameTimeAsync"/> for the order and why it is
/// that order.
/// </para>
/// <para>
/// A quit to the XMB takes the console session away and brings a new one back, so the two halves
/// of that pair belong to different sessions and often to different generations of the same game.
/// The open half is therefore kept across the session going away and coming back, and dropped only
/// when a genuinely different game turns up or the run itself starts or resets.
/// </para>
/// <para>
/// The timer phase, the split index and the split names are read back from LiveSplit on connect,
/// after every command sent and once a second, so a manual reset or undo in LiveSplit is followed
/// rather than fought, and the name the planet route needs is already in hand when a planet event
/// lands. The upcoming name comes from <c>getupcomingsplitname</c> and nowhere else, which is one
/// of the reasons the LiveSplit development build is required.
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

    /// <summary>
    /// The open half of one normalised pair: when the console said it started, and the row it
    /// started under. The row is kept with it because a Deadlocked quit takes the whole session
    /// away, and the descriptors can come back after the resume does; the pair is then still paid
    /// at the parameter it was opened with rather than falling through to a plain resume.
    /// </summary>
    private readonly record struct PairStart(uint TimeMs, AutosplitEventDesc? Row);

    /// <summary>
    /// The open half of every normalised pair, by the family it belongs to and its reason code.
    /// The family is in the key because a load and a pause that happen to share a code are two
    /// different pairs, and only the end of the right one closes each.
    /// </summary>
    private readonly Dictionary<(AutosplitKind Family, byte Code), PairStart> _openPairs = new();

    /// <summary>
    /// The game the open pairs belong to. A pair outlives the session that opened it — a quit to
    /// the XMB is exactly the case the pairs exist for — so it is this, not the running game, that
    /// says whether what is open still means anything.
    /// </summary>
    private GameId _pairsGame = GameId.None;

    private readonly object _gate = new();

    /// <summary>
    /// The two halves of a normalised pause, one after the other. The resume reads game time and
    /// writes it back, so it must never start asking while the quit that opened it is still
    /// setting up, however close together the console reports them.
    /// </summary>
    private Task _gameTimeWork = Task.CompletedTask;

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

        // A fresh connection knows nothing about the run: ask where the timer is before the first
        // event arrives. Off the worker thread, which has a socket to pump.
        _liveSplit.Established += () => _ = Task.Run(RefreshAsync);
    }

    /// <summary>
    /// The game this engine is deciding for. The app feeds it the game the console last described
    /// rather than the one telemetry reports this instant, so a quit to the XMB does not turn it to
    /// None and take the game's settings and route with it.
    /// </summary>
    public GameId Game
    {
        get => _game;
        set
        {
            if (_game == value) return;
            _game = value;

            // What is open belongs to the game that opened it, and a Deadlocked quit takes the
            // session to the XMB and back with a pause still open: the module's clock keeps
            // counting across the quit, so the pair still means what it meant. Only a genuinely
            // different game leaves nothing of the last one worth keeping.
            if (value == GameId.None) return;
            lock (_gate)
            {
                if (_pairsGame == GameId.None || _pairsGame == value) return;
                _openPairs.Clear();
                _pairsGame = GameId.None;
            }
        }
    }

    /// <summary>The AUTOSPLIT_DESCRIBE rows for the running game; empty when it has no watcher.</summary>
    public IReadOnlyList<AutosplitEventDesc> Descriptors { get; set; } = Array.Empty<AutosplitEventDesc>();

    /// <summary>PLANET_LIST, only so the log can name the planet an event carried.</summary>
    public IReadOnlyList<string> PlanetNames { get; set; } = Array.Empty<string>();

    public LiveSplitView View => _view;

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

        // A run that starts or resets is a run in which nothing of the last one is still open,
        // whatever the master switches say about the timer itself.
        if (ev.Kind is AutosplitKind.Start or AutosplitKind.Reset) ClearPairs();

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
                OpenPair(ev, desc);
                logged = true;
                Record(ev, what, $"timing: started, {Seconds(desc.ParamUs)} of it is free", false);
                return false;

            case AutosplitKind.Resume:
                return false;

            case AutosplitKind.LoadEnd:
            {
                if (TakePair(ev) is not { } start || !desc.Normalise) return false;

                logged = true;
                long durationUs = (long)AutosplitEvent.Elapsed(start.TimeMs, ev.TimeMs) * 1000;
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

    /// <summary>
    /// The key one pair is remembered under. The end of a pair carries its start's code and is
    /// described by its start's row — a LOAD_END belongs to a LOAD_START, a RESUME to a PAUSE — so
    /// both halves name the same key here, exactly as they do in <see cref="DescriptorFor"/>.
    /// </summary>
    private static (AutosplitKind Family, byte Code) PairKey(AutosplitEvent ev) => (ev.Kind switch
    {
        AutosplitKind.LoadEnd => AutosplitKind.LoadStart,
        AutosplitKind.Resume => AutosplitKind.Pause,
        _ => ev.Kind,
    }, ev.Code);

    /// <summary>Remembers the open half of a pair, with the row it was opened under.</summary>
    private void OpenPair(AutosplitEvent ev, AutosplitEventDesc? desc)
    {
        lock (_gate)
        {
            _openPairs[PairKey(ev)] = new PairStart(ev.TimeMs, desc);
            _pairsGame = _game;
        }
    }

    /// <summary>Takes this event's open half back out, or null when nothing of its pair is open.</summary>
    private PairStart? TakePair(AutosplitEvent ev)
    {
        lock (_gate) return _openPairs.Remove(PairKey(ev), out var start) ? start : null;
    }

    /// <summary>Drops every open pair: a different game, or a run that has just started or reset.</summary>
    private void ClearPairs()
    {
        lock (_gate)
        {
            _openPairs.Clear();
            _pairsGame = GameId.None;
        }
    }

    /// <summary>Why a load event did nothing, which is only ever one of two things.</summary>
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
            // Game time is brought into existence with the run, so the first quit has something
            // real to freeze and nothing has to be read back to make it so. Sending it again at
            // the quit, or on a second run, changes nothing.
            return Act(ev, what, LiveSplitClient.StartTimer,
                phase == LiveSplitPhase.Unknown ? "the timer phase is not known yet" : null,
                LiveSplitClient.InitialiseGameTime, "so the run has a game time from the first frame");
        }

        Record(ev, what, $"ignored: the timer is {phase}", false);
        return false;
    }

    /// <summary>
    /// A split candidate. The master switch, then the row's own checkbox, then — for the planet
    /// code alone — the route: the split names are the planets of the run, so the planet just
    /// entered has to be the one the <em>upcoming</em> split names, exactly as every old script
    /// compared <c>timer.Run[timer.CurrentSplitIndex + 1].Name</c>. That name is whatever
    /// <c>getupcomingsplitname</c> last answered, and no name means the run has none.
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

        string? name = _view.UpcomingSplit;
        if (string.IsNullOrWhiteSpace(name))
        {
            Record(ev, what, "no split: there is no upcoming split, so this is the last segment", false);
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

        if (ev.Kind == AutosplitKind.Pause)
        {
            return desc is { Normalise: true }
                ? StopGameTime(ev, desc, what)
                : Act(ev, what, LiveSplitClient.Pause);
        }

        // The pause that opened this answers for it: the row is remembered with the pair, so a
        // resume that arrives before the new session's descriptors do is still the normalised one.
        var open = TakePair(ev);
        var row = open?.Row ?? desc;
        if (row is not { Normalise: true }) return Act(ev, what, LiveSplitClient.Resume);

        // Game time must never be left frozen. An unpause with nothing paused does nothing at all
        // in LiveSplit, so the safe move when no pause of this code is open is to send it anyway
        // and say so, rather than to leave a run whose clock stopped at the quit.
        if (open is null) return UnpauseWithNoPause(ev, row, what);

        return StartGameTime(ev, row, what);
    }

    /// <summary>
    /// A RESUME whose PAUSE this client never saw: the client connected in the middle of a quit,
    /// or the pair was dropped with a different game. Nothing is added to game time, because the
    /// engine never stopped it and does not know what it would be adding to, but the unpause goes
    /// out regardless: it costs nothing when nothing is paused and it is the only thing that can
    /// rescue a clock somebody else froze.
    /// </summary>
    private bool UnpauseWithNoPause(AutosplitEvent ev, AutosplitEventDesc desc, string what)
    {
        _liveSplit.Send(LiveSplitClient.UnpauseGameTime);
        Record(ev, what, $"{LiveSplitClient.UnpauseGameTime} (unpaused, though no pause of that code "
                         + $"was open, so the {Seconds(desc.ParamUs)} for it was not put on)", true);
        return true;
    }

    /// <summary>
    /// The quit: game time stops while real time carries on, which is what an ASL's
    /// <c>isLoading</c> returning true does. The timer phase does not move — LiveSplit is still
    /// Running — so the optimistic phase this engine keeps is left exactly as it is.
    /// <para>
    /// Two commands, and the order is the whole of it. In LiveSplit's own <c>LiveSplitState</c> a
    /// run's game time is <b>null</b> until something creates one; until then
    /// <c>getcurrentgametime</c> answers real time and <c>IsGameTimePaused</c> freezes nothing a
    /// query can see, so a pause on its own would leave the resume reading a real time with the
    /// whole trip to the XMB in it. <c>addloadingtimes 0</c> creates one in place: it gives
    /// <c>LoadingTimes</c> a value, game time comes out equal to real time, and the
    /// <c>pausegametime</c> after it freezes something real. Nothing is read and nothing is
    /// rewound, which is why this half no longer asks LiveSplit anything.
    /// </para>
    /// <para>
    /// It is still queued behind whatever half went before it: the resume does ask, and the two
    /// halves must reach the socket in the order the console reported them.
    /// </para>
    /// </summary>
    private bool StopGameTime(AutosplitEvent ev, AutosplitEventDesc desc, string what)
    {
        // The pair is remembered by code so the resume knows a quit of its own is open, and by
        // game so the trip through the XMB the quit itself is does not lose it.
        OpenPair(ev, desc);

        QueueGameTime(() =>
        {
            _liveSplit.Send(LiveSplitClient.InitialiseGameTime);
            _liveSplit.Send(LiveSplitClient.PauseGameTime);
            Record(ev, what, $"{LiveSplitClient.InitialiseGameTime} (so the run has a game time to "
                             + $"freeze) then {LiveSplitClient.PauseGameTime} (the resume puts "
                             + $"{Seconds(desc.ParamUs)} back on)", true);
            return Task.CompletedTask;
        });

        return true;
    }

    /// <summary>
    /// The game is back: the frozen clock is read, the row's parameter goes on it and game time
    /// starts again from there. The read is safe because the quit initialised game time before it
    /// stopped it, so what comes back is the clock as it stood when the player left — even though
    /// the console went to the XMB and came back with a new session in between.
    /// </summary>
    private bool StartGameTime(AutosplitEvent ev, AutosplitEventDesc desc, string what)
    {
        QueueGameTime(() => StartGameTimeAsync(ev, desc, what));
        return true;
    }

    private async Task StartGameTimeAsync(AutosplitEvent ev, AutosplitEventDesc desc, string what)
    {
        long paramUs = desc.ParamUs;
        var owed = TimeSpan.FromTicks(paramUs * TimeSpan.TicksPerMicrosecond);

        string? reply = await _liveSplit.QueryAsync(LiveSplitClient.GetCurrentGameTime).ConfigureAwait(false);
        if (!LiveSplitClient.TryParseGameTime(reply, out var frozen))
        {
            // The clock cannot be moved without knowing where it is, but it must not be left
            // stopped: a run whose game time never starts again is worse than one that is 14.8 s
            // short. The log says what went wrong so the run can be fixed by hand.
            _liveSplit.Send(LiveSplitClient.UnpauseGameTime);
            Record(ev, what, $"{LiveSplitClient.UnpauseGameTime} (error: LiveSplit did not answer "
                             + $"{LiveSplitClient.GetCurrentGameTime}, so the {Seconds(paramUs)} for "
                             + "this quit was not put on)", true);
            return;
        }

        // The old script's two lines, in its order: set the clock while it is still stopped, so the
        // value cannot move under us, then let it run on from there.
        string set = LiveSplitClient.SetGameTimeCommand(frozen + owed);
        _liveSplit.Send(set);
        _liveSplit.Send(LiveSplitClient.UnpauseGameTime);
        Interlocked.Increment(ref _adjustments);
        Record(ev, what, $"{set} (+{Seconds(paramUs)}) then {LiveSplitClient.UnpauseGameTime}", true);
    }

    /// <summary>
    /// Runs one half of a pause pair after whatever half went before it. Console events arrive on
    /// a network thread and neither half may block it, so each is queued rather than awaited.
    /// </summary>
    private void QueueGameTime(Func<Task> work)
    {
        lock (_gate)
        {
            _gameTimeWork = _gameTimeWork
                .ContinueWith(_ => work(), TaskScheduler.Default)
                .Unwrap();
        }
    }

    /// <param name="then">
    /// A second command sent straight after the first and named in the log with it, for the one
    /// place where a decision is two commands rather than one.
    /// </param>
    private bool Act(
        AutosplitEvent ev, string what, string command, string? because = null,
        string? then = null, string? thenBecause = null)
    {
        _liveSplit.Send(command);
        string line = because is null ? command : $"{command} ({because})";

        if (then is not null)
        {
            _liveSplit.Send(then);
            line += thenBecause is null ? $" then {then}" : $" then {then} ({thenBecause})";
        }

        Record(ev, what, line, true);

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
    /// Reads the timer phase, the split index and the two split names back. One refresh runs at a
    /// time: a burst of events would otherwise queue four queries each behind the same connection.
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

            // The one name the route compares. An empty answer is the run's last segment, and the
            // route reads that as "do not split", which is what it means.
            string? upcoming = Clean(await Ask(LiveSplitClient.GetUpcomingSplitName));

            var view = new LiveSplitView(phase, current, upcoming, index);
            _post(() => _view = view);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }

        Task<string?> Ask(string command) => _liveSplit.QueryAsync(command);
    }

    /// <summary>LiveSplit answers a query it cannot serve with an empty line; that is "no split".</summary>
    private static string? Clean(string? reply)
    {
        var trimmed = reply?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed == "-" ? null : trimmed;
    }

    /// <summary>
    /// <c>getsplitindex</c>, which is -1 while the timer is not running. An answer that is not a
    /// number, or none at all, is treated the same way: the panel simply says there is no index.
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
