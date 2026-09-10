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

        if (state.Planets.Length == 0)
        {
            Ui.Hint("This game has no planet list.");
        }
        else
        {
            // The combo shows the planets the game has; the index behind the pick is the one the
            // console numbered it with, filler entries included, because that is what a request
            // carries. A selection left on a hidden entry moves to the first real one.
            var choices = PlanetChoices.For(state.Planets);
            int pick = Math.Max(0, choices.PositionOf(_selectedPlanet));
            _selectedPlanet = choices.PlanetAt(pick);

            ImGui.SetNextItemWidth(260);
            if (ImGui.Combo("Planet", ref pick, choices.Labels, choices.Count))
            {
                _selectedPlanet = choices.PlanetAt(pick);
            }

            // Only the games that do these two on the way into a planet are offered them.
            var boxes = PlanetResetOptions.For(state.DescribedGame, state.LevelFlagsUnsupported);
            if (boxes.LevelFlags)
            {
                _resetLevelFlags = (session.PlanetFlags & PlanetFlags.ResetLevelFlags) != 0 || _resetLevelFlags;
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
        }

        ImGui.Spacing();
        if (ImGui.Button("Die")) state.Run(() => state.Client.DieAsync());

        ImGui.EndDisabled();
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
