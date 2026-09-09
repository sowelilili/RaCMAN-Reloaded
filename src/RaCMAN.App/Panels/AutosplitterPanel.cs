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

        DrawConnection(state, autosplit);
        ImGui.Separator();
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

        // Two to a row: a game can describe eight of these and the panel still has to fit.
        if (ImGui.BeginTable("autosplit-events", 2, ImGuiTableFlags.SizingStretchSame))
        {
            foreach (var desc in descriptors)
            {
                ImGui.TableNextColumn();
                bool on = options.EventEnabled(desc.Label, desc.EnabledByDefault);
                if (ImGui.Checkbox(desc.Label, ref on))
                {
                    options.SetEvent(desc.Label, on);
                    settings.Save();
                }

                if (!Ui.Debug) continue;
                ImGui.SameLine();
                ImGui.TextColored(Ui.Grey, $"({desc.Code})");
            }

            ImGui.EndTable();
        }

        var planetDesc = state.Autosplitter.PlanetDescriptor;
        ImGui.BeginDisabled(planetDesc is null);
        bool route = options.PlanetRoute;
        if (ImGui.Checkbox("Use planet split route", ref route))
        {
            options.PlanetRoute = route;
            settings.Save();
        }

        ImGui.EndDisabled();

        if (planetDesc is null)
        {
            Ui.Hint("This game reports no \"planet entered\" event, so there is no route to follow.");
        }
        else
        {
            ImGui.SameLine();
            Ui.Hint($"(names from data/autosplit/{Path.GetFileName(AutosplitRoutes.FileFor(game))})");

            ImGui.BeginDisabled(!route);
            int names = options.NamesAreDestination ? 1 : 0;
            ImGui.TextUnformatted("Split names are:");
            ImGui.SameLine();
            bool picked = ImGui.RadioButton("the planet you are on", ref names, 0);
            ImGui.SameLine();
            picked |= ImGui.RadioButton("the planet you are travelling to", ref names, 1);
            if (picked)
            {
                options.NamesAreDestination = names == 1;
                settings.Save();
            }

            ImGui.EndDisabled();

            if (route && !AutosplitRoutes.Knows(game, state.Session.CurrentPlanet))
            {
                Ui.Warning($"{Path.GetFileName(AutosplitRoutes.FileFor(game))} has no names for planet "
                           + $"{state.Session.CurrentPlanet}, so entering it will not split.");
            }
        }

        bool neverReset = options.NeverReset;
        if (ImGui.Checkbox("Never reset (e.g. All Exterminator Cards)", ref neverReset))
        {
            options.NeverReset = neverReset;
            settings.Save();
        }
    }

    private static void DrawLog(AppState state)
    {
        var entries = state.Autosplitter.Log();

        ImGui.TextUnformatted("Run events");
        ImGui.SameLine();
        ImGui.TextColored(Ui.Grey, $"| {state.Autosplitter.Received} received, {state.Autosplitter.Acted} acted on");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear")) state.Autosplitter.ClearLog();

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
            ImGui.TableSetupColumn("Tick", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Event", ImGuiTableColumnFlags.WidthFixed, 190);
            ImGui.TableSetupColumn("Action");
            ImGui.TableHeadersRow();

            for (int i = entries.Length - 1; i >= 0; i--)
            {
                var entry = entries[i];
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Tick.ToString());

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
