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
        ComboAction.ToggleFastLoads,
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

    /// <summary>
    /// Whether the fast-loads combo has nothing to flip: the Fast loads cheat patches game code,
    /// so under RPCS3 it needs qwark's patches, as the Fast loads box on the Game panel does, and
    /// the console refuses the combo the same way until they are in.
    /// </summary>
    public static bool FastLoadsComboBlocked(ComboAction action, SessionInfo session) =>
        action == ComboAction.ToggleFastLoads && session.CodeFeaturesUnavailable;

    /// <summary>The first qwark build with the console menu and its combo, revision 1.22.</summary>
    public const byte ConsoleMenuBuild = 54;

    /// <summary>
    /// The first qwark build with the fast-loads combo and the In-game menus switch (MENU_HELP),
    /// revision 1.23.
    /// </summary>
    public const byte FastLoadsComboBuild = 55;

    /// <inheritdoc cref="FastLoadsComboBuild"/>
    public const byte InGameMenusBuild = 55;

    /// <summary>
    /// Whether the panel draws a row for the action: the console menu's and the fast-loads one only
    /// against a module that has them, since an older one answers their COMBO_SET with BAD_ARG.
    /// </summary>
    public static bool ShowsRow(ComboAction action, byte qwarkBuild) => action switch
    {
        ComboAction.OpenConsoleMenu => qwarkBuild >= ConsoleMenuBuild,
        ComboAction.ToggleFastLoads => qwarkBuild >= FastLoadsComboBuild,
        _ => true,
    };

    /// <summary>
    /// Whether the In-game menus box is drawn: only against a module that has MENU_HELP, which an
    /// older one answers UNKNOWN_OP.
    /// </summary>
    public static bool ShowsInGameMenus(byte qwarkBuild) => qwarkBuild >= InGameMenusBuild;

    /// <summary>
    /// Whether the In-game menus box is greyed: under RPCS3 there is no console menu, and qwark
    /// answers MENU_HELP UNSUPPORTED.
    /// </summary>
    public static bool InGameMenusBlocked(SessionInfo session) => session.IsEmulator || session.CodePatchesUnsupported;

    /// <summary>What the row says about itself when the pointer rests on it.</summary>
    public static string? Tooltip(ComboAction action) => action switch
    {
        ComboAction.OpenConsoleMenu =>
            "Opens the Trainer menu in-game.",
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
        ComboAction.ToggleFastLoads => "Toggle fast loads",
        _ => action.ToString(),
    };

    /// <summary>
    /// What a combo's cell says: its buttons, or "disabled" for mask 0. The hex of the bit-field
    /// goes after the buttons only with debug information on: it is of use while reading the wire
    /// and nothing else, and at the end of a six-button combo, the console menu's default, it ran
    /// past the edge of the column at the window's default width.
    /// </summary>
    public static string MaskText(uint mask, bool debug) =>
        mask == 0 ? "disabled"
        : debug ? $"{PadButtons.Describe(mask)}  (0x{mask:X})"
        : PadButtons.Describe(mask);

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

    /// <summary>
    /// The console's In-game menus switch, MENU_HELP of revision 1.23: whether the Trainer menu
    /// takes the place of Help in the game's pause menu. It sits right under the table's last row,
    /// the console menu's combo, which is the other way into that menu, and it is the only setting
    /// of the menu the client has. Drawn from the session flag, like the combo switch, so it always
    /// says what the console says: a click sends the request, the next packet moves the box, and
    /// the console swaps the row at once in play or as soon as the pause menu closes. Hidden
    /// against a module without the op, greyed under RPCS3, which has no console menu.
    /// </summary>
    private static void DrawInGameMenus(AppState state)
    {
        if (!ShowsInGameMenus(state.Session.QwarkVersion)) return;

        bool blocked = InGameMenusBlocked(state.Session);
        bool on = !state.Session.MenuHelpOff;

        ImGui.Spacing();
        ImGui.BeginDisabled(blocked);
        if (ImGui.Checkbox("In-game menus", ref on))
        {
            bool want = on;
            state.Run(() => state.Client.MenuHelpAsync(want));
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            Ui.SetTooltip(blocked
                ? "The console menu is not available under RPCS3."
                : "When enabled, shows the in-game Trainer menu in place of the Help menu. "
                  + "When disabled, the Trainer menu is only accessible with its button combo");
        }

        ImGui.SameLine();
        Ui.Text(Ui.Grey, InGameMenusStatus(state.Session));
    }

    /// <summary>What the In-game menus box says beside itself, from the session flags alone.</summary>
    public static string InGameMenusStatus(SessionInfo session) =>
        InGameMenusBlocked(session) ? "not under RPCS3"
        : session.MenuHelpOff ? "the game's default pause menu"
        : "Trainer replaces Help in the pause menu";

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
            else if (FastLoadsComboBlocked(action, state.Session))
            {
                Ui.TextDisabled(Label(action));
                if (ImGui.IsItemHovered()) Ui.SetTooltip(Ui.NeedsQwarkPatches);
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
                Ui.Text(mask == 0 ? Ui.Grey : Ui.Green, MaskText(mask, Ui.Debug));
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

        DrawInGameMenus(state);

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
