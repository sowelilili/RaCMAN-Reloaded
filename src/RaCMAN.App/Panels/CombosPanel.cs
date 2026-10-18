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
        ComboAction.OpenConsoleMenu,
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

    /// <summary>
    /// Whether a combo's action is out of reach: the set-aside load goes through the savefile
    /// helper, so it follows <see cref="SaveFilesPanel.HelperIn"/>. Every other combo is the
    /// console's own and always works.
    /// </summary>
    public static bool SetAsideComboBlocked(ComboAction action, SessionInfo session, SaveFileInfo info) =>
        action == ComboAction.LoadSetAsideFile && !SaveFilesPanel.HelperIn(session, info);

    /// <summary>
    /// Whether the console menu's combo has nothing to open: the menu is drawn by code qwark
    /// writes into the game, which RPCS3 does not take, so qwark-rpcs3 has no menu at all.
    /// </summary>
    public static bool MenuComboBlocked(ComboAction action, SessionInfo session) =>
        action == ComboAction.OpenConsoleMenu && session.IsEmulator;

    /// <summary>The first qwark build with the console menu and its combo, revision 1.22.</summary>
    public const byte ConsoleMenuBuild = 54;

    /// <summary>
    /// Whether the panel draws a row for the action: the console menu's only against a module
    /// that has it, since an older one answers its COMBO_SET with BAD_ARG.
    /// </summary>
    public static bool ShowsRow(ComboAction action, byte qwarkBuild) =>
        action != ComboAction.OpenConsoleMenu || qwarkBuild >= ConsoleMenuBuild;

    /// <summary>What the row says about itself when the pointer rests on it.</summary>
    public static string? Tooltip(ComboAction action) => action switch
    {
        ComboAction.OpenConsoleMenu =>
            "Opens the RaCMAN menu on the TV, in Deadlocked for now. It works even when the combos are switched off.",
        _ => null,
    };

    public static string Label(ComboAction action) => action switch
    {
        ComboAction.SavePosition => "Save position",
        ComboAction.LoadPosition => "Load position",
        ComboAction.Die => "Die",
        ComboAction.LoadPlanet => "Load planet",
        ComboAction.LoadSetAsideFile => "Load set-aside file",
        ComboAction.OpenConsoleMenu => "Open console menu",
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
            if (!ShowsRow(action, state.Session.QwarkVersion)) continue;

            ImGui.TableNextRow();
            ImGui.PushID((int)action);

            ImGui.TableNextColumn();

            // The set-aside combo runs the savefile helper on the console, which qwark refuses under
            // RPCS3 until qwark's patches have put the helper in the game. The row says so, greyed
            // with the Save files panel's reason, and stays editable, as the list does while the
            // combos are switched off: a combo recorded now fires once the helper is in.
            if (SetAsideComboBlocked(action, state.Session, state.SaveFile))
            {
                Ui.TextDisabled(Label(action));
                if (ImGui.IsItemHovered()) Ui.SetTooltip(Ui.NeedsSaveFileHelper);
            }
            else if (MenuComboBlocked(action, state.Session))
            {
                Ui.TextDisabled(Label(action));
                if (ImGui.IsItemHovered()) Ui.SetTooltip("The console menu is not available under RPCS3.");
            }
            else
            {
                ImGui.TextUnformatted(Label(action));
                if (Tooltip(action) is { } tip && ImGui.IsItemHovered()) Ui.SetTooltip(tip);
            }

            ImGui.TableNextColumn();
            uint mask = current.GetValueOrDefault(action);
            if (_capturing == action)
            {
                uint held = Capture.Captured;
                Ui.Text(Ui.Yellow, held == 0
                    ? "Press a combo on the pad..."
                    : $"{PadButtons.Describe(held)}, release to store");
            }
            else
            {
                Ui.Text(mask == 0 ? Ui.Grey : Ui.Green, mask == 0 ? "disabled" : $"{PadButtons.Describe(mask)}  (0x{mask:X})");
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

            // The console menu's combo is the one with a mask of its own; Clear turns it off, and
            // this puts the console's default back.
            if (action == ComboAction.OpenConsoleMenu && mask != ComboDefaults.OpenConsoleMenu)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Default"))
                {
                    state.Run(async () =>
                    {
                        await state.Client.ComboSetAsync(ComboAction.OpenConsoleMenu, ComboDefaults.OpenConsoleMenu);
                        state.Post(() => state.RefreshCombos());
                    });
                }
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
