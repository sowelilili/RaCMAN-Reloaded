using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The manip categories at the top of the Manips section (revision 1.21): the category type
/// dropdown, the auto-reset and remember switches, Apply now, the warning about per-run manips and
/// a line for the console's last event. The headings MANIP_LIST names under it (Deadlocked's Fixes,
/// UYA's Trophies) are drawn by <see cref="GamePanel"/>, which owns the feature controls, from
/// <see cref="HeaderBlocks"/>, each with <see cref="DrawStatusLines"/> under it: the trophy reset's
/// status, from its status readout in the last telemetry (revision 1.21).
/// <para>
/// Everything here is drawn from <see cref="AppState.Manips"/>, MANIP_LIST's last answer, and each
/// control sends one request and reads the list again. The client keeps no selection and no option
/// of its own: qwark applies the category at the start of a run whether or not a PC is connected,
/// so the console is the only place either can live.
/// </para>
/// <para>
/// Refresh policy: MANIP_LIST is read when the area comes on screen. Everything else is
/// <see cref="AppState"/>'s, which reads it on a timer while a game with groups is up, whatever
/// panel is open, as well as after every manip request, on every way back into a game, after a
/// planet change and after a load this client asked for (<see cref="AppState.ManipPollSeconds"/>).
/// </para>
/// </summary>
public static class ManipsArea
{
    /// <summary>How long the area may go undrawn before it counts as opened again.</summary>
    private const long ReopenAfterMs = 500;

    private const float ComboWidth = 260f;

    private static long _lastDrawnMs = long.MinValue / 2;

    /// <summary>Whether the area has been drawn this run, for the headless summary.</summary>
    public static bool Drawn { get; private set; }

    /// <summary>
    /// The warning under the controls, or null for none: auto-reset is off and the selected group
    /// has per-run writes, which then happen only on Apply now. An NG+ group can be reset every run;
    /// a new-game group has to be, since a run that starts with last run's values is not a fresh
    /// game.
    /// </summary>
    public static string? Warning(ManipList list)
    {
        if (list.AutoReset) return null;
        if (list.SelectedGroup is not { HasPerRun: true } group) return null;

        string verb = group.IsNgPlus ? "can" : "must";
        return $"{group.Name} has manips that {verb} be reset every run. Turn on auto-reset to apply them automatically.";
    }

    /// <summary>The status line under the controls: the console's last event, whatever its kind, or null for none.</summary>
    public static string? LastEventLine(ManipList list) =>
        ManipEvents.NoticeFor(list) is { } notice ? $"Last: {notice.Text}" : null;

    /// <summary>
    /// The line under UYA's Trophies heading for the trophy reset's status word (revision 1.21), or
    /// null for nothing to say: no reset this boot, or a state this client does not know. It is read
    /// at a glance between attempts, so it stays short; the console's log has the details.
    /// </summary>
    public static string? TrophyLine(TrophyResetStatus status) => status.State switch
    {
        TrophyResetState.Running => status.Step switch
        {
            TrophyResetStep.Deleting => "Trophies: resetting... (deleting)",
            TrophyResetStep.Registering when status.Registrations >= 2 =>
                $"Trophies: resetting... (registering, attempt {status.Registrations})",
            TrophyResetStep.Registering => "Trophies: resetting... (registering)",
            _ => "Trophies: resetting...",
        },
        TrophyResetState.Ready => $"Trophies ready ({status.Seconds} s)",
        TrophyResetState.Failed => $"Trophy reset failed: {FailureText(status)}",
        _ => null,
    };

    /// <summary>Why a trophy reset failed, in a few words.</summary>
    private static string FailureText(TrophyResetStatus status) => status.Failure switch
    {
        TrophyResetFailure.DeleteStopped => "a trophy file could not be deleted",
        TrophyResetFailure.Unregistered => "the game could not re-register its trophies",
        TrophyResetFailure.NoAnswer => "the console did not answer, restart the game",
        TrophyResetFailure.LoadEnded => "the Veldin load ended first",
        TrophyResetFailure.GameRefused => "the game refused it",
        TrophyResetFailure.NotTaken => "the game did not respond",
        TrophyResetFailure.Refused when status.RefusedWith == Status.NotFound => "trophy folder not found",
        TrophyResetFailure.Refused => $"refused ({status.RefusedWith})",
        TrophyResetFailure.Stopped => "the game changed under it",
        _ => $"reason {status.Detail}",
    };

