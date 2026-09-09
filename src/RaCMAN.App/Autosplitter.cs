using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>What LiveSplit last told us about itself. Replaced whole, so it is always self-consistent.</summary>
public sealed record LiveSplitView(LiveSplitPhase Phase, string? CurrentSplit, string? UpcomingSplit)
{
    public static LiveSplitView Empty { get; } = new(LiveSplitPhase.Unknown, null, null);
}

/// <summary>One line of the panel's rolling log: what arrived, and what was done about it.</summary>
public sealed record AutosplitLogEntry(uint Seq, uint Tick, AutosplitKind Kind, string Event, string Action, bool Acted);

/// <summary>
/// The decision half of "qwark detects, the client decides": it takes the run events the console
/// emits unconditionally, applies the user's settings and turns what survives into LiveSplit
/// commands. It keeps no timer of its own and never sets game time — LiveSplit owns the clock.
/// <para>
/// The timer phase and the two split names are read back from LiveSplit on connect, after every
/// command sent and once a second, so a manual reset or undo in LiveSplit is followed rather than
/// fought, and the names needed for the planet route are already in hand when a planet event lands.
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
    private readonly object _gate = new();

    private volatile LiveSplitView _view = LiveSplitView.Empty;
    private int _refreshing;
    private int _received;
    private int _acted;
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

        // A fresh connection knows nothing about the run; ask before the first event arrives.
        _liveSplit.Established += () => _ = RefreshAsync();
    }

    /// <summary>The running game, from telemetry. Chooses the settings entry and the planet route.</summary>
    public GameId Game { get; set; } = GameId.None;

    /// <summary>The AUTOSPLIT_DESCRIBE rows for the running game; empty when it has no watcher.</summary>
    public IReadOnlyList<AutosplitEventDesc> Descriptors { get; set; } = Array.Empty<AutosplitEventDesc>();

    /// <summary>PLANET_LIST, only so the log can name the planet an event carried.</summary>
    public IReadOnlyList<string> PlanetNames { get; set; } = Array.Empty<string>();

    public LiveSplitView View => _view;

    /// <summary>Events received from the console, whatever was done with them.</summary>
    public int Received => Volatile.Read(ref _received);

    /// <summary>Events that became a LiveSplit command.</summary>
    public int Acted => Volatile.Read(ref _acted);

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
    /// One event from the console. Called on a network thread: it decides, sends at most one
    /// command and logs, and never touches the UI.
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
        if (desc is not null && !game.EventEnabled(desc.Label, desc.EnabledByDefault))
        {
            Record(ev, what, $"ignored: \"{desc.Label}\" is switched off", false);
            return;
        }

        switch (ev.Kind)
        {
            case AutosplitKind.Start:
                HandleStart(ev, what);
                break;

            case AutosplitKind.Split:
                HandleSplit(ev, desc, what, game);
                break;

            case AutosplitKind.Reset:
                if (game.NeverReset) Record(ev, what, "ignored: never reset is on", false);
                else Act(ev, what, LiveSplitClient.Reset);
                break;

            case AutosplitKind.Pause:
                Act(ev, what, LiveSplitClient.Pause);
                break;

            case AutosplitKind.Resume:
                Act(ev, what, LiveSplitClient.Resume);
                break;

            default:
                Record(ev, what, $"ignored: unknown event kind {(byte)ev.Kind}", false);
                break;
        }
    }

    /// <summary>
    /// A run started. The timer only starts when LiveSplit says it is not running, so a start
    /// event mid-run (a level reload, a second console) leaves the timer alone. An unknown phase
    /// is treated as "not running": the phase query failed, and refusing to start a run because of
    /// that would be worse than a start LiveSplit ignores.
    /// </summary>
    private void HandleStart(AutosplitEvent ev, string what)
    {
        var phase = _view.Phase;
        if (phase is LiveSplitPhase.NotRunning or LiveSplitPhase.Unknown)
        {
            Act(ev, what, LiveSplitClient.StartTimer,
                phase == LiveSplitPhase.Unknown ? "the timer phase is not known yet" : null);
            return;
        }

        Record(ev, what, $"ignored: the timer is {phase}", false);
    }

    private void HandleSplit(AutosplitEvent ev, AutosplitEventDesc? desc, string what, AutosplitGameSettings game)
    {
        bool routed = game.PlanetRoute
                      && (desc?.PlanetRoute ?? ev.Code == AutosplitEvent.PlanetEnteredCode)
                      && ev.Code == AutosplitEvent.PlanetEnteredCode;

        if (!routed)
        {
            Act(ev, what, LiveSplitClient.Split);
            return;
        }

        var view = _view;
        string? name = game.NamesAreDestination ? view.CurrentSplit : view.UpcomingSplit;
        string which = game.NamesAreDestination ? "current split" : "next split";

        if (AutosplitRoutes.Matches(Game, (int)ev.Arg, name))
        {
            Act(ev, what, LiveSplitClient.Split, $"{which} \"{name}\" is this planet");
            return;
        }

        string shown = string.IsNullOrWhiteSpace(name) ? "(none)" : name!;
        Record(ev, what, $"no split: {which} \"{shown}\" is not on this planet's route", false);
    }

    private void Act(AutosplitEvent ev, string what, string command, string? because = null)
    {
        _liveSplit.Send(command);
        Interlocked.Increment(ref _acted);
        Record(ev, what, because is null ? command : $"{command} ({because})", true);

        // What we just sent moved the timer, so the phase and the split names are now stale.
        _ = RefreshAsync();
    }

    private void Record(AutosplitEvent ev, string what, string action, bool acted)
    {
        var entry = new AutosplitLogEntry(ev.Seq, ev.Tick, ev.Kind, what, action, acted);
        lock (_gate)
        {
            _log.Add(entry);
            if (_log.Count > LogLength) _log.RemoveRange(0, _log.Count - LogLength);
        }
    }

    /// <summary>The row that names this event's reason code, or null when the game described none.</summary>
    public AutosplitEventDesc? DescriptorFor(AutosplitEvent ev)
    {
        foreach (var desc in Descriptors)
        {
            if (desc.Kind == ev.Kind && desc.Code == ev.Code) return desc;
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
    /// Reads the timer phase and the two split names back. One refresh runs at a time: a burst of
    /// events would otherwise queue three queries each behind the same connection.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;

        try
        {
            if (!_liveSplit.IsConnected) return;

            var phase = LiveSplitClient.ParsePhase(await _liveSplit.QueryAsync(LiveSplitClient.GetCurrentTimerPhase)
                .ConfigureAwait(false));
            string? current = await _liveSplit.QueryAsync(LiveSplitClient.GetCurrentSplitName).ConfigureAwait(false);
            string? upcoming = await _liveSplit.QueryAsync(LiveSplitClient.GetUpcomingSplitName).ConfigureAwait(false);

            var view = new LiveSplitView(phase, Clean(current), Clean(upcoming));
            _post(() => _view = view);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    /// <summary>LiveSplit answers a query it cannot serve with an empty line; that is "no split".</summary>
    private static string? Clean(string? reply)
    {
        var trimmed = reply?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>The panel's test buttons: one command, no decisions, logged as a manual action.</summary>
    public void SendManual(string command)
    {
        if (!_liveSplit.IsConnected) return;

        _liveSplit.Send(command);
        Record(default, "(manual)", command, false);
        _ = RefreshAsync();
    }
}
