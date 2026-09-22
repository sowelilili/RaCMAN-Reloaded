using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

public static class PositionsPanel
{
    /// <summary>
    /// How wide a coordinate is drawn, which is "-1234.56" and a digit of room. The client's font
    /// is fixed-width, so padding to this is what stops the row shuffling sideways as the player
    /// moves and a number gains or loses a digit.
    /// </summary>
    public const int CoordinateWidth = 9;

    /// <summary>How wide a labelled combo is drawn where there is room for all of it.</summary>
    private const float ComboWidth = 260f;

    /// <summary>And the least it is squeezed to in a column that has no room for that.</summary>
    private const float MinComboWidth = 90f;

    /// <summary>How long a slot's name may be. Long enough for a trick's name, short enough for a row.</summary>
    private const uint NameLength = 48;

    /// <summary>
    /// What is in a coordinate box while it has focus, by slot and axis. The watches table's rule,
    /// for the same reason: the table re-reads itself on a timer, and a re-read arriving mid-edit
    /// must not take the half-typed number away. At most one of these ever exists, because at most
    /// one box has focus.
    /// </summary>
    private static readonly Dictionary<(byte Slot, int Axis), string> CoordinateDrafts = new();

    /// <summary>The same for a name box, which is this PC's own and needs no console at all.</summary>
    private static readonly Dictionary<(byte Planet, byte Slot), string> NameDrafts = new();

    private static float _sinceRefresh;

    /// <summary>
    /// One coordinate as the panel shows it: two decimals, right-aligned in
    /// <see cref="CoordinateWidth"/>. Null is an empty slot, which gets the same width so the
    /// column below it stays a column.
    /// </summary>
    public static string Coordinate(float? value) =>
        (value is { } number ? number.ToString("0.00", CultureInfo.InvariantCulture) : "-")
        .PadLeft(CoordinateWidth);

    /// <summary>
    /// What a coordinate box shows: the same two decimals, without the padding, because a box is
    /// its own column and the text in it is not what holds the table straight.
    /// </summary>
    public static string CoordinateText(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// One typed coordinate. Invariant, so the box takes back exactly what it shows whatever the
    /// PC's own decimal separator is, and a number with no number in it - a blank box, a lone
    /// minus, an infinity - is refused rather than sent to the console as one.
    /// </summary>
    public static bool TryParseCoordinate(string? text, out float value)
    {
        value = 0f;

        var typed = (text ?? string.Empty).Trim();
        if (typed.Length == 0) return false;
        if (!float.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)) return false;
        if (float.IsNaN(parsed) || float.IsInfinity(parsed)) return false;

        value = parsed;
        return true;
    }

    /// <summary>Whether the timed re-read fires this frame, and what the clock says afterwards.</summary>
    public readonly record struct RefreshTick(float Elapsed, bool Read);

    /// <summary>
    /// The slot table's own clock. <paramref name="period"/> is the Settings panel's interval, zero
    /// meaning nothing automatic reads; <paramref name="allowed"/> is whether a read may go out at
    /// all, and <paramref name="reading"/> whether one is still on the wire, in which case the
    /// clock is left where it is rather than reset, so the read happens the moment the last one is
    /// answered instead of a whole period later.
    /// </summary>
    public static RefreshTick NextRefresh(float elapsed, float delta, float period, bool allowed, bool reading)
    {
        if (!allowed || period <= 0f) return new RefreshTick(0f, false);

        elapsed += delta;
        if (elapsed < period || reading) return new RefreshTick(elapsed, false);

        return new RefreshTick(0f, true);
    }

    /// <summary>
    /// Drops what is being typed and the clock. The values themselves are the running process's and
    /// live on <see cref="AppState.Positions"/>, which clears itself.
    /// </summary>
    public static void ClearData()
    {
        CoordinateDrafts.Clear();
        NameDrafts.Clear();
        _sinceRefresh = 0;
    }

    /// <summary>
    /// The same, plus the names read off disk: a different game has its own file, and a file that
    /// was edited by hand between games should be read again rather than remembered.
    /// </summary>
    public static void Reset(AppState state)
    {
        ClearData();
        state.PositionNames.Forget();
    }

