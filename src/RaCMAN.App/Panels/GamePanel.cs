using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The Game page and its sub-pages. The page itself is the everyday controls: the player-value
/// table on top, then the remaining sections stacked and always visible. The sections the layout
/// marks as "side" (Collectables, Cosmetics, Debug by default) each get a sub-page, reached from
/// the indented entries under Game in the side nav.
/// </summary>
public static class GamePanel
{
    /// <summary>Half-typed VALUE edits, kept only while the input has focus.</summary>
    private static readonly Dictionary<byte, string> Drafts = new();

    /// <summary>
    /// The last value this client sent per feature. Since revision 1.1 every VALUE, ENUM and
    /// COLOR names the readout that mirrors it, so this is only the fallback for readout 0xFF.
    /// </summary>
    private static readonly Dictionary<byte, uint> LastSet = new();

    /// <summary>The side section being shown, or null for the Game page itself.</summary>
    public static string? SubPage { get; set; }

    /// <summary>A sub-page to open once the game's descriptors arrive (the --game-section flag).</summary>
    public static string? RequestedSubPage { get; set; }

    public static void Reset()
    {
        Drafts.Clear();
        LastSet.Clear();
        SubPage = null;
    }

    /// <summary>The feature's current value: its readout when it names one, else what we last sent.</summary>
    private static uint CurrentValue(AppState state, Feature feature) =>
        feature.MirrorReadout is { } mirror
            ? state.Session.ReadoutAt(mirror) ?? 0u
            : LastSet.GetValueOrDefault(feature.Id);

    /// <summary>Side sections that have at least one feature for the running game, in layout order. Drives the nav.</summary>
    public static IReadOnlyList<string> SideSectionsWithContent(AppState state)
    {
        var describe = state.Describe;
        if (describe.Features.Length == 0) return Array.Empty<string>();

        var sections = Assign(state.Session.TitleId ?? string.Empty, describe, out _);
        return GameLayout.SideSections.Where(s => sections.ContainsKey(s)).ToArray();
    }

    /// <summary>Puts every feature in its section (client layout first, qwark group as the default).</summary>
    private static Dictionary<string, List<Feature>> Assign(string title, DescribeResult describe, out List<string> firstSeen)
    {
        var sections = new Dictionary<string, List<Feature>>(StringComparer.Ordinal);
        firstSeen = new List<string>();
        foreach (var feature in describe.Features)
        {
            string section = GameLayout.SectionFor(title, feature, describe);
            if (!sections.TryGetValue(section, out var list))
            {
                list = new List<Feature>();
                sections[section] = list;
                firstSeen.Add(section);
            }
            list.Add(feature);
        }
        return sections;
    }

    public static void Draw(AppState state)
    {
        var describe = state.Describe;
        string title = state.Session.TitleId ?? string.Empty;
        var order = new List<string>();
        var sections = describe.Features.Length > 0 ? Assign(title, describe, out order) : null;

        // A sub-page that the running game has nothing for (the game changed) falls back to the page.
        if (SubPage is not null && (sections is null || !sections.ContainsKey(SubPage))) SubPage = null;
        if (SubPage is null && RequestedSubPage is { } wanted && sections is not null && sections.ContainsKey(wanted))
        {
            SubPage = wanted;
            RequestedSubPage = null;
        }

        Ui.Heading(SubPage is null ? "Game" : $"Game / {SubPage}");

        if (sections is null)
        {
            Ui.Hint(state.Connected
                ? "No descriptors. qwark returns an empty DESCRIBE until a supported game is running."
                : "Connect to see the game's descriptors.");
            return;
        }

        bool enabled = state.Ingame;
        if (!enabled) ImGui.TextColored(Ui.Yellow, $"Controls are disabled outside INGAME (state is {state.Session.State}).");

        ImGui.BeginDisabled(!enabled);

        if (SubPage is { } side)
        {
            ImGui.PushID(side);
            DrawSectionBody(state, describe, sections[side]);
            ImGui.PopID();
        }
        else
        {
            var topValues = sections.GetValueOrDefault(GameLayout.ValuesSection) ?? new List<Feature>();
            DrawTopValues(state, describe, topValues);
            ImGui.Spacing();

            // Everything not pinned to the top and not a side page, stacked in layout order.
            var used = order.Where(s => s != GameLayout.ValuesSection);
            foreach (var section in GameLayout.TabOrder(title, used))
            {
                if (GameLayout.SideSections.Contains(section)) continue;
                if (!sections.TryGetValue(section, out var features) || features.Count == 0) continue;

                ImGui.PushID(section);
                if (ImGui.CollapsingHeader(section, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    DrawSectionBody(state, describe, features);
                    ImGui.Spacing();
                }
                ImGui.PopID();
            }
        }

        ImGui.EndDisabled();
    }

    // ---------------------------------------------------------------- values

    /// <summary>
    /// The top table: the value features that live here (VALUE by default), plus every readout that
    /// no feature drives, shown read-only. A readout a moved value drives is shown by that value in
    /// its section, not duplicated here.
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
                // or a value moved to a section) shows itself elsewhere, so skip the row.
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

    // ---------------------------------------------------------------- a section

    private static void DrawSectionBody(AppState state, DescribeResult describe, List<Feature> features)
    {
        var values = features.Where(f => f.Kind == FeatureKind.Value).ToArray();
        var toggles = features.Where(f => f.Kind == FeatureKind.Toggle).ToArray();
        var actions = features.Where(f => f.Kind == FeatureKind.Action).ToArray();
        var choices = features.Where(f => f.Kind is FeatureKind.Enum or FeatureKind.Color).ToArray();

        ImGui.Spacing();

        if (values.Length > 0)
        {
            if (ImGui.BeginTable("section-values", 2,
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

    /// <summary>
    /// Toggles two to a row, each followed by its "on boot" (auto-apply) box. The box sits right
    /// after the label with a fixed gap, inside the cell, so it is never clipped by the column edge
    /// and never reads as belonging to the neighbouring toggle. The grid drops to one column when
    /// the panel is too narrow for two.
    /// </summary>
    private static void DrawToggleGrid(AppState state, Feature[] toggles)
    {
        if (toggles.Length == 0) return;

        var style = ImGui.GetStyle();
        float autoBox = ImGui.GetFrameHeight() + style.ItemInnerSpacing.X + ImGui.CalcTextSize("on boot").X;
        float widest = 0;
        foreach (var feature in toggles)
        {
            float w = ImGui.GetFrameHeight() + style.ItemInnerSpacing.X + ImGui.CalcTextSize(feature.Label).X;
            widest = Math.Max(widest, w);
        }

        // A cell needs the label, the gap and the box, plus the table's own cell padding.
        float cellNeeded = widest + 16 + autoBox + style.CellPadding.X * 2;
        int columns = toggles.Length > 1 && ImGui.GetContentRegionAvail().X >= cellNeeded * 2 ? 2 : 1;

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

            ImGui.SameLine(0, 16);
            bool auto = (session.ToggleAuto & bit) != 0;
            ImGui.PushStyleColor(ImGuiCol.Text, Ui.Grey);
            bool changed = ImGui.Checkbox("on boot", ref auto);
            ImGui.PopStyleColor();
            if (changed)
            {
                byte id = feature.Id;
                bool wanted = auto;
                state.Run(() => state.Client.FeatureSetAutoAsync(id, wanted));
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Auto-apply this toggle when the game boots");

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
