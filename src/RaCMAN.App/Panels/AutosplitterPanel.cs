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
    private static string _host = string.Empty;
    private static int _port;
    private static bool _initialised;

    /// <summary>Drops the host and port drafts, so a settings import or a restart is picked up.</summary>
    public static void Reset() => _initialised = false;

    public static void Draw(AppState state)
    {
        var settings = state.Settings;
        var autosplit = settings.Autosplit;

        if (!_initialised)
        {
            _host = autosplit.Host;
            _port = autosplit.Port;
            _initialised = true;
        }

        Ui.Heading("Autosplitter");
        Ui.Hint("The console reports what happened in the run and keeps no timer. This client decides what splits.");

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
            if (enabled) state.LiveSplit.Start(autosplit.Host, autosplit.Port);
            else state.LiveSplit.Stop();
        }

        ImGui.SetNextItemWidth(180);
        ImGui.InputText("LiveSplit host", ref _host, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        if (ImGui.InputInt("Port", ref _port)) _port = Math.Clamp(_port, 1, 65535);

        ImGui.SameLine();
        bool changed = !string.Equals(_host.Trim(), autosplit.Host, StringComparison.OrdinalIgnoreCase)
                       || _port != autosplit.Port;
        if (ImGui.Button(changed ? "Apply" : "Reconnect"))
        {
            autosplit.Host = _host.Trim();
            autosplit.Port = _port;
            settings.Save();
            if (autosplit.Enabled)
            {
                state.LiveSplit.Stop();
                state.LiveSplit.Start(autosplit.Host, autosplit.Port);
            }
        }

        var colour = state.LiveSplit.Status switch
        {
            LiveSplitStatus.Connected => Ui.Green,
            LiveSplitStatus.Connecting => Ui.Yellow,
            _ => Ui.Grey,
        };

        ImGui.TextColored(colour, state.LiveSplit.StatusLine);

        if (!state.LiveSplit.IsConnected) Ui.Hint(LiveSplitClient.ServerHint);
        else Ui.Hint(DescribeTimer(state.Autosplitter.View));

        ImGui.BeginDisabled(!state.LiveSplit.IsConnected);
        if (ImGui.Button("Split now")) state.Autosplitter.SendManual(LiveSplitClient.Split);
        ImGui.SameLine();
        if (ImGui.Button("Reset")) state.Autosplitter.SendManual(LiveSplitClient.Reset);
        ImGui.EndDisabled();
        ImGui.SameLine();
        Ui.Hint("Test the connection without waiting for the game.");
    }

    private static string DescribeTimer(LiveSplitView view)
    {
        string current = view.CurrentSplit ?? "(none)";
        string upcoming = view.UpcomingSplit ?? "(none)";
        return $"Timer {view.Phase} | this split \"{current}\" | next split \"{upcoming}\"";
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

        // One line, ASCII only: the panel has to fit and the font atlas has no em dash.
        Ui.Hint("The old autosplitters' game-time normalisation is applied automatically.");

        if (options.PlanetRoute && state.Autosplitter.PlanetDescriptor is not null
            && !AutosplitRoutes.Knows(game, state.Session.CurrentPlanet))
        {
            Ui.Warning($"{Path.GetFileName(AutosplitRoutes.FileFor(game))} has no names for planet "
                       + $"{state.Session.CurrentPlanet}, so entering it will not split.");
        }

        if (options.PlanetRoute && state.LiveSplit.IsConnected
            && !state.LiveSplit.Answers(LiveSplitClient.GetUpcomingSplitName))
        {
            Ui.Warning($"This LiveSplit does not answer {LiveSplitClient.GetUpcomingSplitName}, which is the "
                       + "name the route compares, so planet events cannot split while the route is on.");
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

    /// <summary>The right column: what each kind of event is allowed to do to the timer.</summary>
    private static void DrawMasters(AutosplitGameSettings options, Settings settings)
    {
        ImGui.TextUnformatted("Timer control");

        bool start = options.Start;
        if (Master("Start", "START events start the timer", ref start))
        {
            options.Start = start;
            settings.Save();
        }

        bool split = options.Split;
        if (Master("Split", "SPLIT events split", ref split))
        {
            options.Split = split;
            settings.Save();
        }

        bool reset = options.Reset;
        if (Master("Reset", "RESET events reset the timer", ref reset))
        {
            options.Reset = reset;
            settings.Save();
        }
    }

    /// <summary>One master and what it means, on one line so eight subsplits still fit beside it.</summary>
    private static bool Master(string label, string meaning, ref bool value)
    {
        bool changed = ImGui.Checkbox(label, ref value);
        ImGui.SameLine();
        ImGui.TextColored(Ui.Grey, meaning);
        return changed;
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