    /// <summary>
    /// The slot table and the line the player is standing on. The planet controls and Die are the
    /// Game page's quick block now, and the slot the quick block acts on is chosen there as well,
    /// so this panel is the eight slots and nothing else; the planet is still named, because it is
    /// the planet the slots below belong to.
    /// <para>
    /// The table re-reads itself on the Settings panel's table refresh interval, the way the level
    /// flags and the unlocks do: a slot is edited from the pad as well as from here, and a stale
    /// table is worse than a re-read a second. An interval of zero leaves the Refresh button as the
    /// only thing that reads.
    /// </para>
    /// </summary>
    public static void Draw(AppState state)
    {
        Ui.Heading("Positions");

        var session = state.Session;
        bool enabled = state.Ingame;
        if (!enabled) Ui.Warning($"Position commands need INGAME (state is {session.State.DisplayName()}).");

        ImGui.TextUnformatted($"Current planet: {PlanetName(state, session.CurrentPlanet)} ({session.CurrentPlanet})");
        ImGui.TextUnformatted($"Position: {Coordinate(session.PosX)}, {Coordinate(session.PosY)}, {Coordinate(session.PosZ)}");

        // Only where nothing else reads the table: with an interval set on the Settings panel the
        // slots are never more than that many seconds old, and the button had nothing to add.
        if (!state.Settings.AutoRefreshesTables)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Refresh slots")) state.RefreshPositions();
        }

        // The interval is read every frame, so a change on the Settings panel takes effect at once.
        // Nothing goes out while the console is busy with a launch (section 1.1) or outside INGAME,
        // where POS_LIST is refused.
        var tick = NextRefresh(
            _sinceRefresh,
            ImGui.GetIO().DeltaTime,
            state.Settings.TableRefreshSeconds,
            state.Ingame && !state.ConsoleBusy,
            state.PositionsPending);

        _sinceRefresh = tick.Elapsed;
        if (tick.Read) state.RefreshPositions(quiet: true);

        ImGui.BeginDisabled(!enabled);
        ImGui.Spacing();

        DrawSlots(state, session);

