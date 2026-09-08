using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// UNLOCK_LIST rendered as one tab per category, with a column for each field the entry's
/// bitmask says is meaningful. Every edit is one UNLOCK_SET; the list is then re-read.
///
/// Above the tabs sit the features the layout moved to <see cref="GameLayout.UnlocksSection"/>:
/// console-side actions that rewrite the whole table (UYA's weapon-level pair), which belong with
/// the list they change rather than on the Game page.
///
/// Refresh policy: the whole list is re-read once a second while the panel is open, and again
/// straight after any UNLOCK_SET. Re-reading everything is simpler than patching one row back
/// into the list and it keeps another client's edits visible.
/// </summary>
public static class UnlocksPanel
{
    private const float AutoRefreshSeconds = 1f;

    private static bool _opened;
    private static bool _autoRefresh = true;
    private static float _sinceRefresh;

    /// <summary>Half-typed level and ammo edits, kept only while the box has focus.</summary>
    private static readonly Dictionary<(byte Id, int Field), string> Drafts = new();

    private static readonly string[] FieldLabels = { "Owned", "Gold", "Level", "Ammo" };

    public static void Reset()
    {
        _opened = false;
        _sinceRefresh = 0;
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

        ImGui.BeginDisabled(!state.Connected);
        if (ImGui.Button("Refresh"))
        {
            state.RefreshUnlocks();
            _sinceRefresh = 0;
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.Checkbox("Auto-refresh (1 Hz)", ref _autoRefresh);

        if (_autoRefresh && state.Connected)
        {
            _sinceRefresh += ImGui.GetIO().DeltaTime;
            if (_sinceRefresh >= AutoRefreshSeconds)
            {
                _sinceRefresh = 0;
                state.RefreshUnlocks(quiet: true);
            }
        }

        if (!enabled)
        {
            ImGui.TextColored(Ui.Yellow, $"Unlock edits need INGAME (state is {state.Session.State}).");
        }

        if (state.UnlocksUnsupported)
        {
            Ui.Hint("This game has no unlock table.");
            return;
        }

        var list = state.Unlocks;
        if (list.Unlocks.Length == 0)
        {
            Ui.Hint(!state.Connected ? "Connect to read the unlock list."
                : !enabled ? $"No unlock list: the session is {state.Session.State}, not INGAME."
                : "The unlock list is empty.");
            return;
        }

        ImGui.Spacing();

        if (!ImGui.BeginTabBar("unlock-categories")) return;

        int categories = Math.Max(list.Categories.Length, list.Unlocks.Max(u => u.Category) + 1);
        for (int category = 0; category < categories; category++)
        {
            var rows = list.Unlocks.Where(u => u.Category == category).ToArray();
            if (rows.Length == 0) continue;

            string name = category < list.Categories.Length ? list.Categories[category] : $"Category {category}";
            if (!ImGui.BeginTabItem($"{name}###category{category}")) continue;

            ImGui.PushID(category);
            DrawCategory(state, rows, enabled);
            ImGui.PopID();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    /// <summary>
    /// The features the layout sent to the "Unlocks" section, drawn the way the Game page draws
    /// actions: equal-width buttons, two to a row. Each is one FEATURE_TRIGGER followed by a
    /// re-read, the same shape as the bulk owned buttons inside a category. Only actions are
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

    private static void DrawCategory(AppState state, Unlock[] rows, bool enabled)
    {
        ImGui.BeginDisabled(!enabled);

        // The bulk buttons loop UNLOCK_SET over the rows: a client-side loop over a generic op.
        // Anything that needs game knowledge, a max ammo value for instance, is an ACTION
        // feature on the console and does not belong in this panel.
        if (ImGui.Button("Own all")) SetOwnedForAll(state, rows, 1);
        ImGui.SameLine();
        if (ImGui.Button("Own none")) SetOwnedForAll(state, rows, 0);
        ImGui.EndDisabled();

        ImGui.SameLine();
        Ui.Hint($"{rows.Length} entries");
        ImGui.Spacing();

        // Only the columns some row in this category actually carries are shown.
        var present = Enumerable.Range(0, 4).Where(f => rows.Any(r => r.HasField(f))).ToArray();
        int columns = 2 + present.Length;

        if (!ImGui.BeginTable("unlocks", columns,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 36);
        ImGui.TableSetupColumn("Name");
        foreach (int field in present)
        {
            ImGui.TableSetupColumn(FieldLabels[field], ImGuiTableColumnFlags.WidthFixed, field < 2 ? 70 : 110);
        }

        ImGui.TableHeadersRow();

        foreach (var unlock in rows)
        {
            ImGui.TableNextRow();
            ImGui.PushID(unlock.Id);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(unlock.Id.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(unlock.Name);

            foreach (int field in present)
            {
                ImGui.TableNextColumn();
                if (!unlock.HasField(field))
                {
                    ImGui.TextColored(Ui.Grey, "-");
                    continue;
                }

                ImGui.BeginDisabled(!enabled);
                if (field <= 1) DrawFlagCell(state, unlock, field);
                else DrawNumberCell(state, unlock, field);
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

    private static void DrawNumberCell(AppState state, Unlock unlock, int field)
    {
        ImGui.SetNextItemWidth(-1);
        if (Ui.NumberOnEnter($"##field{field}", unlock.Values[field], Drafts, (unlock.Id, field), out long typed))
        {
            Send(state, unlock.Id, field, (uint)Math.Clamp(typed, 0, uint.MaxValue));
        }
    }

    private static void Send(AppState state, byte id, int field, uint value)
    {
        state.Run(async () =>
        {
            await state.Client.UnlockSetAsync(id, (byte)field, value).ConfigureAwait(false);
            state.Post(() => state.RefreshUnlocks());
        });
    }

    private static void SetOwnedForAll(AppState state, Unlock[] rows, uint value)
    {
        var ids = rows.Where(r => r.HasField((int)UnlockField.Owned)).Select(r => r.Id).ToArray();
        if (ids.Length == 0)
        {
            state.AddToast("No entry in this category has an owned field", ToastKind.Error);
            return;
        }

        state.Run(async () =>
        {
            foreach (byte id in ids)
            {
                await state.Client.UnlockSetAsync(id, (byte)UnlockField.Owned, value).ConfigureAwait(false);
            }

            state.Post(() => state.RefreshUnlocks());
        }, $"{ids.Length} entries set to {(value != 0 ? "owned" : "not owned")}");
    }
}