    /// <summary>
    /// The status line for <paramref name="feature"/>, when it is an ACTION that names a status
    /// readout (UYA's Reset trophies, revision 1.21), read from <paramref name="session"/>; null for
    /// every other feature, for a qwark that names no status readout, and for nothing to say.
    /// </summary>
    public static (string Text, TrophyResetState State)? StatusLine(SessionInfo session, Feature feature)
    {
        if (feature.StatusReadout is not { } index || session.ReadoutAt(index) is not { } word) return null;

        var status = TrophyResetStatus.Parse(word);
        return TrophyLine(status) is { } text ? (text, status.State) : null;
    }

    /// <summary>
    /// The status lines of a heading's features, drawn under its button and its toggle: grey while
    /// a reset runs, green once the trophies are ready, red when one failed. Nothing at all before
    /// the first reset of a boot.
    /// </summary>
    public static void DrawStatusLines(AppState state, IReadOnlyList<Feature> features)
    {
        foreach (var feature in features)
        {
            if (StatusLine(state.Session, feature) is not { } line) continue;

            switch (line.State)
            {
                case TrophyResetState.Ready:
                    Ui.Success(line.Text);
                    break;

                case TrophyResetState.Failed:
                    Ui.Error(line.Text);
                    break;

                default:
                    Ui.Hint(line.Text);
                    break;
            }
        }
    }

    /// <summary>
    /// The headings drawn under the controls: each header MANIP_LIST names, in its order, with the
    /// features of <paramref name="features"/> it lists, in the header's order and whatever their
    /// kind. A feature two headers list goes under the first, and a header with nothing to draw
    /// under it is left out rather than drawn as a heading over nothing.
    /// </summary>
    public static IReadOnlyList<GameLayout.FeatureBlock> HeaderBlocks(ManipList list, IEnumerable<Feature> features)
    {
        var byId = new Dictionary<byte, Feature>();
        foreach (var feature in features) byId.TryAdd(feature.Id, feature);

        var taken = new HashSet<byte>();
        var blocks = new List<GameLayout.FeatureBlock>();
        foreach (var header in list.Headers)
        {
            var listed = header.FeatureIds
                .Where(id => byId.ContainsKey(id) && taken.Add(id))
                .Select(id => byId[id])
                .ToArray();

            if (listed.Length > 0) blocks.Add(new GameLayout.FeatureBlock(header.Name, listed));
        }

        return blocks;
    }

    /// <summary>
    /// What the headless summary prints: "hidden" while there is no list, otherwise the selection,
    /// the two options, the groups, the headings with the features under each, the last event,
    /// whether there is a warning and the trophy reset's status line ("-" for none).
    /// </summary>
    public static string Summary(AppState state)
    {
        if (state.Manips is not { } list) return "hidden";

        string Name(string text) => text.Replace(' ', '_');
        var blocks = HeaderBlocks(list, state.Describe.Features);
        string groups = string.Join(",", list.Groups.Select(g =>
            Name(g.Name) + (g.IsExtension ? "*" : string.Empty) + (g.HasPerRun ? "!" : string.Empty)));
        string headers = string.Join(" ", blocks.Select(b =>
            $"{Name(b.Heading!)}:{string.Join(",", b.Features.Select(f => Name(f.Label)))}"));
        string? status = blocks.SelectMany(b => b.Features)
            .Select(f => StatusLine(state.Session, f)?.Text)
            .FirstOrDefault(text => text is not null);

        return $"selected={Name(list.GroupName(list.Selected))} auto={(list.AutoReset ? "on" : "off")} "
               + $"remember={(list.Remember ? "on" : "off")} groups=[{groups}] "
               + $"headers=[{headers}] event={list.EventSeq}:{list.EventKind} "
               + $"toasts={state.ManipToasts} drawn={(Drawn ? "yes" : "no")} "
               + $"warning={(Warning(list) is null ? "no" : "yes")} "
               + $"trophies={(status is null ? "-" : Name(status))}";
    }

