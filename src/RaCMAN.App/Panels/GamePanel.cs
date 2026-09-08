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

        // The tab layout is client-owned: qwark's group is the default, gamelayout.json overrides it.
        string title = state.Session.TitleId ?? string.Empty;
        var sections = new Dictionary<string, List<Feature>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var feature in describe.Features)
        {
            string section = GameLayout.SectionFor(title, feature, describe);
            if (!sections.TryGetValue(section, out var list))
            {
                list = new List<Feature>();
                sections[section] = list;
                order.Add(section);
            }
            list.Add(feature);
        }

        ImGui.BeginDisabled(!enabled);

        var topValues = sections.GetValueOrDefault(GameLayout.ValuesSection) ?? new List<Feature>();
        DrawTopValues(state, describe, topValues);

        ImGui.Spacing();

        var used = order.Where(s => s != GameLayout.ValuesSection);
        var tabs = GameLayout.TabOrder(title, used);
        if (tabs.Count > 0 &&
            ImGui.BeginTabBar("game-groups", ImGuiTabBarFlags.FittingPolicyScroll | ImGuiTabBarFlags.TabListPopupButton))
        {
            foreach (var tab in tabs)
            {
                if (!sections.TryGetValue(tab, out var features) || features.Count == 0) continue;
                if (!ImGui.BeginTabItem(tab)) continue;

                ImGui.PushID(tab);
                DrawSectionBody(state, describe, features);
                ImGui.PopID();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.EndDisabled();
    }

    // ---------------------------------------------------------------- values

    /// <summary>
    /// The top table: the value features that live here (VALUE by default), plus every readout that
    /// no feature drives, shown read-only. A readout a moved value drives is shown by that value in
    /// its tab, not duplicated here.
    /// </summary>
    private static void DrawTopValues(AppState state, DescribeResult describe, List<Feature> here)
    {
        var session = state.Session;

        // Map each readout to the feature that mirrors it, so we know which are driven and by what.
        var mirror = new Dictionary<int, Feature>();
        foreach (var f in describe.Features)
        {
            if (f.MirrorReadout is { } r) mirror[r] = f;
        }

        var topIds = here.Where(f => f.Kind == FeatureKind.Value).Select(f => f.Id).ToHashSet();

        if (!ImGui.BeginTable("player-values", 2,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("Value");
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch);

        for (int i = 0; i < describe.Readouts.Length && i < session.Readout.Length; i++)
        {
            if (mirror.TryGetValue(i, out var feature))
            {
                // A VALUE that lives in this table is editable here; any other mirror (an ENUM/COLOR,
                // or a value moved to a tab) shows itself elsewhere, so skip the row.
                if (feature.Kind == FeatureKind.Value && topIds.Contains(feature.Id))
                    ValueRow(state, describe, feature);
                continue;
            }

            // No feature drives this readout: a plain read-only reading.
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(describe.Readouts[i]);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(session.Readout[i].ToString());
        }

        // Value features that mirror no readout still belong here.
        foreach (var feature in here)
        {
            if (feature.Kind == FeatureKind.Value && feature.MirrorReadout is null)
                ValueRow(state, describe, feature);
        }

        ImGui.EndTable();
    }

    private static void ValueRow(AppState state, DescribeResult describe, Feature feature)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(feature.Label);
        ImGui.TableNextColumn();
        DrawValueEditor(state, describe, feature);
    }

    private static void DrawValueEditor(AppState state, DescribeResult describe, Feature feature)
    {
        uint live = CurrentValue(state, feature);

        ImGui.SetNextItemWidth(-1);
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

    // ---------------------------------------------------------------- a tab

    private static void DrawSectionBody(AppState state, DescribeResult describe, List<Feature> features)
    {
        var values = features.Where(f => f.Kind == FeatureKind.Value).ToArray();
        var toggles = features.Where(f => f.Kind == FeatureKind.Toggle).ToArray();
        var actions = features.Where(f => f.Kind == FeatureKind.Action).ToArray();
        var choices = features.Where(f => f.Kind is FeatureKind.Enum or FeatureKind.Color).ToArray();

        ImGui.Spacing();

        if (values.Length > 0)
        {
            if (ImGui.BeginTable("tab-values", 2,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("Value");
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch);
                foreach (var feature in values) ValueRow(state, describe, feature);
                ImGui.EndTable();
            }
            if (toggles.Length > 0 || actions.Length > 0 || choices.Length > 0) ImGui.Spacing();
        }

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

            // Right-align the auto box inside the cell so it is never clipped by the column edge,
            // whatever the label length. Falls back to sitting right after a very long label.
            ImGui.SameLine();
            float autoWidth = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize("A").X;
            float cellRight = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
            ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), cellRight - autoWidth));

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
