using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The autosplitter: the LiveSplit connection, what each of the running game's run events should
/// do, and a log of what was decided. qwark detects; everything on this page is the PC's decision.
/// </summary>
public static class AutosplitterPanel
{
    private static string _splitsFile = string.Empty;
    private static bool _initialised;
    private static bool _dialogOpen;

    /// <summary>Drops the splits-file draft, so an import or a restart is picked up.</summary>
    public static void Reset() => _initialised = false;

    public static void Draw(AppState state)
    {
        var settings = state.Settings;
        var autosplit = settings.Autosplit;

        if (!_initialised)
        {
            _splitsFile = autosplit.SplitsFile;
            _initialised = true;
        }

        Ui.Heading("Autosplitter");

        // No separator of its own before the game section: its heading draws one, and the panel
        // has to hold a game with eight subsplits inside 940x580 without scrolling.
        DrawConnection(state, autosplit);
        DrawGameSection(state, autosplit);
        ImGui.Separator();

        // The log takes whatever height is left, so the panel always fits the window and only the
        // log itself ever scrolls.
        DrawLog(state);
    }

    private static void DrawConnection(AppState state, AutosplitSettings autosplit)
    {
        var settings = state.Settings;

        bool enabled = autosplit.Enabled;
        if (ImGui.Checkbox("Enable autosplitting", ref enabled))
        {
            autosplit.Enabled = enabled;
            settings.Save();
            if (enabled) Connect(state, autosplit);
            else state.LiveSplit.Stop();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!autosplit.Enabled);
        if (ImGui.Button(state.LiveSplit.IsConnected ? "Reconnect" : "Connect"))
        {
            state.LiveSplit.Stop();
            Connect(state, autosplit);
        }

        ImGui.EndDisabled();

        var colour = state.LiveSplit.Status switch
        {
            LiveSplitStatus.Connected => Ui.Green,
            LiveSplitStatus.Connecting => Ui.Yellow,
            _ => Ui.Grey,
        };

        ImGui.TextColored(colour, state.LiveSplit.StatusLine);

        // The endpoint is on the Settings panel: it is typed once, if ever, and the rest of this
        // panel is about the run.
        Ui.Hint(state.LiveSplit.IsConnected
            ? DescribeTimer(state.Autosplitter.View)
            : $"Looking for LiveSplit's server at {autosplit.Host}:{autosplit.Port}; change that in Settings.");

        DrawSplitsFile(state, autosplit);

        // Sending a split by hand is a way of proving the wiring, not a way of running: during a
        // run the console decides, and a stray press here would put the timer out of step with it.
        if (Ui.Debug)
        {
            ImGui.BeginDisabled(!state.LiveSplit.IsConnected);
            if (ImGui.Button("Split now")) state.Autosplitter.SendManual(LiveSplitClient.Split);
            ImGui.SameLine();
            if (ImGui.Button("Reset")) state.Autosplitter.SendManual(LiveSplitClient.Reset);
            ImGui.EndDisabled();
            ImGui.SameLine();
            Ui.Hint("Test the connection without waiting for the game.");
        }
    }

    /// <summary>
    /// Points the client at LiveSplit, and says the attempt was the user's: a failure from here
    /// earns the "LiveSplit not found" popup even if the reconnect loop has already had its one.
    /// </summary>
    private static void Connect(AppState state, AutosplitSettings autosplit)
    {
        LiveSplitModal.ArmForAttempt();
        state.LiveSplit.Start(autosplit.Host, autosplit.Port);
    }

    private static string DescribeTimer(LiveSplitView view)
    {
        string current = view.CurrentSplit ?? "(none)";
        string upcoming = view.UpcomingSplit ?? "(none)";
        string index = view.SplitIndex >= 0 ? $"#{view.SplitIndex} " : string.Empty;
        return $"Timer {view.Phase} | this split {index}\"{current}\" | next split \"{upcoming}\"";
    }

