using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

public static class CombosPanel
{
    private static ComboAction? _capturing;
    private static uint _captured;

    private static readonly ComboAction[] Actions =
    {
        ComboAction.SavePosition,
        ComboAction.LoadPosition,
        ComboAction.Die,
        ComboAction.LoadPlanet,
        ComboAction.LoadSetAsideFile,
    };

    /// <summary>A capture in flight must not survive a game or connection change.</summary>
    public static void Reset()
    {
        _capturing = null;
        _captured = 0;
    }

    public static string Label(ComboAction action) => action switch
    {
        ComboAction.SavePosition => "Save position",
        ComboAction.LoadPosition => "Load position",
        ComboAction.Die => "Die",
        ComboAction.LoadPlanet => "Load planet",
        ComboAction.LoadSetAsideFile => "Load set-aside file",
        _ => action.ToString(),
    };

    /// <summary>
    /// Capture watches the pad mask in telemetry: the first non-zero mask is held until the pad
    /// returns to 0, which is exactly when qwark re-arms a combo.
    /// </summary>
    public static void Update(AppState state)
    {
        if (_capturing is not { } action) return;

        uint mask = state.Session.PadMask;
        if (mask != 0)
        {
            _captured = mask;
            return;
        }

        if (_captured == 0) return;

        uint value = _captured;
        _captured = 0;
        _capturing = null;
        state.Run(async () =>
        {
            await state.Client.ComboSetAsync(action, value);
            state.Post(state.RefreshCombos);
        }, $"{Label(action)} = {PadButtons.Describe(value)}");
    }

    public static void Draw(AppState state)
    {
        Ui.Heading("Controller combos");
        Ui.Hint("The console watches the pad and fires a combo when exactly those buttons are held, re-arming once they are released.");

        if (!state.Connected)
        {
            Ui.Hint("Connect to configure combos.");
            return;
        }

        var current = state.Combos.ToDictionary(c => c.Action, c => c.Mask);

        if (ImGui.SmallButton("Refresh")) state.RefreshCombos();
        ImGui.Spacing();

        if (!ImGui.BeginTable("combos", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return;

        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn("Combo");
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 180);
        ImGui.TableHeadersRow();

        foreach (var action in Actions)
        {
            ImGui.TableNextRow();
            ImGui.PushID((int)action);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Label(action));

            ImGui.TableNextColumn();
            uint mask = current.GetValueOrDefault(action);
            if (_capturing == action)
            {
                ImGui.TextColored(Ui.Yellow, _captured == 0
                    ? "Press a combo on the pad..."
                    : $"{PadButtons.Describe(_captured)} — release to store");
            }
            else
            {
                ImGui.TextColored(mask == 0 ? Ui.Grey : Ui.Green, mask == 0 ? "disabled" : $"{PadButtons.Describe(mask)}  (0x{mask:X})");
            }

            ImGui.TableNextColumn();
            if (_capturing == action)
            {
                if (ImGui.SmallButton("Cancel"))
                {
                    _capturing = null;
                    _captured = 0;
                }
            }
            else if (ImGui.SmallButton("Capture"))
            {
                _capturing = action;
                _captured = 0;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Clear"))
            {
                var target = action;
                state.Run(async () =>
                {
                    await state.Client.ComboSetAsync(target, 0);
                    state.Post(state.RefreshCombos);
                });
            }

            ImGui.PopID();
        }

        ImGui.EndTable();

        ImGui.Spacing();
        ImGui.TextColored(Ui.Grey, $"Live pad mask: 0x{state.Session.PadMask:X4}  {PadButtons.Describe(state.Session.PadMask)}");
    }
}
