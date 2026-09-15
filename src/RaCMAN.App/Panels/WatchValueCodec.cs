using System.Globalization;

namespace RaCMAN.App.Panels;

/// <summary>
/// The other half of <see cref="Ui.FormatValue"/>: that writes the text in the watches table's
/// Value box, this reads it back and turns it into the bytes MEM_WRITE takes. A row is parsed in
/// the format it is displayed in, so the box accepts exactly what it shows, and the bytes come out
/// big-endian because that is the order the console stores them in.
/// <para>
/// None of this touches ImGui, which is the point: the panel owns the box and this owns the
/// arithmetic, and the arithmetic is what can be tested.
/// </para>
/// </summary>
public static class WatchValueCodec
{
    /// <summary>The largest unsigned value a watch of this size holds.</summary>
    public static ulong MaxFor(byte size) => size >= 8 ? ulong.MaxValue : (1UL << (size * 8)) - 1;

    /// <summary>
    /// Reads one typed value in the row's own format. False for text that is not a number in that
    /// format, and for a number too large for the watch: a value that would be silently truncated
    /// on the way to the console is not a value the user typed.
    /// </summary>
    public static bool TryParse(string? text, byte size, string? format, out ulong value)
    {
        value = 0;

        var typed = (text ?? string.Empty).Trim();
        if (typed.Length == 0 || size is 0 or > 8) return false;

        switch (format)
        {
            case "hex":
                return TryParseHex(typed, size, out value);

            // Only the two sizes that hold an IEEE number are read as one. FormatValue shows the
            // others as plain decimal, and the box has to take back what it puts up.
            case "float" when size == 4:
                if (!float.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out float single)) return false;
                value = (uint)BitConverter.SingleToInt32Bits(single);
                return true;

            case "float" when size == 8:
                if (!double.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out double wide)) return false;
                value = (ulong)BitConverter.DoubleToInt64Bits(wide);
                return true;

            case "signed":
                if (!long.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long signed)) return false;

                // Shifting out a 64-bit ceiling would overflow, so the widest watch is its own case.
                long ceiling = size >= 8 ? long.MaxValue : (1L << (size * 8 - 1)) - 1;
                long floor = size >= 8 ? long.MinValue : -ceiling - 1;
                if (signed > ceiling || signed < floor) return false;

                value = (ulong)signed & MaxFor(size);
                return true;

            default:
                // "dec", and a float on a size with no float in it. A hex prefix is taken here too,
                // because every other address and value box on this panel takes one.
                if (typed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return TryParseHex(typed, size, out value);

                if (!ulong.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value > MaxFor(size))
                {
                    value = 0;
                    return false;
                }

                return true;
        }
    }

    /// <summary>
    /// One byte as a cell of the viewer's hex dump takes it: two hex digits, and nothing else. A
    /// single digit is a pair the user has not finished typing rather than a value to write, so it
    /// is refused here and the cell drops it.
    /// </summary>
    public static bool TryParseByte(string? text, out byte value)
    {
        value = 0;

        var typed = (text ?? string.Empty).Trim();
        if (typed.Length != 2 || !char.IsAsciiHexDigit(typed[0]) || !char.IsAsciiHexDigit(typed[1])) return false;

        return byte.TryParse(typed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseHex(string typed, byte size, out ulong value)
    {
        if (typed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) typed = typed[2..];

        if (typed.Length == 0
            || !ulong.TryParse(typed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            || value > MaxFor(size))
        {
            value = 0;
            return false;
        }

        return true;
    }

    /// <summary>The value as the console stores it: <paramref name="size"/> bytes, big-endian.</summary>
    public static byte[] Encode(ulong value, byte size)
    {
        if (size is 0 or > 8) throw new ArgumentOutOfRangeException(nameof(size), "a watch is 1 to 8 bytes");

        var bytes = new byte[size];
        for (int i = 0; i < size; i++) bytes[size - 1 - i] = (byte)(value >> (i * 8));
        return bytes;
    }
}
