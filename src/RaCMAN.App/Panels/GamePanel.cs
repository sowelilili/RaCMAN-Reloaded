using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The Game page and its sub-pages. The page itself is the everyday controls: the player-value
/// table on top with the per-file "Options" switches in a column beside it, then the remaining
/// sections stacked and always visible. The sections the layout marks as "side" (Manips,
/// Collectables, Cosmetics, Debug by default) each get a sub-page, reached from the indented
/// entries under Game in the side nav. Features moved to "Unlocks" are not drawn here at all; the
/// Unlocks panel reads them through <see cref="FeaturesInSection"/>.
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
        ResetPresets();
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

        var sections = Assign(state.Session.TitleId ?? string.Empty, state.Session.Game, describe, out _);
        return GameLayout.SideSections
            .Where(s => !GameLayout.IsReserved(s) && sections.ContainsKey(s))
            .ToArray();
    }

    /// <summary>
    /// The running game's features in one section, in DESCRIBE order. This is how a panel other
    /// than this one gets at the features the layout sent its way: the Unlocks panel asks for
    /// <see cref="GameLayout.UnlocksSection"/> and draws them itself.
    /// </summary>
    public static IReadOnlyList<Feature> FeaturesInSection(AppState state, string section)
    {
        var describe = state.Describe;
        if (describe.Features.Length == 0) return Array.Empty<Feature>();

        var sections = Assign(state.Session.TitleId ?? string.Empty, state.Session.Game, describe, out _);
        return sections.TryGetValue(section, out var features) ? features : Array.Empty<Feature>();
    }

    /// <summary>Puts every feature in its section (client layout first, qwark group as the default).</summary>
    private static Dictionary<string, List<Feature>> Assign(
        string title, GameId game, DescribeResult describe, out List<string> firstSeen)
    {
        var sections = new Dictionary<string, List<Feature>>(StringComparer.Ordinal);
        firstSeen = new List<string>();
        foreach (var feature in describe.Features)
        {
            string section = GameLayout.SectionFor(title, game, feature, describe);
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
        var game = state.Session.Game;
        var order = new List<string>();
        var sections = describe.Features.Length > 0 ? Assign(title, game, describe, out order) : null;

        // A sub-page that the running game has nothing for (the game changed) falls back to the
        // page, and a reserved section is never a sub-page however it was asked for.
        if (SubPage is not null
            && (sections is null || GameLayout.IsReserved(SubPage) || !sections.ContainsKey(SubPage)))
        {
            SubPage = null;
        }

        if (SubPage is null && RequestedSubPage is { } wanted
            && sections is not null && !GameLayout.IsReserved(wanted) && sections.ContainsKey(wanted))
        {
            SubPage = wanted;
            RequestedSubPage = null;
        }

        Ui.Heading(SubPage is null ? "Game" : $"Game / {SubPage}");

        if (sections is null)
        {
            Ui.Hint(state.Connected
                ? "Start a supported game to see its controls."
                : "Connect to see the game's descriptors.");
            return;
        }

        bool enabled = state.Ingame;
        if (!enabled) ImGui.TextColored(Ui.Yellow, $"Controls are disabled outside INGAME (state is {state.Session.State}).");

        ImGui.BeginDisabled(!enabled);

        if (SubPage is { } side)
        {
            ImGui.PushID(side);
            DrawSectionBody(state, describe, sections[side], side);
            ImGui.PopID();
        }
        else
        {
            var topValues = sections.GetValueOrDefault(GameLayout.ValuesSection) ?? new List<Feature>();
            var options = sections.GetValueOrDefault(GameLayout.OptionsSection) ?? new List<Feature>();
            DrawTopArea(state, describe, topValues, options);
            ImGui.Spacing();

            // Everything not owned by a panel and not a side page, stacked in layout order.
            bool playerDrawn = false;
            var used = order.Where(s => !GameLayout.IsReserved(s));
            foreach (var section in GameLayout.TabOrder(title, game, used))
            {
                if (GameLayout.SideSections.Contains(section)) continue;
                if (!sections.TryGetValue(section, out var features) || features.Count == 0) continue;

                ImGui.PushID(section);
                if (ImGui.CollapsingHeader(section, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    DrawSectionBody(state, describe, features, section);
                    ImGui.Spacing();
                }
                ImGui.PopID();

                if (section == GameLayout.PlayerSection) playerDrawn = true;
            }

            // Every game has position slots, so the save/load pair gets a header of its own when
            // the running game (or the layout) left no Player section to hang it on.
            if (!playerDrawn)
            {
                ImGui.PushID(GameLayout.PlayerSection);
                if (ImGui.CollapsingHeader(GameLayout.PlayerSection, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    DrawPositionButtons(state);
                    ImGui.Spacing();
                }
                ImGui.PopID();
            }

            if (Ui.Debug) DrawRawReadouts(state, describe);
        }

        ImGui.EndDisabled();
    }

    /// <summary>
    /// Every readout the game reports, as the console sends it, for the "show debug information"
    /// switch. The everyday page shows only the editable values; the read-only readings (the
    /// savefile helper byte, update flags, frame counters) are wire detail nobody needs while playing.
    /// </summary>
    private static void DrawRawReadouts(AppState state, DescribeResult describe)
    {
        var session = state.Session;
        if (describe.Readouts.Length == 0) return;

        if (!ImGui.CollapsingHeader("Readouts (debug)")) return;

        if (!ImGui.BeginTable("raw-readouts", 3,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("#");
        ImGui.TableSetupColumn("Readout");
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        for (int i = 0; i < describe.Readouts.Length; i++)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(Ui.Grey, i.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(describe.Readouts[i]);
            ImGui.TableNextColumn();
            uint value = i < session.Readout.Length ? session.Readout[i] : 0u;
            ImGui.TextUnformatted($"{value}  (0x{value:X8})");
        }

        ImGui.EndTable();
    }

    // ---------------------------------------------------------------- values

    /// <summary>How much of the top row the value table takes when the Options column is beside it.</summary>
    private const float ValueColumnWeight = 0.58f;

    /// <summary>
    /// The top of the Game page: the value table, and beside it the per-file switches the layout
    /// moved to "Options". The table only ever needs a label and a number box, so the rest of the
    /// width would otherwise sit empty; a borderless two-column table puts the options there
    /// instead, top-aligned with the first value row. Either half alone takes the whole width.
    /// </summary>
    private static void DrawTopArea(
        AppState state, DescribeResult describe, List<Feature> values, List<Feature> options)
    {
        // Only a VALUE is editable in the top table; anything else moved to "Values" is not drawn.
        var editable = values.Where(f => f.Kind == FeatureKind.Value).ToArray();
        if (editable.Length == 0 && options.Count == 0) return;

        if (options.Count == 0)
        {
            DrawTopValues(state, describe, editable);
            return;
        }

        if (editable.Length == 0)
        {
            DrawOptions(state, describe, options);
            return;
        }

        if (!ImGui.BeginTable("top-area", 2, ImGuiTableFlags.SizingStretchProp)) return;

        ImGui.TableSetupColumn("values", ImGuiTableColumnFlags.WidthStretch, ValueColumnWeight);
        ImGui.TableSetupColumn("options", ImGuiTableColumnFlags.WidthStretch, 1f - ValueColumnWeight);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawTopValues(state, describe, editable);
        ImGui.TableNextColumn();
        DrawOptions(state, describe, options);

        ImGui.EndTable();
    }

    /// <summary>
    /// The "Options" column: the handful of in-game switches that belong with the save file rather
    /// than with a run (UYA's quick-select pause, RaC1's goodies menu). One toggle per row, so the
    /// column stays narrow next to the value table; actions and choices follow if any land here.
    /// </summary>
    private static void DrawOptions(AppState state, DescribeResult describe, List<Feature> features)
    {
        ImGui.PushID(GameLayout.OptionsSection);
        DrawSectionBody(state, describe, features, toggleColumns: 1, leadingSpace: false);
        ImGui.PopID();
    }

    /// <summary>
    /// The top table: the value features that live here, every one of them editable. A value moved
    /// to a section shows itself there, and a readout no feature drives is not shown at all. Rows
    /// follow the game's readout order, so the table reads the same each run.
    /// </summary>
    private static void DrawTopValues(AppState state, DescribeResult describe, Feature[] editable)
    {
        if (editable.Length == 0) return;

        // The mirror bookkeeping: a VALUE placed in this section is the one editable here.
        var byMirror = new Dictionary<int, Feature>();
        foreach (var feature in editable)
        {
            if (feature.MirrorReadout is { } r) byMirror[r] = feature;
        }

        if (!ImGui.BeginTable("player-values", 2,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("Value");
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch);

        var drawn = new HashSet<byte>();
        for (int i = 0; i < describe.Readouts.Length; i++)
        {
            if (byMirror.TryGetValue(i, out var feature) && drawn.Add(feature.Id))
                ValueRow(state, describe, feature);
        }

        // Values that mirror no readout (or one the game never named) follow in feature order.
        foreach (var feature in editable)
        {
            if (drawn.Add(feature.Id)) ValueRow(state, describe, feature);
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

        // The field sits in a table row, so let the row show through it. Hovered and active keep the
        // theme's own colours, which is what makes the field light up when you reach for it.
        ImGui.SetNextItemWidth(-1);
        ImGui.PushID(feature.Id);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        bool sent = Ui.NumberOnEnter("##v", live, Drafts, feature.Id, out long typed);
        ImGui.PopStyleColor();
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

    /// <summary>
    /// One section's controls. <paramref name="section"/> is the section's name when it has one,
    /// which is what earns "Player" its position buttons; <paramref name="toggleColumns"/> caps the
    /// toggle grid (the narrow Options column asks for one) and <paramref name="leadingSpace"/> is
    /// off for a body that has to line up with the top of a table cell.
    /// </summary>
    private static void DrawSectionBody(
        AppState state,
        DescribeResult describe,
        List<Feature> features,
        string? section = null,
        int toggleColumns = 2,
        bool leadingSpace = true)
    {
        var values = features.Where(f => f.Kind == FeatureKind.Value).ToArray();
        var toggles = features.Where(f => f.Kind == FeatureKind.Toggle).ToArray();
        var actions = features.Where(f => f.Kind == FeatureKind.Action).ToArray();
        var choices = features.Where(f => f.Kind is FeatureKind.Enum or FeatureKind.Color).ToArray();

        if (leadingSpace) ImGui.Spacing();

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

        DrawToggleGrid(state, toggles, toggleColumns);
        if (toggles.Length > 0 && (actions.Length > 0 || choices.Length > 0)) ImGui.Spacing();
        DrawActionGrid(state, actions);
        if (choices.Length > 0) ImGui.Spacing();

        // The preset row belongs to the colour editors, so it goes in front of the first of them
        // rather than at the top of the section: an ENUM that shares the section keeps its place.
        var colours = choices.Where(f => f.Kind == FeatureKind.Color).ToArray();
        bool presetsDrawn = false;
        foreach (var feature in choices)
        {
            if (feature.Kind == FeatureKind.Color && !presetsDrawn)
            {
                presetsDrawn = true;
                DrawColourPresets(state, colours, section);
            }

            DrawChoice(state, describe, feature);
        }

        if (section == GameLayout.PlayerSection) DrawPositionButtons(state);
    }

    /// <summary>
    /// Save and load the console's currently selected position slot: the pair the Positions panel
    /// draws per row, on the page the user is already looking at. The slot is the console's, so the
    /// label names it rather than offering a second place to choose one, and a save is followed by
    /// a POS_LIST so the Positions panel agrees about what is in the slot.
    /// </summary>
    private static void DrawPositionButtons(AppState state)
    {
        byte slot = state.Session.SelectedSlot;

        ImGui.Spacing();
        if (!ImGui.BeginTable("positions", 2, ImGuiTableFlags.SizingStretchSame)) return;

        ImGui.TableNextColumn();
        if (ImGui.Button($"Save position (slot {slot})", new Vector2(-1, 0)))
        {
            state.Run(async () =>
            {
                await state.Client.PosSaveAsync().ConfigureAwait(false);
                state.Post(state.RefreshPositions);
            });
        }

        ImGui.TableNextColumn();
        if (ImGui.Button($"Load position (slot {slot})", new Vector2(-1, 0)))
        {
            state.Run(() => state.Client.PosLoadAsync());
        }

        ImGui.EndTable();
    }

    /// <summary>The gap between the widest label in a column and that column's "on boot" boxes.</summary>
    private const float AutoBoxGap = 16f;

    /// <summary>
    /// Toggles two to a row, each followed by its "on boot" (auto-apply) box. Every box in a column
    /// sits at the same offset, the column's widest label plus a fixed gap, so the boxes line up
    /// instead of zig-zagging; inside a cell <c>SameLine(offset)</c> is measured from the column's
    /// own start, so one offset per column does it. A toggle the console marks LIVE gets no box at
    /// all. The grid drops to one column when the panel is too narrow for two, or when
    /// <paramref name="maxColumns"/> says so.
    /// </summary>
    private static void DrawToggleGrid(AppState state, Feature[] toggles, int maxColumns = 2)
    {
        if (toggles.Length == 0) return;

        var style = ImGui.GetStyle();
        float autoBox = ImGui.GetFrameHeight() + style.ItemInnerSpacing.X + ImGui.CalcTextSize("on boot").X;

        var labelWidth = new float[toggles.Length];
        float widest = 0;
        for (int i = 0; i < toggles.Length; i++)
        {
            labelWidth[i] = ImGui.GetFrameHeight() + style.ItemInnerSpacing.X + ImGui.CalcTextSize(toggles[i].Label).X;
            widest = Math.Max(widest, labelWidth[i]);
        }

        // A cell needs the label, the gap and the box, plus the table's own cell padding.
        float cellNeeded = widest + AutoBoxGap + autoBox + style.CellPadding.X * 2;
        int columns = maxColumns > 1 && toggles.Length > 1 && ImGui.GetContentRegionAvail().X >= cellNeeded * 2
            ? 2
            : 1;

        // Toggles fill left to right, so toggle i lands in column i % columns.
        var columnLabel = new float[columns];
        for (int i = 0; i < toggles.Length; i++)
        {
            int column = i % columns;
            columnLabel[column] = Math.Max(columnLabel[column], labelWidth[i]);
        }

        if (!ImGui.BeginTable("toggles", columns, ImGuiTableFlags.SizingStretchSame)) return;

        var session = state.Session;
        for (int i = 0; i < toggles.Length; i++)
        {
            var feature = toggles[i];
            ImGui.TableNextColumn();
            ImGui.PushID(feature.Id);

            ulong bit = 1UL << feature.Id;
            bool on = (session.ToggleState & bit) != 0;

            // A cheat that patches instructions is nothing this platform can do (RPCS3), and
            // qwark would answer UNSUPPORTED: the box is drawn, so the cheat is still listed
            // where it belongs, but it cannot be pressed and the tooltip says why.
            bool blocked = feature.WritesCode && state.CodePatchesUnsupported;

            ImGui.BeginDisabled(blocked);
            if (ImGui.Checkbox(feature.Label, ref on))
            {
                byte id = feature.Id;
                uint value = on ? 1u : 0u;
                state.Run(() => state.Client.FeatureSetAsync(id, value));
            }

            ImGui.EndDisabled();

            // AllowWhenDisabled: a greyed-out box is exactly the one whose tooltip is the point.
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                if (blocked) ImGui.SetTooltip(Ui.NoCodePatches);
                else if (feature.IsLive) ImGui.SetTooltip("Read from the game's memory");
                else if (feature.WritesCode) ImGui.SetTooltip("Patches game code");
            }

            // A live toggle is a game-memory byte qwark polls: there is nothing to apply on boot
            // and FEATURE_SET_AUTO is refused for it, so the box is left out. The column offsets
            // are computed from the labels alone, so the boxes on the other rows still line up.
            if (!feature.IsLive)
            {
                ImGui.SameLine(columnLabel[i % columns] + AutoBoxGap);
                bool auto = (session.ToggleAuto & bit) != 0;
                ImGui.BeginDisabled(blocked);
                ImGui.PushStyleColor(ImGuiCol.Text, Ui.Grey);
                bool changed = ImGui.Checkbox("on boot", ref auto);
                ImGui.PopStyleColor();
                ImGui.EndDisabled();
                if (changed)
                {
                    byte id = feature.Id;
                    bool wanted = auto;
                    state.Run(() => state.Client.FeatureSetAutoAsync(id, wanted));
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(blocked ? Ui.NoCodePatches : "Auto-apply this toggle when the game boots");
                }
            }

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
                ImGui.TextColored(Ui.Grey, $"{feature.Label}: loading options...");
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

    // ---------------------------------------------------------------- colour presets

    /// <summary>The presets on offer, for the game and section the row was last listed for.</summary>
    private static ColourPreset[] _presets = Array.Empty<ColourPreset>();

    private static int _presetIndex = -1;

    /// <summary>"&lt;game&gt;/&lt;section&gt;", so the listing happens on a change rather than once a frame.</summary>
    private static string _presetKey = string.Empty;

    private static string _presetName = string.Empty;

    private static bool _presetDeleteArmed;

    /// <summary>
    /// Makes the preset row list its files again on the next frame it draws, for when something
    /// other than the row itself wrote a preset (the import on the Settings panel).
    /// </summary>
    public static void InvalidatePresets() => _presetKey = string.Empty;

    private static void ResetPresets()
    {
        _presets = Array.Empty<ColourPreset>();
        _presetIndex = -1;
        _presetKey = string.Empty;
        _presetName = string.Empty;
        _presetDeleteArmed = false;
    }

    /// <summary>
    /// The saved-colours row above a section's colour editors: pick one to apply it, type a name and
    /// Save to capture what the section currently shows, Delete... to drop the picked one. Presets
    /// are the PC's and are keyed by game, so they survive a game change and a reconnect alike.
    /// </summary>
    private static void DrawColourPresets(AppState state, Feature[] colours, string? section)
    {
        if (colours.Length == 0) return;

        var game = state.Session.Game;
        string key = $"{(byte)game}/{section}";
        if (!string.Equals(_presetKey, key, StringComparison.Ordinal)) RefreshPresets(state, game, key);

        ImGui.PushID("colour-presets");

        string preview = _presetIndex >= 0 && _presetIndex < _presets.Length ? _presets[_presetIndex].Name : "(none)";

        ImGui.SetNextItemWidth(200);
        ImGui.BeginDisabled(_presets.Length == 0);
        if (ImGui.BeginCombo("Presets", preview))
        {
            for (int i = 0; i < _presets.Length; i++)
            {
                if (!ImGui.Selectable(_presets[i].Name, i == _presetIndex)) continue;

                _presetIndex = i;
                _presetDeleteArmed = false;
                _presetName = _presets[i].Name;
                ApplyPreset(state, colours, _presets[i]);
            }

            ImGui.EndCombo();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();

        // The same two-step the watchlists use: nothing here deletes a saved file on one click.
        if (_presetDeleteArmed)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
            if (ImGui.Button("Confirm delete")) DeletePreset(state, game, key);
            ImGui.PopStyleColor();

            ImGui.SameLine();
            if (ImGui.Button("Cancel")) _presetDeleteArmed = false;
        }
        else
        {
            ImGui.BeginDisabled(_presetIndex < 0);
            if (ImGui.Button("Delete...")) _presetDeleteArmed = true;
            ImGui.EndDisabled();
        }

        ImGui.SetNextItemWidth(200);
        if (Ui.InputTextWithHint("##preset-name", "Preset name", ref _presetName, 64))
        {
            // Typing is how a new preset is made, so the combo follows the box; a name with no
            // preset behind it simply selects nothing.
            SelectPreset(_presetName);
            _presetDeleteArmed = false;
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(_presetName.Trim().Length == 0);
        if (ImGui.Button("Save")) SavePreset(state, game, colours, key);
        ImGui.EndDisabled();

        ImGui.PopID();
        ImGui.Spacing();
    }

    private static void RefreshPresets(AppState state, GameId game, string key)
    {
        _presetKey = key;
        _presetDeleteArmed = false;
        _presets = state.ColourPresets.List(game, out string? problem).ToArray();
        if (problem is not null) state.AddToast($"Colour presets: {problem}", ToastKind.Error);
        SelectPreset(_presetName);
    }

    /// <summary>Points the combo at the preset the name box names, or at nothing.</summary>
    private static void SelectPreset(string name)
    {
        string wanted = (name ?? string.Empty).Trim();
        _presetIndex = wanted.Length == 0
            ? -1
            : Array.FindIndex(_presets, p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sends every colour the preset names, in one go. LastSet is updated as well as the console,
    /// so a COLOR feature that mirrors no readout still shows what was just applied.
    /// </summary>
    private static void ApplyPreset(AppState state, Feature[] colours, ColourPreset preset)
    {
        var wanted = new List<(byte Id, uint Value)>();
        foreach (var feature in colours)
        {
            if (!preset.TryGetColour(feature.Label, out uint rgb)) continue;
            wanted.Add((feature.Id, rgb));
            LastSet[feature.Id] = rgb;
        }

        if (wanted.Count == 0)
        {
            state.AddToast($"Preset {preset.Name} has nothing for these colours", ToastKind.Error);
            return;
        }

        state.Run(async () =>
        {
            foreach (var (id, value) in wanted)
            {
                await state.Client.FeatureSetAsync(id, value).ConfigureAwait(false);
            }
        }, $"Applied colour preset {preset.Name}");
    }

    private static void SavePreset(AppState state, GameId game, Feature[] colours, string key)
    {
        string name = _presetName.Trim();
        var values = colours
            .Select(f => new KeyValuePair<string, uint>(f.Label, CurrentValue(state, f) & 0xFFFFFFu))
            .ToArray();

        try
        {
            state.ColourPresets.Save(game, name, values);
            state.AddToast($"Colour preset {name} saved to {state.ColourPresets.FileFor(game)}", ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            state.AddToast($"Colour preset save failed: {ex.Message}", ToastKind.Error);
        }

        RefreshPresets(state, game, key);
    }

    private static void DeletePreset(AppState state, GameId game, string key)
    {
        _presetDeleteArmed = false;
        if (_presetIndex < 0 || _presetIndex >= _presets.Length) return;

        string name = _presets[_presetIndex].Name;
        try
        {
            if (state.ColourPresets.Delete(game, name)) state.AddToast($"Colour preset {name} deleted", ToastKind.Success);
            else state.AddToast($"Colour preset {name} was already gone", ToastKind.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.AddToast($"Colour preset delete failed: {ex.Message}", ToastKind.Error);
        }

        _presetName = string.Empty;
        RefreshPresets(state, game, key);
    }
}
