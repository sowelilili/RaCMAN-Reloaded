using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// UNLOCK_LIST rendered as one tab per category, with a column for each value slot the running
/// game describes and at least one entry in the tab declares. The console names the slots and says
/// whether each is a flag or a number (revision 1.3), so the panel no longer assumes RaC1's
/// Owned/Gold/Level/Ammo for every game. Every edit is one UNLOCK_SET; the list is then re-read.
/// The name takes half the table and the value columns share the rest, so a game with four of them
/// still shows the names; the id the requests are keyed by has no column of its own.
///
/// Above the tabs sit the features the layout moved to <see cref="GameLayout.UnlocksSection"/>:
/// console-side actions that rewrite the whole table (UYA's weapon-level pair), which belong with
/// the list they change rather than on the Game page.
///
/// After the category tabs come the layout file's own, one per section it lists under
/// "unlocksTabs" (Collectables, in the shipped file) that the running game has something in. Their
/// bodies are <see cref="GamePanel.DrawSectionPage(AppState, string)"/>, the same code a sub-page of
/// the Game page is drawn with, so a section reads the same wherever the file puts it. The bulk
/// buttons, the search box and the table belong to a category tab and are not drawn in those.
///
/// Refresh policy: the whole list is re-read on the Settings panel's table refresh interval while
/// the panel is open, and again straight after any UNLOCK_SET. Re-reading everything is simpler
/// than patching one row back into the list and it keeps another client's edits visible. An
/// interval of zero leaves the Refresh button as the only thing that reads.
/// </summary>
public static class UnlocksPanel
{
    /// <summary>How wide the search box is.</summary>
    private const float SearchWidth = 200f;

    /// <summary>How much of the table the name column takes, whatever else the game describes.</summary>
    private const float NameWeight = 0.5f;

    private static bool _opened;
    private static float _sinceRefresh;

    /// <summary>Half-typed number edits, kept only while the box has focus.</summary>
    private static readonly Dictionary<(byte Id, int Field), string> Drafts = new();

    /// <summary>
    /// The name filter, shared by every category tab: the tables are long enough that hunting for
    /// one weapon by eye is the slow part, and a per-tab filter would just be forgotten in a tab
    /// the user is not looking at. Dropped when the game changes.
    /// </summary>
    private static string _filter = string.Empty;

    /// <summary>
    /// True once a tab has been pushed to the front for this list. The tab bar is often drawn
    /// before the unlock list has arrived, with only the layout's own tabs in it, and ImGui keeps
    /// whichever tab it saw first as the selected one; without this the panel opened on
    /// Collectables every time the categories came in a frame later. The first category tab
    /// takes the front once when it appears, unless a section was asked for by name.
    /// </summary>
    private static bool _frontTabChosen;

    public static void Reset()
    {
        _opened = false;
        _sinceRefresh = 0;
        _filter = string.Empty;
        _frontTabChosen = false;
        Drafts.Clear();
    }

    /// <summary>
    /// The list itself lives on <see cref="AppState.Unlocks"/> and is cleared there; this drops
    /// the half-typed edits and re-arms the fetch so the panel reads again when it comes back.
    /// </summary>
    public static void ClearData()
    {
        _opened = false;
        _sinceRefresh = 0;
        _frontTabChosen = false;
        Drafts.Clear();
    }

    public static void Draw(AppState state)
    {
        Ui.Heading("Unlocks");

        DrawSectionActions(state);

        // First frame on the panel: load the list.
        if (!_opened)
        {
            _opened = true;
            _sinceRefresh = 0;

            // Quiet outside INGAME: opening the panel between sessions is not an error worth a toast.
            if (state.Connected) state.RefreshUnlocks(quiet: !state.Ingame);
        }

        bool enabled = state.Ingame;

        // Only where nothing else reads the table: with an interval set on the Settings panel the
        // table is never more than that many seconds old, and the button had nothing to add.
        if (!state.Settings.AutoRefreshesTables)
        {
            ImGui.BeginDisabled(!state.Connected);
            if (ImGui.Button("Refresh"))
            {
                state.RefreshUnlocks();
                _sinceRefresh = 0;
            }

            ImGui.EndDisabled();
        }

        // The interval is read every frame, so a change on the Settings panel takes effect at once.
        float period = state.Settings.TableRefreshSeconds;
        if (period > 0 && state.Connected)
        {
            _sinceRefresh += ImGui.GetIO().DeltaTime;
            if (_sinceRefresh >= period)
            {
                _sinceRefresh = 0;
                state.RefreshUnlocks(quiet: true);
            }
        }
        else
        {
            _sinceRefresh = 0;
        }

        if (!enabled)
        {
            ImGui.TextColored(Ui.Yellow, $"Unlock edits need INGAME (state is {state.Session.State.DisplayName()}).");
        }

        if (state.UnlocksUnsupported)
        {
            Ui.Hint("This game has no unlock table.");
            return;
        }

        var list = state.Unlocks;

        // The layout's own tabs are features, not unlock rows, so they are there whatever the table
        // itself has in it: an empty table says why it is empty and the tabs are still drawn.
        var sections = GamePanel.UnlocksTabsWithContent(state);

        if (list.Unlocks.Length == 0)
        {
            Ui.Hint(!state.Connected ? "Connect to read the unlock list."
                : !enabled ? $"No unlock list: the session is {state.Session.State.DisplayName()}, not INGAME."
                : "The unlock list is empty.");

            if (sections.Count == 0) return;
        }

        ImGui.Spacing();

        if (!ImGui.BeginTabBar("unlock-categories")) return;

        DrawCategoryTabs(state, list, enabled);
        DrawSectionTabs(state, sections);

        ImGui.EndTabBar();
    }

