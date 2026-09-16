using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

public static class CombosPanel
{
    private static ComboAction? _capturing;

    /// <summary>The pad watcher behind the Capture button; only one row captures at a time.</summary>
    private static readonly ComboCapture Capture = new();

    private static readonly ComboAction[] Actions =
    {
        ComboAction.SavePosition,
        ComboAction.LoadPosition,
        ComboAction.Die,
        ComboAction.LoadPlanet,
        ComboAction.LoadSetAsideFile,
    };

    /// <summary>A capture in flight must not survive a game or connection change.</summary>
    public static void Reset(AppState state) => EndCapture(state);

    /// <summary>
    /// Starts a capture on one row. The console is watching the same pad the capture reads out of
    /// telemetry, so the buttons the user is about to press would fire the combos already stored:
    /// COMBO_SUSPEND holds them off until the capture commits, is cancelled or is dropped.
    /// </summary>
    public static void BeginCapture(AppState state, ComboAction action)
    {
        _capturing = action;
        Capture.Reset();
        Suspend(state, true);
    }

    /// <summary>Drops a capture in flight and hands the console's combos back. Idempotent.</summary>
    public static void EndCapture(AppState state)
    {
        if (_capturing is null)
        {
            Capture.Reset();
            return;
        }

        _capturing = null;
        Capture.Reset();
        Suspend(state, false);
    }

    /// <summary>
    /// The console expires a hold by itself, so a disconnected client owes it nothing: there is
    /// nowhere to send the resume to, and the combos come back on their own.
    /// </summary>
    private static void Suspend(AppState state, bool on)
    {
        if (!state.Connected) return;
        state.Run(() => state.Client.ComboSuspendAsync(on));
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
    /// Feeds the telemetry pad mask to the capture in flight. <see cref="ComboCapture"/> keeps the
    /// fullest mask of the press and answers with it when the pad returns to 0, which is exactly
    /// when qwark re-arms a combo.
    /// </summary>
    public static void Update(AppState state)
    {
        if (_capturing is not { } action) return;
        if (Capture.Feed(state.Session.PadMask) is not { } value) return;

        _capturing = null;
        state.Run(async () =>
        {
            await state.Client.ComboSetAsync(action, value);
            // The pad is empty by now, which is why the capture committed, so handing the combos
            // back here cannot fire the one just stored: qwark waits for the next press.
            await state.Client.ComboSuspendAsync(false);
            state.Post(() => state.RefreshCombos());
        }, $"{Label(action)} = {PadButtons.Describe(value)}");
    }

    /// <summary>
    /// The console's own switch over every combo it holds, COMBO_ENABLE of revision 1.12. It is
    /// drawn from the session flag and never from anything remembered here, so the box always says
    /// what the console says: a click sends the request and the next packet moves it. The switch
    /// lives in the console's config, so it outlives this client and the console's next boot.
    /// <para>
    /// A capture already holds the combos off through COMBO_SUSPEND, so there is nothing for the
    /// switch to do while one is running and it is greyed out for the length of it. The list below
    /// stays editable whatever the switch says: a combo recorded now is one that fires when the
    /// combos are turned back on.
    /// </para>
    /// </summary>
    private static void DrawSwitch(AppState state)
    {
        bool off = state.Session.CombosOff;
        bool enabled = !off;

        ImGui.BeginDisabled(_capturing is not null);
        if (ImGui.Checkbox("Combos enabled", ref enabled))
        {
            bool on = enabled;
            state.Run(() => state.Client.ComboEnableAsync(on));
        }

        ImGui.EndDisabled();

        ImGui.Spacing();
    }

    public static void Draw(AppState state)
    {
        Ui.Heading("Controller combos");

        if (!state.Connected)
        {
            Ui.Hint("Connect to configure combos.");
            return;
        }

        DrawSwitch(state);

        var current = state.Combos.ToDictionary(c => c.Action, c => c.Mask);

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
                uint held = Capture.Captured;
                ImGui.TextColored(Ui.Yellow, held == 0
                    ? "Press a combo on the pad..."
                    : $"{PadButtons.Describe(held)}, release to store");
            }
            else
            {
                ImGui.TextColored(mask == 0 ? Ui.Grey : Ui.Green, mask == 0 ? "disabled" : $"{PadButtons.Describe(mask)}  (0x{mask:X})");
            }

            ImGui.TableNextColumn();
            if (_capturing == action)
            {
                if (ImGui.SmallButton("Cancel")) EndCapture(state);
            }
            else if (ImGui.SmallButton("Capture"))
            {
                BeginCapture(state, action);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Clear"))
            {
                var target = action;
                state.Run(async () =>
                {
                    await state.Client.ComboSetAsync(target, 0);
                    state.Post(() => state.RefreshCombos());
                });
            }

            ImGui.PopID();
        }

        ImGui.EndTable();

        ImGui.Spacing();
        if (ImGui.SmallButton("Refresh")) state.RefreshCombos();

        // The capture reads the pad out of telemetry either way; the mask itself is only of use
        // while looking at the wire, so it goes with the rest of the debug detail.
        if (Ui.Debug)
        {
            ImGui.Spacing();
            Ui.DebugHint($"Live pad mask: 0x{state.Session.PadMask:X4}  {PadButtons.Describe(state.Session.PadMask)}");
        }
    }
}
