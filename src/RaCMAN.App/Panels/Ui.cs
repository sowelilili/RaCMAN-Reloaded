using System.Globalization;
using System.Numerics;
using ImGuiNET;

namespace RaCMAN.App.Panels;

/// <summary>Small shared helpers so the panels stay about the protocol, not about ImGui plumbing.</summary>
public static class Ui
{
    /// <summary>
    /// True while the light theme is applied. Set by the window when the theme is applied, so the
    /// status colours below stay readable on both backgrounds without every call site knowing.
    /// </summary>
    public static bool Light { get; set; } = true;

    /// <summary>Mirrors the "Show debug information" setting; gates <see cref="DebugHint"/>.</summary>
    public static bool Debug { get; set; }

    /// <summary>
    /// Why a control is greyed out when the session carries flags.NO_CODE_PATCHES. One spelling,
    /// because it is the same reason on a cheat, on a mod and on a client patch, and the user
    /// meets it in three panels.
    /// </summary>
    public const string NoCodePatches = "Needs a code patch, which RPCS3 cannot apply";

    /// <summary>The same fact as a sentence, for the panels that are entirely about code patches.</summary>
    public const string ModsAreCodePatches = "Mods are code patches, which RPCS3 cannot apply";

    public const string PatchesAreCodePatches = "Client patches are code patches, which RPCS3 cannot apply";

    /// <summary>
    /// What is going on when the console is INGAME and the client has nothing but the Memory panel
    /// to show: the title running on it is not one qwark has a game module for. Said in the same
    /// words on the Connection panel and on the Memory panel, which are the two places to look.
    /// </summary>
    public const string NoGameModule = "qwark has no game module for this title, so nothing about the "
                                      + "game itself can be read. The memory tools work as they always do.";

    public static Vector4 Green => Light ? new(0.10f, 0.55f, 0.15f, 1f) : new(0.45f, 0.85f, 0.45f, 1f);
    public static Vector4 Red => Light ? new(0.80f, 0.15f, 0.15f, 1f) : new(0.95f, 0.45f, 0.45f, 1f);
    public static Vector4 Yellow => Light ? new(0.70f, 0.45f, 0.00f, 1f) : new(0.95f, 0.82f, 0.35f, 1f);
    public static Vector4 Grey => Light ? new(0.42f, 0.42f, 0.45f, 1f) : new(0.60f, 0.60f, 0.60f, 1f);

    /// <summary>Plain body text, for the places that draw text on their own background.</summary>
    public static Vector4 Neutral => Light ? new(0.12f, 0.12f, 0.16f, 1f) : new(0.85f, 0.85f, 0.90f, 1f);

    public static void Heading(string text)
    {
        ImGui.TextUnformatted(text);
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>
    /// Explanatory grey text. It wraps at the panel edge: the window is small enough that a
    /// sentence written on one line would otherwise run off the right-hand side.
    /// </summary>
    public static void Hint(string text) => Wrapped(Grey, text);

    /// <summary>Something the user should notice but that is not an error.</summary>
    public static void Warning(string text) => Wrapped(Yellow, text);

    /// <summary>Something that is broken and needs the user to act.</summary>
    public static void Error(string text) => Wrapped(Red, text);

    /// <summary>A hint that only exists when the user asked to see the wire-level detail.</summary>
    public static void DebugHint(string text)
    {
        if (Debug) Wrapped(Grey, text);
    }

    /// <summary>
    /// The explanation behind the thing just drawn, shown on hover. Built by hand rather than with
    /// SetTooltip, which is printf underneath and would eat a per cent sign; wrapped, because a
    /// sentence of explanation is wider than anything it hangs off.
    /// </summary>
    public static void Tooltip(string text)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// <summary>
    /// A small button that opens a folder of this client's in the platform's file manager, with
    /// the path itself on the tooltip. The libraries live at absolute paths long enough to wrap
    /// over three lines of panel, and printing one was never something the user could act on.
    /// The tooltip may name a file inside the folder instead, for the buttons that lead to one.
    /// </summary>
    public static void OpenFolderButton(AppState state, string folder, string? tooltip = null)
    {
        string shown = tooltip ?? folder;

        // Several of these can share a panel, and they all read "Open folder", so the path is what
        // tells them apart to ImGui.
        ImGui.PushID(shown);
        ImGui.BeginDisabled(!FileExplorer.IsSupported);
        if (ImGui.SmallButton("Open folder"))
        {
            var (ok, message) = FileExplorer.Open(folder);
            if (!ok) state.AddToast(message, ToastKind.Error);
        }

        ImGui.EndDisabled();
        ImGui.PopID();

        // AllowWhenDisabled so the path can still be read where there is no file manager to ask,
        // and the tooltip is built by hand because SetTooltip is printf underneath, for the same
        // reason as below.
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(shown);
            ImGui.EndTooltip();
        }
    }