    /// <summary>One tab per unlock category the running game declares an entry in.</summary>
    private static void DrawCategoryTabs(AppState state, UnlockList list, bool enabled)
    {
        if (list.Unlocks.Length == 0) return;

        int categories = Math.Max(list.Categories.Length, list.Unlocks.Max(u => u.Category) + 1);
        for (int category = 0; category < categories; category++)
        {
            var rows = list.Unlocks.Where(u => u.Category == category).ToArray();
            if (rows.Length == 0) continue;

            string name = category < list.Categories.Length ? list.Categories[category] : $"Category {category}";

            // The leftmost category is the panel's default tab, whatever the tab bar showed
            // before the list arrived.
            bool front = !_frontTabChosen;
            if (front) _frontTabChosen = true;

            bool open = front
                ? Ui.BeginTabItem($"{name}###category{category}", ImGuiTabItemFlags.SetSelected)
                : ImGui.BeginTabItem($"{name}###category{category}");
            if (!open) continue;

            ImGui.PushID(category);
            DrawCategory(state, list, rows, enabled);
            ImGui.PopID();
            ImGui.EndTabItem();
        }
    }

    /// <summary>
    /// The layout's own tabs, after the categories: one per section it lists under "unlocksTabs",
    /// drawn exactly as the Game page draws a sub-page of its own. A section named on the command
    /// line is selected for the one frame the request lives, which is how <c>--game-section</c>
    /// opens the window on a tab.
    /// </summary>
    private static void DrawSectionTabs(AppState state, IReadOnlyList<string> sections)
    {
        foreach (var section in sections)
        {
            var flags = ImGuiTabItemFlags.None;
            if (string.Equals(SubPageNav.Requested, section, StringComparison.Ordinal))
            {
                flags = ImGuiTabItemFlags.SetSelected;
                SubPageNav.Requested = null;

                // A tab asked for by name is the front tab; the categories must not take it
                // back when they arrive.
                _frontTabChosen = true;
            }

            if (!Ui.BeginTabItem($"{section}###section-{section}", flags)) continue;

            GamePanel.DrawSectionPage(state, section);
            ImGui.EndTabItem();
        }
    }

