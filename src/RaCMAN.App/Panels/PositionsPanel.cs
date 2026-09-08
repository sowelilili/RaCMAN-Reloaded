using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

public static class PositionsPanel
{
    private static int _selectedPlanet;
    private static bool _resetLevelFlags;
    private static bool _resetSpecialBolts;

    public static void Draw(AppState state)
    {
        Ui.Heading("Positions and planets");

        var session = state.Session;
        bool enabled = state.Ingame;
        if (!enabled) Ui.Warning($"Position and planet commands need INGAME (state is {session.State}).");

        ImGui.Text($"Current planet: {PlanetName(state, session.CurrentPlanet)} ({session.CurrentPlanet})");
        ImGui.Text($"Position: {session.PosX:0.###}, {session.PosY:0.###}, {session.PosZ:0.###}");
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
            _selectedPlanet = Math.Clamp(_selectedPlanet, 0, state.Planets.Length - 1);
            ImGui.SetNextItemWidth(260);
            ImGui.Combo("Planet", ref _selectedPlanet, state.Planets, state.Planets.Length);

            _resetLevelFlags = (session.PlanetFlags & PlanetFlags.ResetLevelFlags) != 0 || _resetLevelFlags;
            ImGui.Checkbox("Reset level flags", ref _resetLevelFlags);
            ImGui.SameLine();
            ImGui.Checkbox("Reset special bolts", ref _resetSpecialBolts);

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

            ImGui.SameLine();
            if (ImGui.Button("Load selected on console"))
            {
                state.Run(() => state.Client.PlanetLoadAsync());
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
                : !state.Ingame ? $"Position slots are read out of game memory; the session is {session.State}, not INGAME."
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
            ImGui.TextUnformatted(slot.Filled ? slot.X.ToString("0.###") : "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.Filled ? slot.Y.ToString("0.###") : "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(slot.Filled ? slot.Z.ToString("0.###") : "-");

            ImGui.TableNextColumn();
            byte index = slot.Slot;
            if (ImGui.SmallButton("Select")) state.Run(() => state.Client.PosSelectAsync(index));
            ImGui.SameLine();
            if (ImGui.SmallButton("Save"))
            {
                state.Run(async () =>
                {
                    await state.Client.PosSaveAsync(index);
                    state.Post(state.RefreshPositions);
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
                    state.Post(state.RefreshPositions);
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