    /// <summary>
    /// Which run's split names the planet route counts from. LiveSplit's server cannot hand over a
    /// split by index and most builds will not name the upcoming one at all, so the names come out
    /// of the run's own .lss: found in LiveSplit's recent-splits list, or named here by hand.
    /// </summary>
    private static void DrawSplitsFile(AppState state, AutosplitSettings autosplit)
    {
        var runs = state.Autosplitter.Runs.State;

        ImGui.SetNextItemWidth(-330);
        Ui.InputTextWithHint("Splits file", "found from LiveSplit's recent splits", ref _splitsFile, 512);

        ImGui.SameLine();
        ImGui.BeginDisabled(!FileDialog.IsSupported || _dialogOpen);
        if (ImGui.Button("Browse...")) Browse(state);
        ImGui.EndDisabled();

        ImGui.SameLine();
        bool changed = !string.Equals(_splitsFile.Trim(), autosplit.SplitsFile, StringComparison.Ordinal);
        if (ImGui.Button(changed ? "Use" : "Rescan")) ApplySplitsFile(state, autosplit);

        if (runs.Run is not null)
        {
            // Not TextColored: a category is called "Any%" and printf would eat it.
            Ui.Text(runs.Verified ? Ui.Green : Ui.Yellow, $"Splits: {runs.Summary}");
            ImGui.SameLine();
            Ui.Text(Ui.Grey, $"| upcoming name from: {state.Autosplitter.View.UpcomingSourceLabel}");
        }
        else
        {
            Ui.Hint(runs.Problem ?? "No splits file yet. Connect to LiveSplit with your run loaded, "
                                    + "or pick the .lss file here.");
        }
    }

    /// <summary>Saves what the box says and re-reads the run files off the render thread.</summary>
    private static void ApplySplitsFile(AppState state, AutosplitSettings autosplit)
    {
        autosplit.SplitsFile = _splitsFile.Trim().Trim('"').Trim();
        _splitsFile = autosplit.SplitsFile;
        state.Settings.Save();

        state.Run(async () =>
        {
            var runs = await state.Autosplitter.ReloadRunsAsync().ConfigureAwait(false);
            state.Post(() =>
            {
                if (runs.Problem is { Length: > 0 } problem) state.AddToast(problem, ToastKind.Error);
                else if (runs.Run is not null) state.AddToast($"Splits: {runs.Run.Summary}", ToastKind.Success);
            });

            // The names only line up once LiveSplit has been asked where the run is.
            await state.Autosplitter.RefreshAsync().ConfigureAwait(false);
        });
    }

    /// <summary>
    /// The native picker, off the render thread, with its answer brought home through
    /// <see cref="AppState.Post"/> the same way the mod installer's is.
    /// </summary>
    private static void Browse(AppState state)
    {
        if (!FileDialog.IsSupported)
        {
            state.AddToast("No file dialog available; paste the path", ToastKind.Error);
            return;
        }

        _dialogOpen = true;
        _ = Task.Run(async () =>
        {
            try
            {
                string? picked = await FileDialog
                    .OpenAsync("The run's LiveSplit splits", LiveSplitRun.FilterDescription, LiveSplitRun.Extension)
                    .ConfigureAwait(false);

                state.Post(() =>
                {
                    _dialogOpen = false;
                    if (picked is null) return;

                    _splitsFile = picked;
                    ApplySplitsFile(state, state.Settings.Autosplit);
                });
            }
            catch (Exception ex)
            {
                state.Post(() =>
                {
                    _dialogOpen = false;
                    state.AddToast($"File dialog failed: {ex.Message}", ToastKind.Error);
                });
            }
        });
    }

