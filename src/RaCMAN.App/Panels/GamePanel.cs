using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

public static class GamePanel
{
    /// <summary>Half-typed VALUE edits, kept only while the input has focus.</summary>
    private static readonly Dictionary<byte, string> Drafts = new();

    /// <summary>
    /// The last value this client sent per feature. Since revision 1.1 every VALUE, ENUM and
    /// COLOR names the readout that mirrors it, so this is only the fallback for readout 0xFF.
    /// </summary>
    private static readonly Dictionary<byte, uint> LastSet = new();

    public static void Reset()
    {
        Drafts.Clear();
        LastSet.Clear();
    }

    /// <summary>The feature's current value: its readout when it names one, else what we last sent.</summary>
    private static uint CurrentValue(AppState state, Feature feature) =>
        feature.MirrorReadout is { } mirror
            ? state.Session.ReadoutAt(mirror) ?? 0u
            : LastSet.GetValueOrDefault(feature.Id);

    public static void Draw(AppState state)
    {
        Ui.Heading("Game");

        var describe = state.Describe;
        if (describe.Features.Length == 0)
        {
            Ui.Hint(state.Connected
                ? "No descriptors. qwark returns an empty DESCRIBE until a supported game is running."
                : "Connect to see the game's descriptors.");
            return;
        }

        bool enabled = state.Ingame;
        if (!enabled) ImGui.TextColored(Ui.Yellow, $"Controls are disabled outside INGAME (state is {state.Session.State}).");

        ImGui.BeginDisabled(!enabled);

        DrawValues(state, describe);
        ImGui.Spacing();
        DrawGroupTabs(state, describe);

        ImGui.EndDisabled();
    }

    // ---------------------------------------------------------------- values

