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
    public static void Draw(AppState state)
    {
        var autosplit = state.Settings.Autosplit;

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
            _ when state.LiveSplit.TooOld => Ui.Red,
            _ => Ui.Grey,
        };

        // Not TextColored: the version LiveSplit reports is its own text and printf would read it.
        Ui.Text(colour, state.LiveSplit.StatusLine);

        // The endpoint is on the Settings panel: it is typed once, if ever, and the rest of this
        // panel is about the run. The phase and the two split names are wire detail — the coloured
        // line above already says whether LiveSplit is there — so they wait for debug information.
        if (state.LiveSplit.TooOld) Ui.Hint(LiveSplitModal.TooOldBody);
        else if (!state.LiveSplit.IsConnected) Ui.Hint($"Listening for LiveSplit at {autosplit.Host}:{autosplit.Port}...");
        else if (Ui.Debug) Ui.Hint(DescribeTimer(state.Autosplitter.View));

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
    /// earns its popup, not found or too old, even if that one has already been shown once.
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

    private static void DrawGameSection(AppState state, AutosplitSettings autosplit)
    {
        // The described game, so the rows and their checkboxes are still here between sessions:
        // a Deadlocked quit is part of a run, not the end of one.
        var game = state.DescribedGame;
        if (game == GameId.None)
        {
            Ui.Hint("Connect and start a game to choose what its run events do.");
            return;
        }

        var descriptors = state.AutosplitEvents;
        Ui.Heading("Settings");

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

        DrawRouteWarning(state, game, options);
    }

    /// <summary>
    /// The one thing that can silently go wrong with a route: the file is out of step with the
    /// planet list the console described, so a planet nobody wrote a line for would never split.
    /// It is said about the destination, because that is what the route compares, and never about
    /// a <c>null</c> line — index 0 is the main menu in every game and the rest are indices the
    /// game never enters, so having no names for those is the file being right.
    /// </summary>
    private static void DrawRouteWarning(AppState state, GameId game, AutosplitGameSettings options)
    {
        // Only while a game is running: the planet the session reports at the XMB is nobody's.
        if (!state.Ingame || !options.PlanetRoute || state.Autosplitter.PlanetDescriptor is null) return;

        int planet = state.Session.CurrentPlanet;
        if (AutosplitRoutes.Knows(game, planet) || AutosplitRoutes.Unused(game, planet)) return;

        string name = planet < state.Planets.Length ? state.Planets[planet] : string.Empty;
        string which = string.IsNullOrEmpty(name) ? $"planet {planet}" : $"{name} (planet {planet})";
        Ui.Warning($"{Path.GetFileName(AutosplitRoutes.FileFor(game))} has no names for {which}, "
                   + "so a load into it will not split.");
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
    /// The right column: what each kind of event is allowed to do to the timer. Four plain
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

        bool pause = options.Pause;
        if (ImGui.Checkbox("Pause", ref pause))
        {
            options.Pause = pause;
            settings.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Stop game time while the game is away, and put the time the run is "
                             + "charged for it back on when it returns. Only Deadlocked's quit to "
                             + "the XMB reports one.");
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