    private static void DrawGameSection(AppState state, AutosplitSettings autosplit)
    {
        var game = state.Session.Game;
        if (!state.Connected || game == GameId.None)
        {
            Ui.Hint("Connect and start a game to choose what its run events do.");
            return;
        }

        var descriptors = state.AutosplitEvents;
        Ui.Heading($"{game.DisplayName()} run events");

        if (descriptors.Length == 0)
        {
            Ui.Hint(state.QwarkStale
                ? "The console's qwark is older than this client and has no autosplitter for this game. Re-upload qwark.sprx."
                : "qwark has no autosplitter for this game, so there is nothing to configure.");
            return;
        }

        var options = autosplit.For(game);
        var settings = state.Settings;

        // The subsplits the user picks on the left, the three "does this kind of event do
        // anything at all" masters on the right. Timing rows are on neither: they are not a
        // choice, and the hint under the table says so.
        if (ImGui.BeginTable("autosplit-events", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawSplitOptions(game, options, settings, descriptors);

            ImGui.TableNextColumn();
            DrawMasters(options, settings);

            ImGui.EndTable();
        }

        if (options.PlanetRoute && state.Autosplitter.PlanetDescriptor is not null
            && !AutosplitRoutes.Knows(game, state.Session.CurrentPlanet))
        {
            Ui.Warning($"{Path.GetFileName(AutosplitRoutes.FileFor(game))} has no names for planet "
                       + $"{state.Session.CurrentPlanet}, so entering it will not split.");
        }

        // Neither LiveSplit nor a splits file will name the split after this one, so there is
        // nothing for the route to compare and a planet event cannot split.
        if (options.PlanetRoute && state.LiveSplit.IsConnected
            && !state.LiveSplit.Answers(LiveSplitClient.GetUpcomingSplitName)
            && !state.Autosplitter.Runs.State.HasNames)
        {
            Ui.Warning("Planet route needs the split names: load your splits in LiveSplit or pick the .lss file.");
        }
    }

    /// <summary>The left column: one checkbox per SPLIT row, with the route indented under its own.</summary>
    private static void DrawSplitOptions(
        GameId game, AutosplitGameSettings options, Settings settings,
        IReadOnlyList<AutosplitEventDesc> descriptors)
    {
        ImGui.TextUnformatted("Split on");

        bool any = false;
        foreach (var desc in descriptors)
        {
            if (!desc.IsSplitOption) continue;
            any = true;

            bool on = options.EventEnabled(desc.Label, desc.EnabledByDefault);
            if (ImGui.Checkbox(desc.Label, ref on))
            {
                options.SetEvent(desc.Label, on);
                settings.Save();
            }

            if (Ui.Debug)
            {
                ImGui.SameLine();
                ImGui.TextColored(Ui.Grey, $"({desc.Code})");
            }

            if (!desc.PlanetRoute) continue;

            // The route is a property of the planet split, so it lives under it and means nothing
            // while the planet split itself is off.
            ImGui.Indent();
            ImGui.BeginDisabled(!on);
            bool route = options.PlanetRoute;
            if (ImGui.Checkbox("Use planet split route", ref route))
            {
                options.PlanetRoute = route;
                settings.Save();
            }

            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Only split when the planet you have just reached is the one the "
                                 + "next split is named after, from "
                                 + $"data/autosplit/{Path.GetFileName(AutosplitRoutes.FileFor(game))}.");
            }

            ImGui.Unindent();
        }

        if (!any) Ui.Hint("This game reports only timing events, so there is nothing to choose.");
    }

    /// <summary>
    /// The right column: what each kind of event is allowed to do to the timer. Three plain
    /// checkboxes under one label, because "Start" beside "Timer control" already says it.
    /// </summary>
    private static void DrawMasters(AutosplitGameSettings options, Settings settings)
    {
        ImGui.TextUnformatted("Timer control");

        bool start = options.Start;
        if (ImGui.Checkbox("Start", ref start))
        {
            options.Start = start;
            settings.Save();
        }

        bool split = options.Split;
        if (ImGui.Checkbox("Split", ref split))
        {
            options.Split = split;
            settings.Save();
        }

        bool reset = options.Reset;
        if (ImGui.Checkbox("Reset", ref reset))
        {
            options.Reset = reset;
            settings.Save();
        }
    }

    private static void DrawLog(AppState state)
    {
        var splitter = state.Autosplitter;

        // Short enough to clear the window's own status text in the bottom right corner, which
        // shares this line once a game with eight subsplits has filled the panel above it.
        ImGui.TextUnformatted("Run events");
        ImGui.SameLine();
        ImGui.TextColored(Ui.Grey, $"| {splitter.Received} received, {splitter.Acted} acted on, "
                                   + $"{splitter.Adjustments} adjusted");

        if (!Ui.Debug)
        {
            ImGui.SameLine();
            ImGui.TextColored(Ui.Grey, "| log in Settings");
            return;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear")) splitter.ClearLog();

        var entries = splitter.Log();
        if (entries.Length == 0)
        {
            Ui.Hint("Nothing yet. Events arrive from the console the moment it detects them.");
            return;
        }

        // The log fills whatever is left of the panel, newest first, and scrolls on its own.
        if (ImGui.BeginChild("##autosplit-log", new Vector2(-1, -1), ImGuiChildFlags.Borders)
            && ImGui.BeginTable("events", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Event", ImGuiTableColumnFlags.WidthFixed, 190);
            ImGui.TableSetupColumn("Action");
            ImGui.TableHeadersRow();

            for (int i = entries.Length - 1; i >= 0; i--)
            {
                var entry = entries[i];
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{entry.TimeMs / 1000.0:0.000}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Kind == AutosplitKind.None ? "-" : entry.Kind.ToString());

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Event);

                ImGui.TableNextColumn();
                ImGui.TextColored(entry.Acted ? Ui.Green : Ui.Grey, entry.Action);
            }

            ImGui.EndTable();
        }

        ImGui.EndChild();
    }
}
