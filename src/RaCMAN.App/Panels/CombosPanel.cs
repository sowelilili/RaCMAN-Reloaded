using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

public static class CombosPanel
{
    /// <summary>
    /// The capture in flight: which row, how it is committed and the pad watcher behind it. It is
    /// the state's (<see cref="AppState.ComboCapture"/>) rather than the panel's, because it holds
    /// that client's console's combos off and only that client's session change may drop it.
    /// </summary>
    public sealed class CaptureInFlight
    {
        /// <summary>The row being captured; null while none is. Only one row captures at a time.</summary>
        internal ComboAction? Action;

        /// <summary>
        /// Whether the capture in flight is committed as the active profile's override
        /// (COMBO_OVERRIDE) rather than as the shared combo (COMBO_SET).
        /// </summary>
        internal bool AsOverride;

        /// <summary>The pad watcher behind the Capture button.</summary>
        internal readonly ComboCapture Pad = new();
    }

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
    public static void BeginCapture(AppState state, ComboAction action, bool asOverride = false)
    {
        var capture = state.ComboCapture;
        capture.Action = action;
        capture.AsOverride = asOverride;
        capture.Pad.Reset();
        Suspend(state, true);
    }

    /// <summary>Drops a capture in flight and hands the console's combos back. Idempotent.</summary>
    public static void EndCapture(AppState state)
    {
        var capture = state.ComboCapture;
        if (capture.Action is null)
        {
            capture.Pad.Reset();
            return;
        }

        capture.Action = null;
        capture.Pad.Reset();
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
    /// Whether the console menu's combo has nothing to open: the running game's module says its
    /// Trainer cannot come up in this game on this platform (DESCRIBE's extension, menu_flags bit0,
    /// revision 1.34), the In-game menus switch's own rule (<see cref="QwarkSettings.InGameMenusBlocked"/>).
    /// Under RPCS3 that is the four original games, whose menu is code qwark writes into the game;
    /// Tools of Destruction's Trainer is its pause menu and comes up there too.
    /// </summary>
    public static bool MenuComboBlocked(ComboAction action, SessionInfo session, DescribeResult describe) =>
        action == ComboAction.OpenConsoleMenu && QwarkSettings.InGameMenusBlocked(session, describe);

    /// <summary>What a greyed console menu row, or Trainer page row, says on hover.</summary>
    public const string MenuUnavailable = "The console menu is not available under RPCS3.";

    /// <summary>
    /// Whether the fast-loads combo has nothing to flip: the Fast loads cheat patches game code,
    /// so under RPCS3 it needs qwark's patches, as the Fast loads box on the Game panel does, and
    /// the console refuses the combo the same way until they are in.
    /// </summary>
    public static bool FastLoadsComboBlocked(ComboAction action, SessionInfo session) =>
        action == ComboAction.ToggleFastLoads && session.CodeFeaturesUnavailable;

    /// <summary>
    /// The same, knowing the running game's DESCRIBE: a game whose Fast loads toggle is a data
    /// write (no WRITES_CODE) needs no patch, so its combo is never greyed. Every game's is a code
    /// patch today, Tools of Destruction's too since qwark revision 1.29.
    /// </summary>
    public static bool FastLoadsComboBlocked(ComboAction action, SessionInfo session, DescribeResult describe)
    {
        if (!FastLoadsComboBlocked(action, session)) return false;
        foreach (var feature in describe.Features)
        {
            if (feature.Kind == FeatureKind.Toggle && feature.Label == "Fast loads") return feature.WritesCode;
        }

        return true;
    }

    /// <summary>The first qwark build with the console menu and its combo, revision 1.22.</summary>
    public const byte ConsoleMenuBuild = 54;

    /// <summary>
    /// The first qwark build with the fast-loads combo and the In-game menus switch (MENU_HELP),
    /// revision 1.23.
    /// </summary>
    public const byte FastLoadsComboBuild = 55;

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
    /// The same, for the session: a game with no console menu (SessionInfo <c>flags2</c> bit7
    /// NO_MENU, Tools of Destruction) has no row for the menu's combo either, since it opens
    /// nothing there. The combo itself is the console's and stays stored for the other games.
    /// </summary>
    public static bool ShowsRow(ComboAction action, SessionInfo session) =>
        ShowsRow(action, session.QwarkVersion)
        && !(action == ComboAction.OpenConsoleMenu && session.NoConsoleMenu);

    /// <summary>
    /// What the row says about itself when the pointer rests on it. The console menu's combo is
    /// the one with a mask out of the box, and its tooltip names it, so a user who cleared or
    /// changed it can capture it again by hand: there is no Default button any more.
    /// </summary>
    public static string? Tooltip(ComboAction action) => action switch
    {
        ComboAction.OpenConsoleMenu =>
            $"Opens the Trainer menu in-game. Default: {PadButtons.Describe(ComboDefaults.OpenConsoleMenu)}.",
        _ => null,
    };

    /// <summary>
    /// Whether the rows get their Override button: per-user settings exist on this console
    /// (SessionInfo byte 29 bit1, never under RPCS3), the switch is on and a user's profile is
    /// active. Without an active profile COMBO_OVERRIDE can only answer NOT_FOUND.
    /// </summary>
    public static bool ShowsOverride(SessionInfo session) =>
        ShowsProfiles(session) && session.ProfilesOn && session.ProfileActive;

    /// <summary>The Override button is on the rows that have none yet; an overridden row's Capture re-captures it.</summary>
    public static bool ShowsOverrideButton(SessionInfo session, ComboEntry entry) =>
        ShowsOverride(session) && !IsOverridden(session, entry);

    /// <summary>
    /// Whether a row is this user's own combo: COMBO_LIST flags it, and only while the console has
    /// per-user settings at all. A module without them (a release of build 56, PROTOCOL.md 3.1.2)
    /// never flags a row; if one did, the row is still drawn and cleared as a shared one, so
    /// nothing here sends COMBO_OVERRIDE to it.
    /// </summary>
    public static bool IsOverridden(SessionInfo session, ComboEntry entry) =>
        ShowsProfiles(session) && entry.Overridden;

    /// <summary>What one press of Clear sends, by row.</summary>
    public enum ClearRequest
    {
        /// <summary>COMBO_OVERRIDE(action, 0): only this user's override goes; the shared combo shows again.</summary>
        RemoveOverride,

        /// <summary>COMBO_SET(action, 0): the shared combo goes, as before per-user settings.</summary>
        ClearShared,
    }

    /// <summary>
    /// Clear on an overridden row removes the override; on any other row it clears the shared
    /// combo. So two presses on an overridden row clear the combo completely: the second press
    /// finds the row plain.
    /// </summary>
    public static ClearRequest ClearFor(SessionInfo session, ComboEntry entry) =>
        IsOverridden(session, entry) ? ClearRequest.RemoveOverride : ClearRequest.ClearShared;

    /// <summary>
    /// What a row's Clear sends, for the row's own action: COMBO_OVERRIDE(action, 0) on an overridden
    /// row, COMBO_SET(action, 0) on any other (<see cref="ClearFor"/>).
    /// </summary>
    public static Task ClearAsync(QwarkClient client, SessionInfo session, ComboEntry entry) =>
        ClearFor(session, entry) == ClearRequest.RemoveOverride
            ? client.ComboOverrideClearAsync(entry.Action)
            : client.ComboSetAsync(entry.Action, 0);

    /// <summary>Whether Capture on a row commits as the override (COMBO_OVERRIDE) rather than the shared combo (COMBO_SET).</summary>
    public static bool CaptureIsOverride(SessionInfo session, ComboEntry entry) => IsOverridden(session, entry);

    /// <summary>
    /// Whether the line under the switch is drawn: only while overrides can be made and at least
    /// one row the table draws is this user's own (orange). Overrides being possible is not
    /// enough; with none made, every row is green and the line would say something untrue.
    /// </summary>
    public static bool ShowsOverrideHint(SessionInfo session, IEnumerable<ComboEntry> combos) =>
        ShowsOverride(session)
        && combos.Any(entry => IsOverridden(session, entry) && ShowsRow(entry.Action, session));

    /// <summary>The line under the switch while this user has an overridden combo.</summary>
    public static string OverrideHint(ProfileInfo profile) =>
        $"{UserName(profile)} has one or more overridden combos (orange).";

    /// <summary>
    /// How wide the buttons cell's buttons are. <see cref="First"/> is the row's first button:
    /// Capture, Cancel while it captures, or Override on an overridden row, which captures the
    /// override again; all three are one width, so the button after it starts at the same place on
    /// every row. <see cref="Rest"/> is what follows it on a row with an Override button (Clear,
    /// a space, Override), which is the width of an overridden row's Clear Override, so its right
    /// edge meets the plain rows' Override. <see cref="Column"/> is the cell itself.
    /// </summary>
    public readonly record struct ButtonWidths(float First, float Rest, float Column);

    /// <summary>
    /// The buttons cell's widths, from the labels as the font draws them (<paramref name="measure"/>
    /// is ImGui.CalcTextSize in the panel and a stand-in in the tests), the style's frame padding,
    /// which a button adds each side of its label, and its item spacing, which goes between two
    /// buttons on a line. <paramref name="withOverride"/> is whether any row has a third button
    /// or a Clear Override; without one the cell is Capture and Clear, and the Combo column keeps
    /// the rest.
    /// </summary>
    public static ButtonWidths ButtonWidthsFor(Func<string, float> measure, float framePadding, float spacing, bool withOverride)
    {
        float Button(string label) => measure(label) + (framePadding * 2f);

        float first = MathF.Max(Button("Capture"), MathF.Max(Button("Cancel"), Button("Override")));
        float clear = Button("Clear");
        float rest = withOverride
            ? MathF.Max(clear + spacing + Button("Override"), Button("Clear Override"))
            : clear;
        return new ButtonWidths(first, rest, MathF.Ceiling(first + spacing + rest));
    }

    /// <summary>
    /// A SmallButton of a set width: the same height and padding as ImGui.SmallButton, which has
    /// no width of its own to give.
    /// </summary>
    private static bool SizedSmallButton(string label, float width)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, 0f));
        bool pressed = ImGui.Button(label, new Vector2(width, 0f));
        ImGui.PopStyleVar();
        return pressed;
    }

    /// <summary>
    /// The row's entry from the last COMBO_LIST, or a plain one with no mask when the console
    /// listed nothing for it.
    /// </summary>
    private static ComboEntry EntryFor(AppState state, ComboAction action)
    {
        foreach (var entry in state.Combos)
        {
            if (entry.Action == action) return entry;
        }

        return new ComboEntry(action, 0);
    }

    /// <summary>
    /// A combo's name as the panel and its toasts say it: a Trainer page's combo by the page the
    /// module named ("Open Cheats page"), every other one by <see cref="Label(ComboAction)"/>.
    /// </summary>
    public static string Label(IEnumerable<ComboEntry> combos, ComboAction action)
    {
        foreach (var entry in combos)
        {
            if (entry.Action == action && entry.Page is { } page) return PageLabel(page);
        }

        return Label(action);
    }

    /// <summary>A Trainer page's combo, named for its page.</summary>
    public static string PageLabel(string page) => $"Open {page} page";

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
        var capture = state.ComboCapture;
        if (capture.Action is not { } action) return;
        if (capture.Pad.Feed(state.Session.PadMask) is not { } value) return;

        capture.Action = null;
        bool asOverride = capture.AsOverride;
        string name = Label(state.Combos, action);
        if (asOverride && !ShowsOverride(state.Session))
        {
            // The per-user settings went away under the capture: the switch went off, or the
            // console has none. There is no user to store it for, and COMBO_OVERRIDE is not sent.
            Suspend(state, false);
            state.AddToast($"{name} not stored: per-user settings are off");
            return;
        }

        state.Run(async () =>
        {
            if (asOverride) await state.Client.ComboOverrideAsync(action, value);
            else await state.Client.ComboSetAsync(action, value);
            // The pad is empty by now, which is why the capture committed, so handing the combos
            // back here cannot fire the one just stored: qwark waits for the next press.
            await state.Client.ComboSuspendAsync(false);
            state.Post(() => state.RefreshCombos());
        }, $"{name} = {PadButtons.Describe(value)}{(asOverride ? " (override)" : "")}");
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

        ImGui.BeginDisabled(state.ComboCapture.Action is not null);
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

        // Per-user settings on and a user's profile active (revision 1.24): each row can carry
        // this user's own combo, drawn in orange, over the shared one. The line saying so is
        // only there while one of the rows is.
        bool overrides = ShowsOverride(state.Session);
        if (ShowsOverrideHint(state.Session, state.Combos))
        {
            Ui.Hint(OverrideHint(state.Profile));
            ImGui.Spacing();
        }

        // The buttons cell is as wide as its buttons, measured afresh every frame so it follows the
        // font and the style; Combo stretches into whatever the two fixed columns leave.
        var style = ImGui.GetStyle();
        bool anyOverridden = state.Combos.Any(entry => IsOverridden(state.Session, entry));
        var widths = ButtonWidthsFor(text => ImGui.CalcTextSize(text).X, style.FramePadding.X, style.ItemSpacing.X,
            withOverride: overrides || anyOverridden);

        if (!ImGui.BeginTable("combos", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return;

        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn("Combo");
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, widths.Column);
        ImGui.TableHeadersRow();

        foreach (var action in Actions)
        {
            if (!ShowsRow(action, state.Session)) continue;

            var entry = EntryFor(state, action);

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
            else if (MenuComboBlocked(action, state.Session, state.Describe))
            {
                Ui.TextDisabled(Label(action));
                if (ImGui.IsItemHovered()) Ui.SetTooltip(MenuUnavailable);
            }
            else if (FastLoadsComboBlocked(action, state.Session, state.Describe))
            {
                Ui.TextDisabled(Label(action));
                if (ImGui.IsItemHovered()) Ui.SetTooltip(Ui.NeedsQwarkPatches);
            }
            else
            {
                ImGui.TextUnformatted(Label(action));
                if (Tooltip(action) is { } tip && ImGui.IsItemHovered()) Ui.SetTooltip(tip);
            }

            DrawCells(state, action, entry, widths, overrides);
            ImGui.PopID();
        }

        ImGui.EndTable();

        // Revision 1.34: the Trainer pages' own table, the pages with a combo and, after "More...",
        // the rest. A module before build 59 names no page and has neither.
        bool pageTable = TrainerPageTable.Shows(state.Combos);
        var pages = pageTable ? TrainerPageTable.Pages(state.Combos) : Array.Empty<ComboEntry>();
        if (pageTable) DrawPageTable(state, pages, widths, overrides);

        ImGui.Spacing();
        if (pageTable && PageTable.ButtonLabel(pages) is { } more)
        {
            if (ImGui.SmallButton(more)) PageTable.Toggle();
            Ui.Tooltip(PageTable.ButtonTooltip);
            ImGui.SameLine();
        }

        if (ImGui.SmallButton("Refresh")) state.RefreshCombos();

        // The capture reads the pad out of telemetry either way; the mask itself is only of use
        // while looking at the wire, so it goes with the rest of the debug detail.
        if (Ui.Debug)
        {
            ImGui.Spacing();
            Ui.DebugHint($"Live pad mask: 0x{state.Session.PadMask:X4}  {PadButtons.Describe(state.Session.PadMask)}");
        }

        // The console's own settings that were here (In-game menus, Run warnings, Per-user settings)
        // are on the Settings page's Qwark tab (QwarkSettings).
    }

    /// <summary>A row's Combo and buttons cells: the mask in force, Capture, Clear and Override.</summary>
    private static void DrawCells(AppState state, ComboAction action, ComboEntry entry, ButtonWidths widths, bool overrides)
    {
        bool overridden = IsOverridden(state.Session, entry);
        var inFlight = state.ComboCapture;

        ImGui.TableNextColumn();
        uint mask = entry.Mask;
        if (inFlight.Action == action)
        {
            uint held = inFlight.Pad.Captured;
            Ui.Text(Ui.Yellow, held == 0
                ? "Press a combo on the pad..."
                : $"{PadButtons.Describe(held)}, release to store");
        }
        else
        {
            Ui.Text(overridden ? Ui.Orange : mask == 0 ? Ui.Grey : Ui.Green, MaskText(mask, Ui.Debug));
            if (overridden && ImGui.IsItemHovered())
            {
                Ui.SetTooltip($"Overridden: this combo is only active for {UserName(state.Profile)}.");
            }
        }

        ImGui.TableNextColumn();
        if (inFlight.Action == action)
        {
            if (SizedSmallButton("Cancel", widths.First)) EndCapture(state);
        }
        else
        {
            // An overridden row's first button records the override again, and says so; a plain
            // row's Capture records the shared combo. The IDs differ from the plain rows' Override.
            string capture = overridden ? "Override##recapture" : "Capture";
            if (SizedSmallButton(capture, widths.First)) BeginCapture(state, action, CaptureIsOverride(state.Session, entry));
        }

        ImGui.SameLine();
        bool clear = overridden
            ? SizedSmallButton("Clear Override##clear", widths.Rest)
            : ImGui.SmallButton("Clear");
        if (clear)
        {
            var session = state.Session;
            state.Run(async () =>
            {
                await ClearAsync(state.Client, session, entry);
                state.Post(() => state.RefreshCombos());
            });
        }

        if (overridden && ImGui.IsItemHovered())
        {
            Ui.SetTooltip("Clear again to clear the shared combo.");
        }

        // Where the Default button was: a capture whose result is this user's own combo.
        if (inFlight.Action != action && overrides && ShowsOverrideButton(state.Session, entry))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Override")) BeginCapture(state, action, asOverride: true);
            if (ImGui.IsItemHovered())
            {
                Ui.SetTooltip($"Capture a combo for {UserName(state.Profile)} only.");
            }
        }
    }

    // ---------------------------------------------------------------- Trainer pages (revision 1.34)

    /// <summary>
    /// The Trainer pages' table under the action combos': which of its rows show, and its button.
    /// The window tells it which panel is open every frame (<see cref="TrainerPageTable.Observe"/>).
    /// </summary>
    public static readonly TrainerPageTable PageTable = new();

    /// <summary>
    /// The Trainer pages' table: a row for each page the module lists, laid out as the action combos'
    /// table, with the same widths so the two line up, and acting on its own page alone. Drawn only
    /// while it has a row: a page with a combo, or every page after "More...".
    /// </summary>
    private static void DrawPageTable(AppState state, IReadOnlyList<ComboEntry> pages, ButtonWidths widths, bool overrides)
    {
        var rows = PageTable.Rows(pages, state.ComboCapture.Action);
        if (rows.Count == 0) return;

        ImGui.Spacing();
        if (!ImGui.BeginTable("trainer-pages", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return;

        ImGui.TableSetupColumn(TrainerPageTable.Header, ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn("Combo");
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, widths.Column);
        ImGui.TableHeadersRow();

        bool greyed = TrainerPageTable.Greyed(state.Session, state.Describe);
        foreach (var page in rows)
        {
            ImGui.TableNextRow();
            ImGui.PushID((int)page.Action);
            ImGui.TableNextColumn();

            // A page opens the Trainer, so where the module says the Trainer cannot come up it has
            // nothing to open either.
            string label = TrainerPageTable.RowLabel(page);
            if (greyed)
            {
                Ui.TextDisabled(label);
                if (ImGui.IsItemHovered()) Ui.SetTooltip(MenuUnavailable);
            }
            else
            {
                ImGui.TextUnformatted(label);
                if (ImGui.IsItemHovered()) Ui.SetTooltip(TrainerPageTable.Tooltip(page));
            }

            DrawCells(state, page.Action, page, widths, overrides);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    // ---------------------------------------------------------------- per-user settings

    /// <summary>
    /// Whether anything about per-user settings is drawn: SessionInfo byte 29 bit1, the capability
    /// bit, and never under RPCS3, which has one user. Not the build number: the bit is the contract.
    /// </summary>
    public static bool ShowsProfiles(SessionInfo session) => session.ProfilesSupported && !session.IsEmulator;

    /// <summary>
    /// A console user as the panels name them: "User n". The module leaves PROFILE_INFO's name
    /// empty for a console user; a name is used only if one comes (named profiles, a later revision).
    /// "this user" before PROFILE_INFO has said who is signed in.
    /// </summary>
    public static string UserName(ProfileInfo profile) =>
        profile.Name.Length > 0 ? profile.Name
        : profile.SignedIn ? $"User {profile.UserId}"
        : "this user";

    /// <summary>"User 1's", for the lines that say whose something is.</summary>
    public static string Possessive(string name) => $"{name}'s";

    /// <inheritdoc cref="Possessive(string)"/>
    public static string PossessiveName(ProfileInfo profile) => Possessive(UserName(profile));
}
