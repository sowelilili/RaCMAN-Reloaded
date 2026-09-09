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
