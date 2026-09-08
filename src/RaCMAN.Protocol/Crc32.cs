namespace RaCMAN.Protocol;

/// <summary>
/// The standard reflected CRC-32 (poly 0xEDB88320, init and final xor 0xFFFFFFFF).
/// This is the hash qwark reports in MOD_LIST and the client writes to qwark.sum.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Update(Start(), data));

    public static uint Start() => 0xFFFFFFFFu;

    public static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            state = Table[(state ^ b) & 0xFF] ^ (state >> 8);
        }

        return state;
    }

    public static uint Finish(uint state) => state ^ 0xFFFFFFFFu;

    /// <summary>The eight lowercase hex digits that go into qwark.sum.</summary>
    public static string ToSumText(uint hash) => hash.ToString("x8");

    public static bool TryParseSum(string? text, out uint hash) =>
        uint.TryParse((text ?? string.Empty).Trim(), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out hash);
}
