using ImGuiNET;

namespace RaCMAN.App.Panels;

/// <summary>
/// Local PC preferences only: how this client looks and where it keeps its files. Anything the
/// console owns (auto-reconnect aside, which is a client habit) stays on the Connection panel.
/// </summary>
public static class SettingsPanel
{
    public static void Draw(AppState state)
    {
        var settings = state.Settings;

        Ui.Heading("Settings");

        ImGui.TextUnformatted("Theme");
        Ui.Hint("Applies straight away and is remembered.");
        ImGui.Spacing();

        bool light = settings.LightTheme;
        if (ImGui.RadioButton("Light", light) && !light) SetTheme(state, "light");
        ImGui.SameLine();
        if (ImGui.RadioButton("Dark", !light) && light) SetTheme(state, "dark");

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Debug");

        bool debug = settings.DebugInfo;
        if (ImGui.Checkbox("Show debug information", ref debug))
        {
            settings.DebugInfo = debug;
            settings.Save();
            state.ThemeDirty = true;
        }

        Ui.HintWrapped("Shows the wire-level detail: qwark and protocol versions, the reboot and tick "
                       + "counters, request names in error messages, frame rate, raw readouts and internal addresses.");

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Layout");

        if (ImGui.Button("Reload gamelayout.json"))
        {
            GameLayout.Invalidate();

            // Touching the layout re-reads the file now, so a broken edit is reported here
            // instead of on the next visit to the Game page.
            _ = GameLayout.SideSections;
            state.AddToast(
                GameLayout.Problems.Count == 0 ? "Game layout reloaded" : "Game layout reloaded with problems",
                GameLayout.Problems.Count == 0 ? ToastKind.Success : ToastKind.Error);
        }

        foreach (var problem in GameLayout.Problems) ImGui.TextColored(Ui.Yellow, problem);
        Ui.Hint(GameLayout.DefaultPath);

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Files");

        Path("Settings file", string.IsNullOrEmpty(settings.Path) ? Settings.DefaultPath : settings.Path);
        Path("Mods folder", state.Mods.RootPath);
        Path("Save files folder", state.SaveFiles.RootPath);
    }

    private static void SetTheme(AppState state, string theme)
    {
        state.Settings.Theme = theme;
        state.Settings.Save();
        state.ThemeDirty = true;
    }

    private static void Path(string label, string value)
    {
        ImGui.TextUnformatted(label);
        Ui.Hint(value);
    }
}
