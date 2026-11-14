using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The question a game boot asks under the console's Apply toggles on game boot = Ask (qwark
/// revision 1.33, SessionInfo flags3 bit7): the game booted with none of its on-boot toggles and
/// mods applied, and this lists them and asks. No is the easy answer, as in the previous-session
/// prompt beside it: Enter, Esc and closing the window all leave them off.
/// </summary>
public static class BootAskModal
{
    public const string Title = "Apply on-boot toggles?";

    private static bool _open;

    /// <summary>What the modal lists: the running game's on-boot toggles and mods, by name.</summary>
    public static (IReadOnlyList<string> Toggles, IReadOnlyList<string> Mods) Items(AppState state)
    {
        var session = state.Session;
        var toggles = new List<string>();
        for (int i = 0; i < 64; i++)
        {
            if ((session.ToggleAuto & (1UL << i)) == 0) continue;
            toggles.Add(state.Describe.Features.FirstOrDefault(f => f.Id == i)?.Label ?? $"feature {i}");
        }

        var mods = new List<string>();
        for (int i = 0; i < 32; i++)
        {
            if ((session.ModAuto & (1U << i)) == 0) continue;
            mods.Add(state.ConsoleMods.FirstOrDefault(m => m.Index == i)?.Name ?? $"mod {i}");
        }

        return (toggles, mods);
    }

    public static void Draw(AppState state)
    {
        // One modal at a time: the previous-session prompt of the same boot goes first.
        if (state.BootAskRequested && !_open && !state.PreviousModalRequested)
        {
            _open = true;
            ImGui.OpenPopup(Title);
        }

        if (!_open) return;

        if (!state.BootAskRequested)
        {
            // Answered elsewhere (another client, the game gone): nothing left to ask.
            _open = false;
            return;
        }

        var centre = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(480, 0), ImGuiCond.Appearing);

        bool open = true;
        if (!ImGui.BeginPopupModal(Title, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) Answer(state, apply: false);
            return;
        }

        var (toggles, mods) = Items(state);
        Ui.TextWrapped($"{state.Session.GameName} booted without its on-boot toggles and mods, because your "
                       + "console is set to ask (Settings, Console, Apply toggles on game boot).");
        ImGui.Spacing();

        if (toggles.Count > 0)
        {
            ImGui.TextUnformatted("Toggles");
            ImGui.Indent();
            foreach (var label in toggles) Ui.BulletText(label);
            ImGui.Unindent();
        }

        if (mods.Count > 0)
        {
            ImGui.TextUnformatted("Mods");
            ImGui.Indent();
            foreach (var name in mods) Ui.BulletText(name);
            ImGui.Unindent();
        }

        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.Button("Apply", new Vector2(120, 0)))
        {
            Answer(state, apply: true);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        bool leave = ImGui.Button("Leave off", new Vector2(120, 0));

        // Enter on a dialog that has just opened answers no.
        ImGui.SetItemDefaultFocus();
        if (leave || !open || ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            Answer(state, apply: false);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static void Answer(AppState state, bool apply)
    {
        _open = false;
        state.AnswerBootAsk(apply);
    }
}
