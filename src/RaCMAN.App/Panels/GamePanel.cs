using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The Game page and its sub-pages. The page itself is the everyday controls: the quick block at
/// the very top (die, the position pair, the savefile ACTIONs, the slot and planet controls), then
/// the player-value table with the per-file "Options" switches in a column beside it under a
/// "Values" header, then the remaining sections stacked and always visible. The sections the layout
/// hangs under a panel (Manips, Cosmetics and Debug under Game, Collectables under Unlocks by
/// default) each get a sub-page, reached from the indented entries under that panel in the side
/// nav. Features moved to "Unlocks" are not drawn here at all; the Unlocks panel reads them through
/// <see cref="FeaturesInSection"/>.
/// <para>
/// The sub-page machinery is here for both panels: sorting features into sections is this page's
/// job, so <see cref="SubPagesWithContent"/>, <see cref="ResolveSubPage(AppState, string)"/> and
/// <see cref="DrawSubPage(AppState, string, string)"/> are what the Unlocks panel calls to draw
/// the section hung under it exactly as this page draws its own.
/// </para>
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

    public static void Reset()
    {
        Drafts.Clear();
        LastSet.Clear();
        SubPageNav.Set(GameLayout.GameHost, null);
        ResetPresets();
    }

    /// <summary>The feature's current value: its readout when it names one, else what we last sent.</summary>
    private static uint CurrentValue(AppState state, Feature feature) =>
        feature.MirrorReadout is { } mirror
            ? state.Session.ReadoutAt(mirror) ?? 0u
            : LastSet.GetValueOrDefault(feature.Id);

    /// <summary>
    /// The sub-pages one panel shows: the sections the layout hangs under it that the running game
    /// has something in, in layout order. Drives the indented entries in the side nav. A game with
    /// no unlock table has no Unlocks entry to hang anything under, so what the file put there is
    /// listed under Game instead (<see cref="GameLayout.SubPagesUnder(string, bool)"/>).
    /// </summary>
    public static IReadOnlyList<string> SubPagesWithContent(AppState state, string host)
    {
        var describe = state.Describe;
        if (describe.Features.Length == 0) return Array.Empty<string>();

        var sections = Assign(state.DescribedTitle, state.DescribedGame, describe, out _);
        return GameLayout.SubPagesUnder(host, state.UnlocksUnsupported)
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

        var sections = Assign(state.DescribedTitle, state.DescribedGame, describe, out _);
        return sections.TryGetValue(section, out var features) ? features : Array.Empty<Feature>();
    }

    /// <summary>
    /// The sub-page a hosting panel draws this frame, or null for the panel's own page. Both hosts
    /// go through this, so both follow one rule: a sub-page the running game no longer has a
    /// section for (the game changed), one a panel places itself, and one that now belongs to the
    /// other panel (the unlock table came or went) are all dropped, and the sub-page a headless run
    /// asked for by name is taken up by whichever panel hosts it as soon as the descriptors are in.
    /// </summary>
    public static string? ResolveSubPage(AppState state, string host)
    {
        var describe = state.Describe;
        var sections = describe.Features.Length > 0
            ? Assign(state.DescribedTitle, state.DescribedGame, describe, out _)
            : null;

        return ResolveSubPage(state, host, sections);
    }

    /// <summary>
    /// <see cref="ResolveSubPage(AppState, string)"/> for a caller that has already sorted the
    /// features into their sections.
    /// </summary>
    public static string? ResolveSubPage(
        AppState state, string host, IReadOnlyDictionary<string, List<Feature>>? sections)
    {
        string? open = SubPageNav.For(host);
        if (open is not null && !HostShows(state, host, open, sections))
        {
            open = null;
            SubPageNav.Set(host, null);
        }

        if (open is null && SubPageNav.Requested is { } wanted && HostShows(state, host, wanted, sections))
        {
            open = wanted;
            SubPageNav.Requested = null;
            SubPageNav.Set(host, open);
        }

        return open;
    }

    /// <summary>
    /// Whether one panel would draw a section as its sub-page: the running game has to have
    /// something in it, no panel may place it itself, and the layout has to hang it there. A
    /// section the file hangs nowhere belongs to the Game page, which is what lets
    /// <c>--game-section</c> open a stacked section as a page of its own.
    /// </summary>
    private static bool HostShows(
        AppState state, string host, string section, IReadOnlyDictionary<string, List<Feature>>? sections) =>
        sections is not null
        && !string.IsNullOrWhiteSpace(section)
        && !GameLayout.IsReserved(section)
        && sections.ContainsKey(section)
        && string.Equals(GameLayout.HostFor(section, state.UnlocksUnsupported), host, StringComparison.Ordinal);

    /// <summary>
    /// The panel that would open a section as a sub-page right now, or null when the running game
    /// has nothing in it. This is how the window routes a <c>--game-section</c> to the panel that
    /// hosts the section before that panel is ever drawn.
    /// </summary>
    public static string? HostForOpenableSubPage(AppState state, string section)
    {
        var describe = state.Describe;
        if (describe.Features.Length == 0) return null;

        var sections = Assign(state.DescribedTitle, state.DescribedGame, describe, out _);
        foreach (var host in GameLayout.Hosts)
        {
            if (HostShows(state, host, section, sections)) return host;
        }

        return null;
    }

    /// <summary>
    /// What a game calls the ACTION that does what the console's own DIE opcode does. The quick
    /// block sends the opcode, so a game that describes the action as well would otherwise put the
    /// same button on the page twice.
    /// </summary>
    public const string DieLabel = "Die";

    /// <summary>True for an ACTION a game describes for dying, whatever the label's spacing or case.</summary>
    public static bool IsDieAction(Feature feature) =>
        feature.Kind == FeatureKind.Action
        && string.Equals(feature.Label.Trim(), DieLabel, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The features the quick block draws itself: the two ACTIONs the console flags SAVE_ASIDE and
    /// LOAD_ASIDE, and a "Die" the game describes. <see cref="Assign"/> takes these out before it
    /// sorts anything, so no section draws one of them a second time and a section left with
    /// nothing but these (a Savefile group that was only the two of them) stops appearing.
    /// </summary>
    public static IReadOnlySet<byte> QuickClaims(DescribeResult describe)
    {
        var claimed = new HashSet<byte>();
        if (describe.SaveAsideAction is { } save) claimed.Add(save.Id);
        if (describe.LoadAsideAction is { } load) claimed.Add(load.Id);

        foreach (var feature in describe.Features)
        {
            if (IsDieAction(feature)) claimed.Add(feature.Id);
        }

        return claimed;
    }

    /// <summary>
    /// Every section the game has and what is in each of them, the quick block's own rows already
    /// gone. This is what the page draws from, and what the layout tests read the sorting out of.
    /// </summary>
    public static IReadOnlyDictionary<string, List<Feature>> SectionsFor(
        string title, GameId game, DescribeResult describe) =>
        Assign(title, game, describe, out _);

    /// <summary>Puts every feature in its section (client layout first, qwark group as the default).</summary>
    private static Dictionary<string, List<Feature>> Assign(
        string title, GameId game, DescribeResult describe, out List<string> firstSeen)
    {
        var claimed = QuickClaims(describe);
        var sections = new Dictionary<string, List<Feature>>(StringComparer.Ordinal);
        firstSeen = new List<string>();
        foreach (var feature in describe.Features)
        {
            if (claimed.Contains(feature.Id)) continue;

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
        // The described game, not the running one: at the XMB the page keeps the layout it had,
        // greyed out, rather than re-sorting itself into qwark's default groups and back again.
        string title = state.DescribedTitle;
        var game = state.DescribedGame;
        var order = new List<string>();
        var sections = describe.Features.Length > 0 ? Assign(title, game, describe, out order) : null;

        if (ResolveSubPage(state, GameLayout.GameHost, sections) is { } side)
        {
            DrawSubPage(state, GameLayout.GameHost, side, sections![side]);
            return;
        }

        Ui.Heading("Game");

        if (sections is null)
        {
            Ui.Hint(state.Connected
                ? "Start a supported game to see its controls."
                : "Connect to see the game's controls.");
            return;
        }

        bool enabled = state.Ingame;
        if (!enabled) ImGui.TextColored(Ui.Yellow, OutsideIngame(state));

        ImGui.BeginDisabled(!enabled);

        DrawQuickBlock(state, describe, sections.GetValueOrDefault(GameLayout.QuickSection));
        ImGui.Spacing();

        // The value table and the Options column beside it, under a header of their own so the
        // whole of the page folds away the same way.
        var topValues = sections.GetValueOrDefault(GameLayout.ValuesSection) ?? new List<Feature>();
        var options = sections.GetValueOrDefault(GameLayout.OptionsSection) ?? new List<Feature>();
        if (topValues.Any(f => f.Kind == FeatureKind.Value) || options.Count > 0)
        {
            ImGui.PushID(GameLayout.ValuesSection);
            if (SectionHeader(state, GameLayout.ValuesSection))
            {
                DrawTopArea(state, describe, topValues, options);
                ImGui.Spacing();
            }
            ImGui.PopID();
        }

        // Everything not owned by a panel and not a sub-page of one, stacked in layout order. A
        // section the quick block emptied is not in the dictionary at all, so it draws no header.
        var used = order.Where(s => !GameLayout.IsReserved(s));
        var subPages = GameLayout.AllSubPages;
        foreach (var section in GameLayout.TabOrder(title, game, used))
        {
            if (subPages.Contains(section)) continue;
            if (!sections.TryGetValue(section, out var features) || features.Count == 0) continue;

            ImGui.PushID(section);
            if (SectionHeader(state, section))
            {
                DrawSectionBody(state, describe, features, section);
                ImGui.Spacing();
            }
            ImGui.PopID();
        }

        if (Ui.Debug) DrawRawReadouts(state, describe);

        ImGui.EndDisabled();
    }

    /// <summary>Why the controls on a page are greyed out, in the words every page uses.</summary>
    private static string OutsideIngame(AppState state) =>
        $"Controls are disabled outside INGAME (state is {state.Session.State.DisplayName()}).";

    /// <summary>
    /// One section as a page of its own, for whichever panel hosts it: the heading naming the panel
    /// and the section, then the section's features in the blocks its headings make. This is the
    /// whole of what a sub-page is, so the Unlocks panel draws the section hung under it by calling
    /// this and nothing else.
    /// </summary>
    public static void DrawSubPage(AppState state, string host, string section) =>
        DrawSubPage(state, host, section, FeaturesInSection(state, section));

    private static void DrawSubPage(
        AppState state, string host, string section, IReadOnlyList<Feature> features)
    {
        Ui.Heading($"{host} / {section}");

        bool enabled = state.Ingame;
        if (!enabled) ImGui.TextColored(Ui.Yellow, OutsideIngame(state));

        ImGui.BeginDisabled(!enabled);
        ImGui.PushID(section);
        DrawSectionBody(state, state.Describe, features, section);
        ImGui.PopID();
        ImGui.EndDisabled();
    }

    /// <summary>
    /// One section's collapsing header, open unless this game's settings say it was folded away.
    /// Which sections are open is the user's rather than the layout's, so the state is pushed in
    /// every frame instead of being left to DefaultOpen, and the click that changed it is written
    /// back at once: the settings file is the only place it lives.
    /// </summary>
    private static bool SectionHeader(AppState state, string section)
    {
        var settings = state.Settings;
        var game = state.DescribedGame;

        bool was = settings.SectionOpen(game, section);
        ImGui.SetNextItemOpen(was);

        bool open = ImGui.CollapsingHeader(section);
        if (open == was) return open;

        settings.SetSectionOpen(game, section, open);
        settings.Save();

        return open;
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

        // A readout a signed VALUE mirrors is shown as that feature reads it, with the raw word
        // still beside it: this table is where someone goes to see the bits.
        var signedBy = new Dictionary<int, Feature>();
        foreach (var feature in describe.Features)
        {
            if (feature.IsSigned && feature.MirrorReadout is { } r) signedBy[r] = feature;
        }

        for (int i = 0; i < describe.Readouts.Length; i++)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(Ui.Grey, i.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(describe.Readouts[i]);
            ImGui.TableNextColumn();
            uint value = i < session.Readout.Length ? session.Readout[i] : 0u;
            long shown = signedBy.TryGetValue(i, out var signed) ? signed.SignExtend(value) : value;
            ImGui.TextUnformatted($"{shown}  (0x{value:X8})");
        }

        ImGui.EndTable();
    }

    // ---------------------------------------------------------------- values

    /// <summary>How much of the top row the value table takes when the Options column is beside it.</summary>
    private const float ValueColumnWeight = 0.42f;

    /// <summary>
    /// The "Values" header's body: the value table, and beside it the per-file switches the layout
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
        Ui.TableLabel(feature.Label);
        ImGui.TableNextColumn();
        DrawValueEditor(state, describe, feature);
    }

    private static void DrawValueEditor(AppState state, DescribeResult describe, Feature feature)
    {
        // A signed feature's readout carries the raw field, so the box shows the number behind it
        // rather than the bits: -1 on a halfword, not 65535 (revision 1.7).
        long live = feature.SignExtend(CurrentValue(state, feature));

        // The field sits in a table row, so it is drawn the way every boxed table cell is.
        ImGui.SetNextItemWidth(-1);
        ImGui.PushID(feature.Id);
        Ui.PushTableInput();
        bool sent = Ui.NumberOnEnter("##v", live, Drafts, feature.Id, out long typed);
        Ui.PopTableInput();
        ImGui.PopID();

        if (feature.HasRange && ImGui.IsItemHovered())
            ImGui.SetTooltip($"{feature.RangeMin}..{feature.RangeMax}" + (feature.MirrorReadout is null ? "  (no live readout)" : ""));

        if (!sent) return;

        // Encode clamps to the feature's range and sends the low bits of the field, so a negative
        // number reaches qwark as the word the game already stores.
        uint value = feature.Encode(typed);

        byte id = feature.Id;
        LastSet[id] = value;
        state.Run(() => state.Client.FeatureSetAsync(id, value));
    }

    // ---------------------------------------------------------------- a section

    /// <summary>
    /// One section's controls, in the blocks the layout's headings make: a section the file says
    /// nothing about is one block and draws exactly what it always did, and a section with headings
    /// draws each run of features under its own header. <paramref name="section"/> is the section's
    /// name when it has one, which is what keys both the headings and its colour presets.
    /// </summary>
    private static void DrawSectionBody(
        AppState state,
        DescribeResult describe,
        IReadOnlyList<Feature> features,
        string? section = null,
        int toggleColumns = 2,
        bool leadingSpace = true)
    {
        var blocks = section is null
            ? new[] { new GameLayout.FeatureBlock(null, features) }
            : GameLayout.Blocks(state.DescribedTitle, state.DescribedGame, section, features);

        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];

            // Every block draws the same tables, so each needs an id of its own for ImGui to keep
            // them apart.
            ImGui.PushID(i);

            if (block.Heading is { } heading)
            {
                if (i > 0) ImGui.Spacing();
                Ui.Heading(heading);
            }

            // The gap before the first block is the caller's to ask for; a block under a heading
            // has that heading's own spacing above it already.
            DrawFeatureBlock(state, describe, block.Features, section, toggleColumns,
                leadingSpace && i == 0 && block.Heading is null);

            ImGui.PopID();
        }
    }

    /// <summary>
    /// One run of features: the value table, the toggle grid, the actions and then the choices,
    /// with the colour presets in front of the first colour editor. <paramref name="toggleColumns"/>
    /// caps the toggle grid (the narrow Options and Quick columns ask for one) and
    /// <paramref name="leadingSpace"/> is off for a body that has to line up with the top of a
    /// table cell.
    /// </summary>
    private static void DrawFeatureBlock(
        AppState state,
        DescribeResult describe,
        IReadOnlyList<Feature> features,
        string? section,
        int toggleColumns,
        bool leadingSpace)
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
    }

    // ---------------------------------------------------------------- the quick block

    /// <summary>How much of the quick block's row the stacked buttons take.</summary>
    private const float QuickButtonWeight = 0.42f;

    /// <summary>
    /// The block at the very top of the Game page: the few controls a run reaches for every other
    /// minute, above everything else and in one place. Buttons on the left, the console's position
    /// slot and the planet controls on the right, and nothing here is drawn anywhere else on the
    /// page: Die is the console's own opcode (an ACTION a game describes for it is claimed by
    /// <see cref="QuickClaims"/>), the position pair used to end the Player section, and the two
    /// buttons after them are the flagged savefile ACTIONs the Savefile section used to hold. The
    /// planet controls and the slot dropdown are this block's alone, so the Positions panel is the
    /// slot table and nothing else. A game with no savefile helper simply gets the buttons it has,
    /// and one that named no planets gets no planet controls.
    /// </summary>
    private static void DrawQuickBlock(AppState state, DescribeResult describe, List<Feature>? moved)
    {
        if (!ImGui.BeginTable("quick", 2, ImGuiTableFlags.SizingStretchProp)) return;

        ImGui.TableSetupColumn("buttons", ImGuiTableColumnFlags.WidthStretch, QuickButtonWeight);
        ImGui.TableSetupColumn("places", ImGuiTableColumnFlags.WidthStretch, 1f - QuickButtonWeight);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawQuickButtons(state, describe, moved);

        ImGui.TableNextColumn();
        PositionsPanel.SlotPicker(state);
        PositionsPanel.PlanetLoadControls(state);

        ImGui.EndTable();
    }

    /// <summary>
    /// The quick block's left column, one full-width button per row so the column stays a column
    /// however narrow the window is. The slot the position pair acts on is the console's, so the
    /// labels name it rather than offering a second place to choose one, and a save is followed by
    /// a POS_LIST so the Positions panel agrees about what is in the slot.
    /// </summary>
    private static void DrawQuickButtons(AppState state, DescribeResult describe, List<Feature>? moved)
    {
        var wide = new Vector2(-1, 0);
        byte slot = state.Session.SelectedSlot;

        if (ImGui.Button("Die", wide)) state.Run(() => state.Client.DieAsync());

        if (ImGui.Button("Save position", wide))
        {
            state.Run(async () =>
            {
                await state.Client.PosSaveAsync().ConfigureAwait(false);
                state.Post(() => state.RefreshPositions());
            });
        }

        if (ImGui.Button("Load position", wide))
        {
            state.Run(() => state.Client.PosLoadAsync());
        }

        // The savefile helper is a code cave the console branches the game into, which is the one
        // thing RPCS3 cannot do: the buttons stay where they are and say why instead.
        bool blocked = state.CodePatchesUnsupported;
        ImGui.BeginDisabled(blocked);
        DrawAsideButton(state, describe.SaveAsideAction, blocked, wide);
        DrawAsideButton(state, describe.LoadAsideAction, blocked, wide);
        ImGui.EndDisabled();

        // Whatever the layout sent to "Quick", under the buttons and in the same narrow column.
        if (moved is { Count: > 0 })
        {
            ImGui.PushID(GameLayout.QuickSection);
            DrawSectionBody(state, describe, moved, GameLayout.QuickSection, toggleColumns: 1);
            ImGui.PopID();
        }
    }

    /// <summary>One of the two flagged savefile ACTIONs, or nothing for a game that has no helper.</summary>
    private static void DrawAsideButton(AppState state, Feature? feature, bool blocked, Vector2 size)
    {
        if (feature is not { } action) return;

        ImGui.PushID(action.Id);
        if (ImGui.Button(action.Label, size))
        {
            byte id = action.Id;
            state.Run(() => state.Client.FeatureTriggerAsync(id));
        }

        ImGui.PopID();

        // AllowWhenDisabled: the greyed-out button is the one whose tooltip is the point.
        if (blocked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Ui.NoCodePatches);
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

            // AllowWhenDisabled: a greyed-out box is exactly the one whose tooltip is the point. A
            // LIVE toggle gets none: that the console reads it back out of the game is how every
            // toggle here behaves as far as the user is concerned, so saying so was noise.
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                if (blocked) ImGui.SetTooltip(Ui.NoCodePatches);
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

        var game = state.DescribedGame;
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
