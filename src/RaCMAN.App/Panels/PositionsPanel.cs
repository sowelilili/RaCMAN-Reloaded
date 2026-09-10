using System.Globalization;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

public static class PositionsPanel
{
    private static int _selectedPlanet;
    private static bool _resetLevelFlags;
    private static bool _resetSpecialBolts;

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

    /// <summary>
    /// One coordinate as the panel shows it: two decimals, right-aligned in
    /// <see cref="CoordinateWidth"/>. Null is an empty slot, which gets the same width so the
    /// column below it stays a column.
    /// </summary>
    public static string Coordinate(float? value) =>
        (value is { } number ? number.ToString("0.00", CultureInfo.InvariantCulture) : "-")
        .PadLeft(CoordinateWidth);

    public static void Draw(AppState state)
    {
        Ui.Heading("Positions and planets");

        var session = state.Session;
        bool enabled = state.Ingame;
        if (!enabled) Ui.Warning($"Position and planet commands need INGAME (state is {session.State}).");

        ImGui.TextUnformatted($"Current planet: {PlanetName(state, session.CurrentPlanet)} ({session.CurrentPlanet})");
        ImGui.TextUnformatted($"Position: {Coordinate(session.PosX)}, {Coordinate(session.PosY)}, {Coordinate(session.PosZ)}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh slots")) state.RefreshPositions();

        ImGui.BeginDisabled(!enabled);
        ImGui.Spacing();

        DrawSlots(state, session);

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Planets");

        if (!PlanetLoadControls(state)) Ui.Hint("This game has no planet list.");

        ImGui.Spacing();
        if (ImGui.Button("Die")) state.Run(() => state.Client.DieAsync());

        ImGui.EndDisabled();
    }

    /// <summary>
    /// The planet controls: the combo with the filler names left out, the reset boxes the running
    /// game has, and Select and Load planet. The Game page's quick block draws the same block, so
    /// this is one helper both panels call rather than two copies that drift apart, and the planet
    /// picked on one page is the planet the other one shows. False when the game named no planets,
    /// which draws nothing at all: the quick block leaves out what a game has not got, and this
    /// panel says so in its own words.
    /// </summary>
    public static bool PlanetLoadControls(AppState state)
    {
        if (state.Planets.Length == 0) return false;

        // The combo shows the planets the game has; the index behind the pick is the one the
        // console numbered it with, filler entries included, because that is what a request
        // carries. A selection left on a hidden entry moves to the first real one.
        var choices = PlanetChoices.For(state.Planets);
        int pick = Math.Max(0, choices.PositionOf(_selectedPlanet));
        _selectedPlanet = choices.PlanetAt(pick);

        ImGui.SetNextItemWidth(FittedComboWidth("Planet"));
        if (ImGui.Combo("Planet", ref pick, choices.Labels, choices.Count))
        {
            _selectedPlanet = choices.PlanetAt(pick);
        }

        // Only the games that do these two on the way into a planet are offered them.
        var boxes = PlanetResetOptions.For(state.DescribedGame, state.LevelFlagsUnsupported);
        if (boxes.LevelFlags)
        {
            _resetLevelFlags = (state.Session.PlanetFlags & PlanetFlags.ResetLevelFlags) != 0 || _resetLevelFlags;
            ImGui.Checkbox("Reset level flags", ref _resetLevelFlags);
        }
        else
        {
            _resetLevelFlags = false;
        }

        if (boxes.SpecialBolts)
        {
            if (boxes.LevelFlags) ImGui.SameLine();
            ImGui.Checkbox("Reset special bolts", ref _resetSpecialBolts);
        }
        else
        {
            _resetSpecialBolts = false;
        }

        if (ImGui.Button("Select"))
        {
            byte planet = (byte)_selectedPlanet;
            var flags = (_resetLevelFlags ? PlanetFlags.ResetLevelFlags : 0)
                        | (_resetSpecialBolts ? PlanetFlags.ResetSpecialBolts : 0);
            state.Run(() => state.Client.PlanetSelectAsync(planet, flags));
        }

        ImGui.SameLine();
        if (ImGui.Button("Load planet"))
        {
            byte planet = (byte)_selectedPlanet;
            byte flags = (byte)((_resetLevelFlags ? 1 : 0) | (_resetSpecialBolts ? 2 : 0));
            state.Run(() => state.Client.PlanetLoadAsync(planet, flags));
        }

        return true;
    }

    /// <summary>
    /// The console's selected position slot as a dropdown: picking one sends POS_SELECT, which is
    /// exactly what the slot table's Select button does per row. The Game page's quick block draws
    /// it beside the save and load buttons, so the slot those two act on is chosen in the same
    /// place; the console owns the selection, so what the box shows is what telemetry reports.
    /// </summary>
    public static void SlotPicker(AppState state)
    {
        var slots = state.Positions.Slots;
        byte selected = state.Session.SelectedSlot;
        var labels = SlotLabels(state.Positions, selected);
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
    /// What the slot dropdown offers: one entry per slot the console listed, saying which of them
    /// already hold a position. A game whose slots have not been read yet (outside INGAME, or
    /// before the first POS_LIST) still names the selected one, so the box says what the console
    /// last told us rather than going blank.
    /// </summary>
    public static string[] SlotLabels(PositionList positions, byte selected)
    {
        var slots = positions.Slots;
        if (slots.Length == 0) return new[] { $"Slot {selected}" };

        var labels = new string[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            labels[i] = slots[i].Filled ? $"Slot {slots[i].Slot} (saved)" : $"Slot {slots[i].Slot}";
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

    private static void DrawSlots(AppState state, SessionInfo session)
    {
        var positions = state.Positions;
        if (positions.Slots.Length == 0)
        {
            Ui.Hint(!state.Connected ? "Connect to read the position slots."
                : !state.Ingame ? $"Reading the position slots needs INGAME (state is {session.State})."
                : "No positions saved for this planet.");
            return;
        }

        if (!ImGui.BeginTable("slots", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return;

        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Filled", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("X");
        ImGui.TableSetupColumn("Y");
        ImGui.TableSetupColumn("Z");
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 240);
        ImGui.TableHeadersRow();

        foreach (var slot in positions.Slots)
        {
            ImGui.TableNextRow();
            ImGui.PushID(slot.Slot);

            ImGui.TableNextColumn();
            bool selected = session.SelectedSlot == slot.Slot;
            ImGui.TextColored(selected ? Ui.Green : Ui.Grey, selected ? $"> {slot.Slot}" : slot.Slot.ToString());

            ImGui.TableNextColumn();
            ImGui.TextColored(slot.Filled ? Ui.Green : Ui.Grey, slot.Filled ? "yes" : "-");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Coordinate(slot.Filled ? slot.X : null));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Coordinate(slot.Filled ? slot.Y : null));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Coordinate(slot.Filled ? slot.Z : null));

            ImGui.TableNextColumn();
            byte index = slot.Slot;
            if (ImGui.SmallButton("Select")) state.Run(() => state.Client.PosSelectAsync(index));
            ImGui.SameLine();
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
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static string PlanetName(AppState state, byte index) =>
        index < state.Planets.Length ? state.Planets[index] : "unknown";
}