    /// <summary>
    /// The features the layout sent to the "Unlocks" section, drawn the way the Game page draws
    /// actions: equal-width buttons, two to a row. Each is one FEATURE_TRIGGER followed by a
    /// re-read, the same shape as the bulk unlock buttons inside a category. Only actions are
    /// expected here, so anything else the layout moved in is left alone.
    /// </summary>
    private static void DrawSectionActions(AppState state)
    {
        var actions = GamePanel.FeaturesInSection(state, GameLayout.UnlocksSection)
            .Where(f => f.Kind == FeatureKind.Action)
            .ToArray();

        if (actions.Length == 0) return;

        ImGui.BeginDisabled(!state.Ingame);

        int columns = actions.Length > 1 ? 2 : 1;
        if (ImGui.BeginTable("unlock-actions", columns, ImGuiTableFlags.SizingStretchSame))
        {
            foreach (var feature in actions)
            {
                ImGui.TableNextColumn();
                ImGui.PushID(feature.Id);
                if (ImGui.Button(feature.Label, new Vector2(-1, 0)))
                {
                    byte id = feature.Id;
                    state.Run(async () =>
                    {
                        await state.Client.FeatureTriggerAsync(id).ConfigureAwait(false);
                        state.Post(() => state.RefreshUnlocks());
                    });
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.EndDisabled();
        ImGui.Spacing();
    }

    /// <summary>
    /// The table's stretch weights, the name column first. The name takes half the table and the
    /// value columns share the other half evenly, because a fixed width per value column left UYA's
    /// four (Owned, Level, XP, Ammo) crowding the names off the left-hand side of the window.
    /// Proportions also mean the columns grow with the window rather than leaving it empty.
    /// </summary>
    public static float[] ColumnWeights(int valueColumns)
    {
        if (valueColumns <= 0) return new[] { 1f };

        var weights = new float[valueColumns + 1];
        weights[0] = NameWeight;
        for (int i = 1; i < weights.Length; i++) weights[i] = (1f - NameWeight) / valueColumns;

        return weights;
    }

    private static void DrawCategory(AppState state, UnlockList list, Unlock[] rows, bool enabled)
    {
        // The filter is what the bulk buttons act on, so it is applied before they are drawn.
        string needle = _filter.Trim();
        bool filtering = needle.Length > 0;
        var visible = filtering
            ? rows.Where(r => r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToArray()
            : rows;

        ImGui.BeginDisabled(!enabled);

        // The bulk buttons loop UNLOCK_SET over the rows: a client-side loop over a generic op.
        // Anything that needs game knowledge, a max ammo value for instance, is an ACTION
        // feature on the console and does not belong in this panel.
        if (ImGui.Button("Unlock all")) SetPrimaryForAll(state, visible, 1, filtering);
        ImGui.SameLine();
        if (ImGui.Button("Unlock none")) SetPrimaryForAll(state, visible, 0, filtering);
        ImGui.EndDisabled();

        // Searching is a view, not an edit, so it stays live outside INGAME.
        ImGui.SameLine();
        ImGui.SetNextItemWidth(SearchWidth);
        Ui.InputTextWithHint("##search", "Search", ref _filter);

        ImGui.SameLine();
        ImGui.TextColored(Ui.Grey, filtering ? $"{visible.Length} of {rows.Length}" : $"{rows.Length} entries");
        ImGui.Spacing();

        // A column needs both halves: the game has to have named the slot, and some entry in this
        // category has to declare it. Filtered-out rows still count, so the columns do not shift
        // about while the user types.
        var present = Enumerable.Range(0, UnlockList.SlotCount)
            .Where(slot => list.FieldAt(slot).IsNamed && rows.Any(r => r.HasField(slot)))
            .ToArray();

        var weights = ColumnWeights(present.Length);

        if (!ImGui.BeginTable("unlocks", weights.Length,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, weights[0]);
        for (int i = 0; i < present.Length; i++)
        {
            ImGui.TableSetupColumn(list.FieldAt(present[i]).Name, ImGuiTableColumnFlags.WidthStretch, weights[i + 1]);
        }

        ImGui.TableHeadersRow();

        foreach (var unlock in visible)
        {
            ImGui.TableNextRow();
            ImGui.PushID(unlock.Id);

            // The rows are a checkbox or a value box high, so the text columns are put on the same
            // line as the controls beside them rather than at the top of the row.
            ImGui.TableNextColumn();
            Ui.TableLabel(unlock.Name);

            // The id is still what every UNLOCK_SET carries; it is only the column that is gone,
            // because the names needed the width more. It is on the name's tooltip for anyone
            // reading the wire, which is what the debug switch is for.
            if (Ui.Debug && ImGui.IsItemHovered()) ImGui.SetTooltip($"id {unlock.Id}");

            foreach (int slot in present)
            {
                ImGui.TableNextColumn();
                if (!unlock.HasField(slot))
                {
                    Ui.TableLabel(Ui.Grey, "-");
                    continue;
                }

                var field = list.FieldAt(slot);
                ImGui.BeginDisabled(!enabled);
                if (field.Kind == UnlockFieldKind.Flag) DrawFlagCell(state, unlock, slot);
                else DrawNumberCell(state, unlock, slot, field);
                ImGui.EndDisabled();
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static void DrawFlagCell(AppState state, Unlock unlock, int field)
    {
        bool on = unlock.Values[field] != 0;
        if (ImGui.Checkbox($"##field{field}", ref on))
        {
            Send(state, unlock.Id, field, on ? 1u : 0u);
        }
    }

    private static void DrawNumberCell(AppState state, Unlock unlock, int field, UnlockField descriptor)
    {
        ImGui.SetNextItemWidth(-1);
        Ui.PushTableInput();
        bool sent = Ui.NumberOnEnter($"##field{field}", unlock.Values[field], Drafts, (unlock.Id, field), out long typed);
        Ui.PopTableInput();
        if (!sent) return;

        long ceiling = descriptor.Ceiling ?? uint.MaxValue;
        Send(state, unlock.Id, field, (uint)Math.Clamp(typed, 0, ceiling));
    }

    private static void Send(AppState state, byte id, int field, uint value)
    {
        state.Run(async () =>
        {
            await state.Client.UnlockSetAsync(id, (byte)field, value).ConfigureAwait(false);
            state.Post(() => state.RefreshUnlocks());
        });
    }

    /// <summary>
    /// The bulk buttons, over slot 0: the "do I have this" flag every game keeps there. With a
    /// filter in the box they act on the rows the user can actually see, and the toast says so.
    /// </summary>
    private static void SetPrimaryForAll(AppState state, Unlock[] rows, uint value, bool filtering)
    {
        var ids = rows.Where(r => r.HasField(UnlockList.PrimarySlot)).Select(r => r.Id).ToArray();
        if (ids.Length == 0)
        {
            state.AddToast(filtering
                ? "No entry matching the search can be unlocked"
                : "No entry in this category can be unlocked", ToastKind.Error);
            return;
        }

        string what = value != 0 ? "unlocked" : "not unlocked";
        string scope = filtering ? "shown entries" : "entries";

        state.Run(async () =>
        {
            foreach (byte id in ids)
            {
                await state.Client.UnlockSetAsync(id, UnlockList.PrimarySlot, value).ConfigureAwait(false);
            }

            state.Post(() => state.RefreshUnlocks());
        }, $"{ids.Length} {scope} set to {what}");
    }
}