    /// <summary>
    /// The player-value table: every VALUE feature is an inline-editable row, and every readout
    /// that no feature drives is a read-only row. Readouts mirrored by an ENUM or COLOR are shown
    /// by that control in its group, not here, so they are not duplicated.
    /// </summary>
    private static void DrawValues(AppState state, DescribeResult describe)
    {
        var session = state.Session;

        // Which readouts a feature already represents, and by which kind.
        var mirroredByValue = new Dictionary<int, Feature>();
        var mirroredByOther = new HashSet<int>();
        foreach (var f in describe.Features)
        {
            if (f.MirrorReadout is not { } r) continue;
            if (f.Kind == FeatureKind.Value) mirroredByValue[r] = f;
            else if (f.Kind is FeatureKind.Enum or FeatureKind.Color) mirroredByOther.Add(r);
        }

        if (!ImGui.BeginTable("player-values", 2,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("Value");
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch);
        // No header row: the panel heading already says what this is, and it reads as a stat block.

        // Readouts in order: an editable VALUE where one drives it, otherwise a read-only reading.
        for (int i = 0; i < describe.Readouts.Length && i < session.Readout.Length; i++)
        {
            if (mirroredByOther.Contains(i)) continue;   // shown by its ENUM/COLOR control below

            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            if (mirroredByValue.TryGetValue(i, out var feature))
            {
                ImGui.TextUnformatted(feature.Label);
                ImGui.TableNextColumn();
                DrawValueEditor(state, describe, feature);
            }
            else
            {
                ImGui.TextUnformatted(describe.Readouts[i]);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(session.Readout[i].ToString());
            }
        }

        // VALUE features that mirror no readout still belong in the table.
        foreach (var feature in describe.Features)
        {
            if (feature.Kind != FeatureKind.Value || feature.MirrorReadout is not null) continue;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(feature.Label);
            ImGui.TableNextColumn();
            DrawValueEditor(state, describe, feature);
        }

        ImGui.EndTable();
    }

    private static void DrawValueEditor(AppState state, DescribeResult describe, Feature feature)
    {
        uint live = CurrentValue(state, feature);

        ImGui.SetNextItemWidth(-1);   // fill the stretch column
        ImGui.PushID(feature.Id);
        bool sent = Ui.NumberOnEnter("##v", live, Drafts, feature.Id, out long typed);
        ImGui.PopID();

        if (feature.Max > feature.Min && ImGui.IsItemHovered())
            ImGui.SetTooltip($"{feature.Min}..{feature.Max}" + (feature.MirrorReadout is null ? "  (no live readout)" : ""));

        if (!sent) return;

        uint value = (uint)Math.Clamp(typed, 0, uint.MaxValue);
        if (feature.Max > feature.Min) value = Math.Clamp(value, feature.Min, feature.Max);

        byte id = feature.Id;
        LastSet[id] = value;
        state.Run(() => state.Client.FeatureSetAsync(id, value));
    }

    // ---------------------------------------------------------------- groups

    private static void DrawGroupTabs(AppState state, DescribeResult describe)
    {
        int groupCount = Math.Max(1, describe.Groups.Length);

        // A group is worth a tab only if it has something other than the VALUEs shown up top.
        bool HasContent(byte g) =>
            describe.Features.Any(f => f.Group == g && f.Kind != FeatureKind.Value);

        if (!ImGui.BeginTabBar("game-groups", ImGuiTabBarFlags.FittingPolicyScroll | ImGuiTabBarFlags.TabListPopupButton))
            return;

        for (byte group = 0; group < groupCount; group++)
        {
            if (!HasContent(group)) continue;
            if (!ImGui.BeginTabItem(describe.GroupName(group))) continue;

            ImGui.PushID(group);
            DrawGroupBody(state, describe, group);
            ImGui.PopID();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private static void DrawGroupBody(AppState state, DescribeResult describe, byte group)
    {
        var features = describe.Features.Where(f => f.Group == group).ToArray();

        var toggles = features.Where(f => f.Kind == FeatureKind.Toggle).ToArray();
        var actions = features.Where(f => f.Kind == FeatureKind.Action).ToArray();
        var choices = features.Where(f => f.Kind is FeatureKind.Enum or FeatureKind.Color).ToArray();

        ImGui.Spacing();
        DrawToggleGrid(state, toggles);
        if (toggles.Length > 0 && (actions.Length > 0 || choices.Length > 0)) ImGui.Spacing();
        DrawActionGrid(state, actions);
        if (choices.Length > 0) ImGui.Spacing();
        foreach (var feature in choices) DrawChoice(state, describe, feature);
    }

    /// <summary>Toggles two to a row, each with a compact "auto on boot" box, to reclaim vertical space.</summary>
    private static void DrawToggleGrid(AppState state, Feature[] toggles)
    {
        if (toggles.Length == 0) return;

        int columns = toggles.Length > 1 ? 2 : 1;
        if (!ImGui.BeginTable("toggles", columns, ImGuiTableFlags.SizingStretchSame)) return;

        var session = state.Session;
        foreach (var feature in toggles)
        {
            ImGui.TableNextColumn();
            ImGui.PushID(feature.Id);

            ulong bit = 1UL << feature.Id;
            bool on = (session.ToggleState & bit) != 0;
            if (ImGui.Checkbox(feature.Label, ref on))
            {
                byte id = feature.Id;
                uint value = on ? 1u : 0u;
                state.Run(() => state.Client.FeatureSetAsync(id, value));
            }
            if (feature.WritesCode && ImGui.IsItemHovered()) ImGui.SetTooltip("Patches game code");

            ImGui.SameLine();
            bool auto = (session.ToggleAuto & bit) != 0;
            if (ImGui.Checkbox("A", ref auto))
            {
                byte id = feature.Id;
                bool wanted = auto;
                state.Run(() => state.Client.FeatureSetAutoAsync(id, wanted));
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Auto-apply on game boot");

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    /// <summary>Actions as a grid of equal-width buttons.</summary>
    private static void DrawActionGrid(AppState state, Feature[] actions)
    {
        if (actions.Length == 0) return;

        int columns = actions.Length > 1 ? 2 : 1;
        if (!ImGui.BeginTable("actions", columns, ImGuiTableFlags.SizingStretchSame)) return;

        foreach (var feature in actions)
        {
            ImGui.TableNextColumn();
            ImGui.PushID(feature.Id);
            if (ImGui.Button(feature.Label, new Vector2(-1, 0)))
            {
                byte id = feature.Id;
                state.Run(() => state.Client.FeatureTriggerAsync(id));
            }
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static void DrawChoice(AppState state, DescribeResult describe, Feature feature)
    {
        ImGui.PushID(feature.Id);

        if (feature.Kind == FeatureKind.Enum)
        {
            var options = state.EnumOptions.TryGetValue(feature.Id, out var loaded) ? loaded : Array.Empty<string>();
            if (options.Length == 0)
            {
                ImGui.TextColored(Ui.Grey, $"{feature.Label}: waiting for FEATURE_OPTIONS");
                ImGui.PopID();
                return;
            }

            uint live = CurrentValue(state, feature);
            int current = live < (uint)options.Length ? (int)live : -1;

            ImGui.SetNextItemWidth(240);
            if (ImGui.Combo(feature.Label, ref current, options, options.Length) && current >= 0)
            {
                byte id = feature.Id;
                uint value = (uint)current;
                LastSet[id] = value;
                state.Run(() => state.Client.FeatureSetAsync(id, value));
            }

            if (current < 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(Ui.Yellow, $"readout says {live}");
            }
        }
        else // Color
        {
            uint packed = CurrentValue(state, feature);
            var colour = new Vector3(
                ((packed >> 16) & 0xFF) / 255f,
                ((packed >> 8) & 0xFF) / 255f,
                (packed & 0xFF) / 255f);

            ImGui.SetNextItemWidth(240);
            if (ImGui.ColorEdit3(feature.Label, ref colour))
            {
                uint value = ((uint)(colour.X * 255) << 16) | ((uint)(colour.Y * 255) << 8) | (uint)(colour.Z * 255);
                byte id = feature.Id;
                LastSet[id] = value;
                state.Run(() => state.Client.FeatureSetAsync(id, value));
            }
        }

        ImGui.PopID();
    }
}
