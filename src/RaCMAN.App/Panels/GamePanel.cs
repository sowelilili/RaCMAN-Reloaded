using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

public static class GamePanel
{
    /// <summary>Half-typed VALUE edits, kept only while the input has focus.</summary>
    private static readonly Dictionary<byte, string> Drafts = new();

    /// <summary>
    /// The last value this client sent per feature. Since revision 1.1 every VALUE, ENUM and
    /// COLOR names the readout that mirrors it, so this is only the fallback for readout 0xFF.
    /// </summary>
    private static readonly Dictionary<byte, uint> LastSet = new();

    public static void Reset()
    {
        Drafts.Clear();
        LastSet.Clear();
    }

    /// <summary>The feature's current value: its readout when it names one, else what we last sent.</summary>
    private static uint CurrentValue(AppState state, Feature feature) =>
        feature.MirrorReadout is { } mirror
            ? state.Session.ReadoutAt(mirror) ?? 0u
            : LastSet.GetValueOrDefault(feature.Id);

    public static void Draw(AppState state)
    {
        Ui.Heading("Game");

        var describe = state.Describe;
        if (describe.Features.Length == 0)
        {
            Ui.Hint(state.Connected
                ? "No descriptors. qwark returns an empty DESCRIBE until a supported game is running."
                : "Connect to see the game's descriptors.");
            return;
        }

        DrawReadouts(state, describe);

        ImGui.Spacing();
        bool enabled = state.Ingame;
        if (!enabled) ImGui.TextColored(Ui.Yellow, $"Controls are disabled outside INGAME (state is {state.Session.State}).");

        ImGui.BeginDisabled(!enabled);

        for (byte group = 0; group < Math.Max((byte)1, (byte)describe.Groups.Length); group++)
        {
            var features = describe.Features.Where(f => f.Group == group).ToArray();
            if (features.Length == 0) continue;

            if (!ImGui.CollapsingHeader(describe.GroupName(group), ImGuiTreeNodeFlags.DefaultOpen)) continue;

            ImGui.PushID(group);
            foreach (var feature in features) DrawFeature(state, describe, feature);
            ImGui.PopID();
            ImGui.Spacing();
        }

        ImGui.EndDisabled();
    }

    private static void DrawReadouts(AppState state, DescribeResult describe)
    {
        if (describe.Readouts.Length == 0) return;

        if (!ImGui.BeginTable("readouts", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit)) return;

        ImGui.TableSetupColumn("Readout");
        ImGui.TableSetupColumn("Value");
        ImGui.TableHeadersRow();

        var session = state.Session;
        for (int i = 0; i < describe.Readouts.Length && i < session.Readout.Length; i++)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(describe.Readouts[i]);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(session.Readout[i].ToString());
        }

        ImGui.EndTable();
    }

    private static void DrawFeature(AppState state, DescribeResult describe, Feature feature)
    {
        ImGui.PushID(feature.Id);
        var session = state.Session;
        ulong bit = 1UL << feature.Id;

        switch (feature.Kind)
        {
            case FeatureKind.Toggle:
            {
                bool on = (session.ToggleState & bit) != 0;
                if (ImGui.Checkbox(feature.Label, ref on))
                {
                    byte id = feature.Id;
                    uint value = on ? 1u : 0u;
                    state.Run(() => state.Client.FeatureSetAsync(id, value));
                }

                ImGui.SameLine();
                bool auto = (session.ToggleAuto & bit) != 0;
                if (ImGui.Checkbox("auto on boot", ref auto))
                {
                    byte id = feature.Id;
                    bool wanted = auto;
                    state.Run(() => state.Client.FeatureSetAutoAsync(id, wanted));
                }

                if (feature.WritesCode)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Ui.Grey, "(patches code)");
                }

                break;
            }

            case FeatureKind.Action:
            {
                if (ImGui.Button(feature.Label))
                {
                    byte id = feature.Id;
                    state.Run(() => state.Client.FeatureTriggerAsync(id));
                }

                break;
            }

            case FeatureKind.Value:
            {
                // The readout is the current value. A draft only exists while the box has focus,
                // and Enter is the only thing that sends: half-typed digits never reach the game.
                uint live = CurrentValue(state, feature);

                ImGui.SetNextItemWidth(160);
                if (Ui.NumberOnEnter(feature.Label, live, Drafts, feature.Id, out long typed))
                {
                    uint value = (uint)Math.Clamp(typed, 0, uint.MaxValue);
                    if (feature.Max > feature.Min) value = Math.Clamp(value, feature.Min, feature.Max);

                    byte id = feature.Id;
                    LastSet[id] = value;
                    state.Run(() => state.Client.FeatureSetAsync(id, value));
                }

                ImGui.SameLine();
                ImGui.TextColored(Ui.Grey, feature.MirrorReadout is { } mirror
                    ? $"live {live} ({describe.ReadoutName(mirror)}); Enter to send"
                    : $"last sent {live} (no readout); Enter to send");

                if (feature.Max > feature.Min)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Ui.Grey, $"[{feature.Min}..{feature.Max}]");
                }

                break;
            }

            case FeatureKind.Enum:
            {
                var options = state.EnumOptions.TryGetValue(feature.Id, out var loaded)
                    ? loaded
                    : Array.Empty<string>();

                if (options.Length == 0)
                {
                    ImGui.TextColored(Ui.Grey, $"{feature.Label}: waiting for FEATURE_OPTIONS");
                    break;
                }

                uint live = CurrentValue(state, feature);
                int current = live < (uint)options.Length ? (int)live : -1;

                ImGui.SetNextItemWidth(220);
                if (ImGui.Combo(feature.Label, ref current, options, options.Length) && current >= 0)
                {
                    byte id = feature.Id;
                    uint value = (uint)current;
                    LastSet[id] = value;
                    state.Run(() => state.Client.FeatureSetAsync(id, value));
                }

                if (current < 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Ui.Yellow, $"readout says {live}, which is not one of {options.Length} options");
                }

                break;
            }

            case FeatureKind.Color:
            {
                uint packed = CurrentValue(state, feature);
                var colour = new Vector3(
                    ((packed >> 16) & 0xFF) / 255f,
                    ((packed >> 8) & 0xFF) / 255f,
                    (packed & 0xFF) / 255f);

                ImGui.SetNextItemWidth(220);
                if (ImGui.ColorEdit3(feature.Label, ref colour))
                {
                    uint value = ((uint)(colour.X * 255) << 16) | ((uint)(colour.Y * 255) << 8) | (uint)(colour.Z * 255);
                    byte id = feature.Id;
                    LastSet[id] = value;
                    state.Run(() => state.Client.FeatureSetAsync(id, value));
                }

                ImGui.SameLine();
                ImGui.TextColored(Ui.Grey, $"0x{packed:X6}");

                break;
            }
        }

        ImGui.PopID();
    }
}