    /// <summary>
    /// Coloured text that is text and not a format string. Everything ImGui calls Text is printf
    /// underneath, so a LiveSplit category called "Any% (co-op)" reaches the screen as
    /// "Any(co-op)": the per cent and what follows it are read as a conversion and eaten.
    /// </summary>
    public static void Text(Vector4 colour, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    // ---------------------------------------------------------------- inputs inside tables

    /// <summary>
    /// The frame colours for a box drawn in a table cell: the field's own background goes away, so
    /// the row shows through it and the cell reads as part of that row rather than as a patch of a
    /// different grey. Hovered and active keep the theme's own colours, which is what makes the
    /// field light up when you reach for it. Always paired with <see cref="PopTableInput"/>.
    /// <para>
    /// Only for the boxes that are typed into. A checkbox is nothing but its frame, so one drawn
    /// this way would be invisible until it was ticked.
    /// </para>
    /// </summary>
    public static void PushTableInput() => ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);

    public static void PopTableInput() => ImGui.PopStyleColor();

    /// <summary>
    /// A label in a row whose other cells hold a box or a checkbox. Text is drawn at the top of a
    /// row otherwise, so the name sits high beside the field it belongs to; this puts it on the
    /// field's own line.
    /// </summary>
    public static void TableLabel(string text)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(text);
    }

    /// <summary>The same for the coloured readings the tables put beside their boxes.</summary>
    public static void TableLabel(Vector4 colour, string text)
    {
        ImGui.AlignTextToFramePadding();
        Text(colour, text);
    }

    /// <summary>
    /// How tall a table row is when one of its cells holds two buttons stacked on top of each
    /// other. Rows are built around the tallest cell, so the number is wanted before the row is
    /// drawn as well as inside it.
    /// </summary>
    public static float StackedButtonRowHeight() =>
        (ImGui.GetFrameHeight() * 2f) + ImGui.GetStyle().ItemSpacing.Y;

    /// <summary>
    /// Moves the cursor down to where a single-line widget sits on the middle of a row that is
    /// <paramref name="rowHeight"/> tall. A cell is filled from the top, so a box in a row made
    /// tall by a stack of buttons beside it would otherwise hang off that row's top edge and read
    /// as belonging to the row above.
    /// </summary>
    public static void CentreInRow(float rowHeight)
    {
        float offset = (rowHeight - ImGui.GetFrameHeight()) * 0.5f;
        if (offset > 0f) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offset);
    }

    private static void Wrapped(Vector4 colour, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, colour);

        // TextUnformatted inside a wrap region rather than TextWrapped, for the same reason.
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();

        ImGui.PopStyleColor();
    }

    public static bool TryParseAddress(string text, out uint address)
    {
        text = (text ?? string.Empty).Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    public static bool TryParseValue(string text, out ulong value)
    {
        text = (text ?? string.Empty).Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public static byte[]? TryParseHexBytes(string text)
    {
        var cleaned = new string((text ?? string.Empty).Where(char.IsAsciiHexDigit).ToArray());
        if (cleaned.Length == 0 || cleaned.Length % 2 != 0) return null;

        var bytes = new byte[cleaned.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(cleaned.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    /// <summary>Formats a right-aligned watch value the way the old memory viewer did.</summary>
    public static string FormatValue(ulong value, byte size, string format)
    {
        switch (format)
        {
            case "hex":
                return "0x" + size switch
                {
                    1 => ((byte)value).ToString("X2"),
                    2 => ((ushort)value).ToString("X4"),
                    4 => ((uint)value).ToString("X8"),
                    _ => value.ToString("X16"),
                };
            case "float" when size == 4:
                return BitConverter.Int32BitsToSingle((int)(uint)value).ToString("0.####", CultureInfo.InvariantCulture);
            case "float" when size == 8:
                return BitConverter.Int64BitsToDouble((long)value).ToString("0.####", CultureInfo.InvariantCulture);
            case "signed":
                return size switch
                {
                    1 => ((sbyte)value).ToString(CultureInfo.InvariantCulture),
                    2 => ((short)value).ToString(CultureInfo.InvariantCulture),
                    4 => ((int)(uint)value).ToString(CultureInfo.InvariantCulture),
                    _ => ((long)value).ToString(CultureInfo.InvariantCulture),
                };
            default:
                return value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static readonly string[] ValueFormats = { "dec", "hex", "signed", "float" };

    /// <summary>A hex dump with the ASCII column, 16 bytes per line.</summary>
    public static string HexDump(uint baseAddress, ReadOnlySpan<byte> data)
    {
        var builder = new System.Text.StringBuilder();
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            builder.Append((baseAddress + offset).ToString("X8")).Append("  ");
            int count = Math.Min(16, data.Length - offset);

            for (int i = 0; i < 16; i++)
            {
                builder.Append(i < count ? data[offset + i].ToString("X2") : "  ").Append(i == 7 ? "  " : " ");
            }

            builder.Append(" |");
            for (int i = 0; i < count; i++)
            {
                char c = (char)data[offset + i];
                builder.Append(c is >= ' ' and <= '~' ? c : '.');
            }

            builder.Append("|\n");
        }

        return builder.ToString();
    }

    public static bool InputTextWithHint(string label, string hint, ref string value, uint maxLength = 256) =>
        ImGui.InputTextWithHint(label, hint, ref value, maxLength);

    /// <summary>
    /// A tab item with flags on it. The binding only offers those through the overload that also
    /// takes the "still open" flag a close box writes back to, and none of these tabs has a close
    /// box, so this is the call it does not generate: the same tab item with no such flag. What the
    /// panels want it for is <see cref="ImGuiTabItemFlags.SetSelected"/>, which is how a tab named
    /// on the command line is the one showing when the window opens.
    /// </summary>
    public static unsafe bool BeginTabItem(string label, ImGuiTabItemFlags flags)
    {
        int length = System.Text.Encoding.UTF8.GetByteCount(label);
        Span<byte> utf8 = length < 256 ? stackalloc byte[length + 1] : new byte[length + 1];
        System.Text.Encoding.UTF8.GetBytes(label, utf8);
        utf8[length] = 0;

        fixed (byte* text = utf8)
        {
            return ImGuiNative.igBeginTabItem(text, null, flags) != 0;
        }
    }

    /// <summary>
    /// A decimal box that shows <paramref name="live"/> until the user types in it and only
    /// reports a value when Enter is pressed, so a half-typed number never reaches the console.
    /// The draft lives in <paramref name="drafts"/> while the box has focus and is dropped the
    /// moment it loses it, which is what makes the box follow the live value again.
    ///
    /// This is InputText rather than InputInt on purpose: Dear ImGui's InputScalar asserts when
    /// it is given ImGuiInputTextFlags.EnterReturnsTrue.
    /// </summary>
    public static bool NumberOnEnter<TKey>(string label, long live, IDictionary<TKey, string> drafts, TKey key,
        out long value)
        where TKey : notnull
    {
        value = live;

        string text = drafts.TryGetValue(key, out var draft)
            ? draft
            : live.ToString(CultureInfo.InvariantCulture);

        bool entered = ImGui.InputText(label, ref text, 12,
            ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);

        if (ImGui.IsItemActive()) drafts[key] = text;
        else drafts.Remove(key);

        if (!entered) return false;

        drafts.Remove(key);
        return long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