    public static void Draw(AppState state, ManipList list)
    {
        Poll(state);
        Drawn = true;

        ImGui.PushID("manips");

        DrawCategoryType(state, list);

        // Both switches are the console's: what is drawn is what MANIP_LIST said, and a click sends
        // the pair with the one that changed.
        bool autoReset = list.AutoReset;
        if (ImGui.Checkbox("Auto-reset", ref autoReset)) state.SetManipOptions(autoReset, list.Remember);
        // The console's trigger is the start of a run, not every load: a load into the first planet
        // in RaC1, RaC2 and UYA, a new game or a challenge-mode start in Deadlocked.
        Ui.Tooltip("Apply the category's per-run manips again at the start of every run");

        ImGui.SameLine();
        bool remember = list.Remember;
        if (ImGui.Checkbox("Remember category", ref remember)) state.SetManipOptions(list.AutoReset, remember);
        Ui.Tooltip("Automatically apply your last category's manips on the next game boot.");

        bool none = list.SelectedGroup is null;
        ImGui.BeginDisabled(none);
        if (ImGui.Button("Apply now")) state.ApplyManips();
        ImGui.EndDisabled();
        Ui.Tooltip(none
            ? "Pick a category type first."
            : "Apply the category's manips now. A save must be loaded.");

        if (Warning(list) is { } warning) Ui.Warning(warning);
        if (LastEventLine(list) is { } line) Ui.Hint(line);

        ImGui.PopID();
    }

    /// <summary>
    /// Reads the list when the area comes back on screen, so what it shows is fresh the moment it is
    /// looked at. The reads while it stays there are AppState's timer; the read itself knows when the
    /// op answers.
    /// </summary>
    private static void Poll(AppState state)
    {
        long now = Environment.TickCount64;
        bool reopened = now - _lastDrawnMs > ReopenAfterMs;
        _lastDrawnMs = now;

        if (reopened) state.RefreshManips();
    }

    /// <summary>
    /// None and then the groups in the console's order. Extensions are greyed but can be picked,
    /// and every entry lists its leaderboard categories on hover, one per line, as does the closed
    /// dropdown for the selected group.
    /// </summary>
    private static void DrawCategoryType(AppState state, ManipList list)
    {
        var selected = list.SelectedGroup;
        string preview = list.GroupName(list.Selected);

        ImGui.SetNextItemWidth(ComboWidth);
        if (!ImGui.BeginCombo("Category type", preview))
        {
            if (selected is not null) CategoriesTooltip(selected);
            return;
        }

        if (ImGui.Selectable(ManipList.NoneName, list.Selected == ManipList.None) && list.Selected != ManipList.None)
        {
            state.SelectManipGroup(ManipList.None);
        }

        Ui.Tooltip("No manips: nothing is written.");

        foreach (var group in list.Groups)
        {
            ImGui.PushID(group.Id);

            if (group.IsExtension) ImGui.PushStyleColor(ImGuiCol.Text, Ui.Grey);
            bool picked = ImGui.Selectable(group.Name, group.Id == list.Selected);
            if (group.IsExtension) ImGui.PopStyleColor();

            CategoriesTooltip(group);
            ImGui.PopID();

            if (picked && group.Id != list.Selected) state.SelectManipGroup(group.Id);
        }

        ImGui.EndCombo();
    }

    /// <summary>The categories a group applies to, one per line, on hover.</summary>
    private static void CategoriesTooltip(ManipGroup group)
    {
        if (group.Categories.Length == 0) return;
        Ui.Tooltip(string.Join("\n", group.Categories));
    }
}
