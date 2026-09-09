using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The prompt section 4.1 asks for: when flags.PREVIOUS_PENDING is set, list what the last
/// same-game session left behind and let the user re-apply categories. Default is nothing.
/// </summary>
public static class PreviousSessionModal
{
    private const string Title = "Previous session";

    private static bool _toggles;
    private static bool _mods;
    private static bool _freezes;
    private static bool _patches;
    private static bool _open;

    public static void Draw(AppState state)
    {
        if (state.PreviousModalRequested && !_open)
        {
            _open = true;
            _toggles = _mods = _freezes = _patches = false;
            ImGui.OpenPopup(Title);
        }

        if (!_open) return;

        var centre = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(520, 0), ImGuiCond.Appearing);

        bool open = true;
        if (!ImGui.BeginPopupModal(Title, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) Close(state);
            return;
        }

        var previous = state.Previous ?? PreviousSession.Empty;

        ImGui.TextWrapped("The game rebooted into the same title. These were active before and were not flagged to re-apply themselves.");
        ImGui.Spacing();

        var describe = state.Describe;

        // On a platform that refuses code patches the mods and the client patches are still worth
        // listing — they say what the last session had — but nothing can put them back, so their
        // boxes are dead and carry the same reason the panels give.
        bool noCode = state.CodePatchesUnsupported;

        DrawCategory("Toggles", ref _toggles, () =>
        {
            for (int i = 0; i < 64; i++)
            {
                if ((previous.Toggles & (1UL << i)) == 0) continue;
                var feature = describe.Features.FirstOrDefault(f => f.Id == i);
                ImGui.BulletText(feature?.Label ?? $"feature {i}");
            }
        }, previous.Toggles != 0);

        DrawCategory("Mods", ref _mods, () =>
        {
            for (int i = 0; i < 32; i++)
            {
                if ((previous.Mods & (1U << i)) == 0) continue;
                var mod = state.ConsoleMods.FirstOrDefault(m => m.Index == i);
                ImGui.BulletText(mod?.Name ?? $"mod {i}");
            }
        }, previous.Mods != 0, noCode);

        DrawCategory("Freezes", ref _freezes, () =>
        {
            foreach (var freeze in previous.Freezes)
            {
                ImGui.BulletText($"0x{freeze.Address:X8} ({freeze.Size} bytes) = {freeze.Value}");
            }
        }, previous.Freezes.Length > 0);

        DrawCategory("Client patches", ref _patches, () =>
        {
            foreach (var patch in previous.Patches)
            {
                ImGui.BulletText($"0x{patch.FirstAddress:X8}, {patch.WordCount} words");
            }
        }, previous.Patches.Length > 0, noCode);

        if (noCode) _mods = _patches = false;

        ImGui.Spacing();
        ImGui.Separator();

        var categories = (_toggles ? PreviousCategories.Toggles : 0)
                         | (_mods ? PreviousCategories.Mods : 0)
                         | (_freezes ? PreviousCategories.Freezes : 0)
                         | (_patches ? PreviousCategories.Patches : 0);

        void Reapply(PreviousCategories wanted)
        {
            state.Run(async () =>
            {
                await state.Client.PreviousReapplyAsync(wanted);
                state.Post(() =>
                {
                    state.RefreshFreezes();
                    state.RefreshPatches();
                    state.RefreshMods();
                });
            }, "Previous session re-applied");
            Close(state);
            ImGui.CloseCurrentPopup();
        }

        ImGui.BeginDisabled(categories == PreviousCategories.None);
        if (ImGui.Button("Reapply selected")) Reapply(categories);
        ImGui.EndDisabled();

        ImGui.SameLine();
        const PreviousCategories all = PreviousCategories.Toggles | PreviousCategories.Mods
                                       | PreviousCategories.Freezes | PreviousCategories.Patches;

        // "All" is all of what this platform can do: asking for the mods and the patches here
        // would only earn an UNSUPPORTED for the whole request.
        var everything = noCode ? all & ~(PreviousCategories.Mods | PreviousCategories.Patches) : all;
        if (ImGui.Button("Reapply all")) Reapply(everything);

        ImGui.SameLine();
        if (ImGui.Button("Dismiss"))
        {
            state.Run(() => state.Client.PreviousDismissAsync(), "Previous session dismissed");
            Close(state);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        Ui.Hint("Nothing is selected by default.");

        ImGui.EndPopup();

        if (!open) Close(state);
    }

    private static void DrawCategory(string label, ref bool selected, Action drawItems, bool any,
        bool refused = false)
    {
        ImGui.BeginDisabled(!any || refused);
        ImGui.Checkbox(label, ref selected);
        ImGui.EndDisabled();

        if (!any)
        {
            ImGui.SameLine();
            ImGui.TextColored(Ui.Grey, "(none)");
            return;
        }

        if (refused)
        {
            ImGui.SameLine();
            Ui.Text(Ui.Yellow, Ui.NoCodePatches);
        }

        ImGui.Indent();
        drawItems();
        ImGui.Unindent();
    }

    private static void Close(AppState state)
    {
        _open = false;
        state.PreviousModalRequested = false;
    }
}