        ImGui.EndDisabled();
    }

    /// <summary>
    /// The planet controls: the combo with the filler names left out, the reset boxes the running
    /// game has, and Load planet across the rest of the row. The console owns the planet selection
    /// the way it owns the slot (a combo's Load planet loads whatever it holds), so the combo and
    /// the boxes show what telemetry reports and a change to either is sent as PLANET_SELECT the
    /// moment it is made, which is why there is no Select button. The Game page's quick block is
    /// the only page that draws them, and this is where they live because the planet list and the
    /// slot table are the same panel's subject. False when the game named no planets, which draws
    /// nothing at all: the quick block leaves out what a game has not got.
    /// </summary>
    public static bool PlanetLoadControls(AppState state)
    {
        if (state.Planets.Length == 0) return false;

        // The combo shows the planets the game has; the index behind the pick is the one the
        // console numbered it with, filler entries included, because that is what a request
        // carries. A selection the console holds on a hidden entry shows as the first real one.
        var choices = PlanetChoices.For(state.Planets);
        int pick = Math.Max(0, choices.PositionOf(state.Session.SelectedPlanet));
        var flags = state.Session.PlanetFlags;

        ImGui.SetNextItemWidth(FittedComboWidth("Planet"));
        if (ImGui.Combo("Planet", ref pick, choices.Labels, choices.Count))
        {
            byte planet = (byte)choices.PlanetAt(pick);
            state.Run(() => state.Client.PlanetSelectAsync(planet, flags));
        }

        // Only the games that do these two on the way into a planet are offered them.
        var boxes = PlanetResetOptions.For(state.DescribedGame, state.LevelFlagsUnsupported);
        if (boxes.LevelFlags)
        {
            ResetBox(state, "Reset level flags", choices.PlanetAt(pick), flags, PlanetFlags.ResetLevelFlags);
        }

        if (boxes.SpecialBolts)
        {
            if (boxes.LevelFlags) ImGui.SameLine();
            ResetBox(state, "Reset special bolts", choices.PlanetAt(pick), flags, PlanetFlags.ResetSpecialBolts);
        }

        if (ImGui.Button("Load planet"))
        {
            byte planet = (byte)choices.PlanetAt(pick);
            state.Run(() => state.Client.PlanetLoadAsync(planet, (byte)flags));
        }

        return true;
    }

    /// <summary>
    /// One reset box: it shows the console's flag, and ticking it sends the selection back with
    /// that flag turned, so the box follows telemetry like the combo beside it.
    /// </summary>
    private static void ResetBox(AppState state, string label, int planet, PlanetFlags flags, PlanetFlags bit)
    {
        bool on = (flags & bit) != 0;
        if (!ImGui.Checkbox(label, ref on)) return;

        byte selected = (byte)planet;
        var turned = on ? flags | bit : flags & ~bit;
        state.Run(() => state.Client.PlanetSelectAsync(selected, turned));
    }

    /// <summary>
    /// The console's selected position slot as a dropdown: picking one sends POS_SELECT, and the
    /// slot table's double-click is the only other thing that does. The Game page's quick block
    /// draws it beside the save and load buttons, so the slot those two act on is chosen where they
    /// are; the console owns the selection, so what the box shows is what telemetry reports.
    /// </summary>
    public static void SlotPicker(AppState state)
    {
        var slots = state.Positions.Slots;
        byte selected = state.Session.SelectedSlot;
        var labels = SlotLabels(state.Positions, selected, NameLookup(state, state.Positions.Planet));
        int pick = Math.Max(0, Array.FindIndex(slots, s => s.Slot == selected));

        ImGui.SetNextItemWidth(FittedComboWidth("Slot"));
        ImGui.BeginDisabled(slots.Length == 0);
        if (ImGui.Combo("Slot", ref pick, labels, labels.Length) && pick < slots.Length)
        {
            byte slot = slots[pick].Slot;
            state.Run(() => state.Client.PosSelectAsync(slot));
        }

        ImGui.EndDisabled();
    }

    /// <summary>
    /// What one slot is called wherever a slot is picked or listed: the name the user gave it,
    /// with the slot number after it so the console's own numbering is never lost, and the plain
    /// label when there is no name. A slot with no name says whether it holds a position, which is
    /// what a named one says by having a name at all.
    /// </summary>
    public static string SlotLabel(byte slot, bool filled, string? name)
    {
        var given = (name ?? string.Empty).Trim();
        if (given.Length > 0) return $"{given} (slot {slot})";

        return filled ? $"Slot {slot} (saved)" : $"Slot {slot}";
    }

    /// <summary>
    /// What the slot dropdown offers: one entry per slot the console listed, under the names this
    /// PC keeps for them. A game whose slots have not been read yet (outside INGAME, or before the
    /// first POS_LIST) still names the selected one, so the box says what the console last told us
    /// rather than going blank.
    /// </summary>
    public static string[] SlotLabels(PositionList positions, byte selected, Func<byte, string?>? name = null)
    {
        var slots = positions.Slots;
        if (slots.Length == 0) return new[] { SlotLabel(selected, false, name?.Invoke(selected)) };

        var labels = new string[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            labels[i] = SlotLabel(slots[i].Slot, slots[i].Filled, name?.Invoke(slots[i].Slot));
        }

        return labels;
    }

    /// <summary>
    /// How wide a labelled combo is drawn: its usual width where there is room for it, and as much
    /// of that as the column allows where there is not, since the quick block's right-hand column
    /// is narrower than this panel.
    /// </summary>
    private static float FittedComboWidth(string label)
    {
        float room = ImGui.GetContentRegionAvail().X
                     - ImGui.CalcTextSize(label).X
                     - ImGui.GetStyle().ItemInnerSpacing.X;
        return Math.Max(MinComboWidth, Math.Min(ComboWidth, room));
    }

    /// <summary>
    /// What a slot is called, for a game whose names this client keeps. A game the console has not
    /// named yet has no file of its own, so nothing is read and nothing is written under it.
    /// </summary>
    private static Func<byte, string?> NameLookup(AppState state, byte planet)
    {
        var game = state.DescribedGame;
        if (game == GameId.None) return _ => null;

        var names = state.PositionNames;
        return slot => names.Name(game, planet, slot);
    }

    private static void DrawSlots(AppState state, SessionInfo session)
    {
        var positions = state.Positions;
        if (positions.Slots.Length == 0)
        {
            Ui.Hint(!state.Connected ? "Connect to read the position slots."
                : !state.Ingame ? $"Reading the position slots needs INGAME (state is {session.State.DisplayName()})."
                : "No positions saved for this planet.");
            return;
        }

        var style = ImGui.GetStyle();

        // Measured rather than guessed: the row has three coordinate boxes and three buttons on it
        // now, and the Name column is what is left over, so everything else is as narrow as its own
        // contents allow or the row does not fit the window at the size it opens at.
        float slotWidth = ImGui.CalcTextSize("> 0").X + (style.FramePadding.X * 2f);
        float coordinateWidth = ImGui.CalcTextSize("-1234.56").X + (style.FramePadding.X * 2f);
        float actionWidth = ImGui.CalcTextSize("Save").X + ImGui.CalcTextSize("Load").X + ImGui.CalcTextSize("Clear").X
                            + (style.FramePadding.X * 6f) + (style.ItemSpacing.X * 2f);

        if (!ImGui.BeginTable("slots", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return;

        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, slotWidth);
        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("X", ImGuiTableColumnFlags.WidthFixed, coordinateWidth);
        ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, coordinateWidth);
        ImGui.TableSetupColumn("Z", ImGuiTableColumnFlags.WidthFixed, coordinateWidth);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, actionWidth);
        ImGui.TableHeadersRow();

        byte planet = positions.Planet;
        var game = state.DescribedGame;
        float rowHeight = ImGui.GetFrameHeight();

        foreach (var slot in positions.Slots)
        {
            ImGui.TableNextRow();
            ImGui.PushID(slot.Slot);

            byte index = slot.Slot;

            ImGui.TableNextColumn();
            DrawSlotCell(state, session, index, rowHeight);

            ImGui.TableNextColumn();
            DrawNameCell(state, game, planet, index);

            // An empty slot has no position to edit and no blob behind it: POS_EDIT would answer
            // NOT_FOUND, so the row says so instead of offering three boxes that cannot be sent.
            if (slot.Filled)
            {
                ImGui.TableNextColumn();
                if (TryCoordinateCell(state, index, 0, slot.X, out float x)) Edit(state, index, x, slot.Y, slot.Z);
                ImGui.TableNextColumn();
                if (TryCoordinateCell(state, index, 1, slot.Y, out float y)) Edit(state, index, slot.X, y, slot.Z);
                ImGui.TableNextColumn();
                if (TryCoordinateCell(state, index, 2, slot.Z, out float z)) Edit(state, index, slot.X, slot.Y, z);
            }
            else
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    CoordinateDrafts.Remove((index, axis));
                    ImGui.TableNextColumn();
                    Ui.TableLabel(Ui.Grey, "-");
                }
            }

            ImGui.TableNextColumn();
            DrawActions(state, slot);

            ImGui.PopID();
        }

        ImGui.EndTable();

        Ui.Hint("Double-click a row to select its slot. The names are kept on this PC; the slots are the console's.");
    }

    /// <summary>
    /// The slot number, as the row's own handle: it is drawn across the whole row so the selected
    /// slot is a highlighted line rather than one green digit, and double-clicking it sends
    /// POS_SELECT. A single click does nothing, because the console owns the selection and a stray
    /// click over a table is not a request.
    /// </summary>
    private static void DrawSlotCell(AppState state, SessionInfo session, byte slot, float rowHeight)
    {
        bool selected = session.SelectedSlot == slot;

        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0f, 0.5f));
        ImGui.PushStyleColor(ImGuiCol.Text, selected ? Ui.Green : Ui.Grey);

        // AllowOverlap, because the rest of the row is boxes drawn over this one: without it the
        // line under them takes the mouse and the name and coordinates cannot be reached at all.
        bool clicked = ImGui.Selectable(
            selected ? $"> {slot}" : slot.ToString(),
            selected,
            ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick
                | ImGuiSelectableFlags.AllowOverlap,
            new Vector2(0f, rowHeight));

        ImGui.PopStyleColor();
        ImGui.PopStyleVar();

        if (clicked && ImGui.IsMouseDoubleClicked(0)) state.Run(() => state.Client.PosSelectAsync(slot));
    }

    /// <summary>
    /// The name, which is this PC's alone: the console knows nothing about it, so the box writes
    /// the game's file on Enter or on being left and nothing crosses the wire. An empty slot gets
    /// the same box, because labelling a slot before saving into it is how a route is laid out.
    /// </summary>
    private static void DrawNameCell(AppState state, GameId game, byte planet, byte slot)
    {
        var key = (planet, slot);
        string name = NameDrafts.TryGetValue(key, out var draft)
            ? draft
            : game == GameId.None ? string.Empty : state.PositionNames.Name(game, planet, slot);

        ImGui.SetNextItemWidth(-1);
        Ui.PushTableInput();
        bool entered = ImGui.InputTextWithHint("##name", "unnamed", ref name, NameLength,
            ImGuiInputTextFlags.EnterReturnsTrue);
        Ui.PopTableInput();
        bool active = ImGui.IsItemActive();
        bool committed = entered || ImGui.IsItemDeactivatedAfterEdit();

        if (active) NameDrafts[key] = name;
        else NameDrafts.Remove(key);

        if (!committed || game == GameId.None) return;

        NameDrafts.Remove(key);
        if (string.Equals(name.Trim(), state.PositionNames.Name(game, planet, slot), StringComparison.Ordinal)) return;

        try
        {
            state.PositionNames.Set(game, planet, slot, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.AddToast($"Slot names not saved: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>
    /// One coordinate, as a box that writes it back. True, with the number, when a coordinate was
    /// typed and committed; a parse failure toasts and returns false, so nothing is sent for text
    /// that is not a number.
    /// </summary>
    private static bool TryCoordinateCell(AppState state, byte slot, int axis, float value, out float typed)
    {
        typed = 0f;

        var key = (slot, axis);
        string text = CoordinateDrafts.TryGetValue(key, out var draft) ? draft : CoordinateText(value);

        ImGui.SetNextItemWidth(-1);
        Ui.PushTableInput();
        bool entered = ImGui.InputText($"##axis{axis}", ref text, 24,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsScientific);
        Ui.PopTableInput();
        bool active = ImGui.IsItemActive();

        if (active) CoordinateDrafts[key] = text;
        else CoordinateDrafts.Remove(key);

        if (!entered) return false;

        CoordinateDrafts.Remove(key);

        if (TryParseCoordinate(text, out typed)) return true;

        state.AddToast($"'{text.Trim()}' is not a coordinate", ToastKind.Error);
        return false;
    }

    /// <summary>
    /// POS_EDIT for one slot: the three coordinates together, because the op takes all three, and
    /// the two the user did not touch are the ones the last POS_LIST reported. The rest of the
    /// slot's blob - the rotation, the camera - is the console's and is left where it is.
    /// </summary>
    private static void Edit(AppState state, byte slot, float x, float y, float z)
    {
        state.Run(async () =>
        {
            await state.Client.PosEditAsync(slot, x, y, z).ConfigureAwait(false);
            state.Post(() => state.RefreshPositions());
        });
    }

    /// <summary>
    /// What a row does to the console. No Select of its own: the quick block's slot dropdown and
    /// this row's double-click are where the console's selected slot is picked.
    /// </summary>
    private static void DrawActions(AppState state, PositionSlot slot)
    {
        byte index = slot.Slot;

        if (ImGui.SmallButton("Save"))
        {
            state.Run(async () =>
            {
                await state.Client.PosSaveAsync(index);
                state.Post(() => state.RefreshPositions());
            });
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!slot.Filled);
        if (ImGui.SmallButton("Load")) state.Run(() => state.Client.PosLoadAsync(index));
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
        {
            state.Run(async () =>
            {
                await state.Client.PosClearAsync(index);
                state.Post(() => state.RefreshPositions());
            });
        }

        ImGui.EndDisabled();
    }

    private static string PlanetName(AppState state, byte index) =>
        index < state.Planets.Length ? state.Planets[index] : "unknown";
}
