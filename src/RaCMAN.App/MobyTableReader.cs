using System.Buffers.Binary;
using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>One decoded row of the moby array.</summary>
public readonly record struct MobyRow(
    int Index,
    uint Address,
    float X,
    float Y,
    float Z,
    long? OClass,
    long? Uid,
    long? State);

/// <summary>What one read of the moby table produced.</summary>
public sealed record MobyTableSnapshot(
    MobyTableInfo Info,
    uint Start,
    uint End,
    int TotalRows,
    MobyRow[] Rows)
{
    /// <summary>
    /// How many of the <see cref="TotalRows"/> rows were read, the free ones included: a table
    /// whose MOBY_TABLE carries a live test (revision 1.34) lists only the live rows it read.
    /// </summary>
    public int RowsWalked { get; init; } = -1;

    private int Walked => RowsWalked < 0 ? Rows.Length : RowsWalked;

    /// <summary>The free rows the walk passed over.</summary>
    public int FreeRows => Walked - Rows.Length;

    public bool Capped => Walked < TotalRows;

    public string Describe()
    {
        string free = FreeRows > 0 ? $", {Rows.Length} live and {FreeRows} free" : string.Empty;
        return Capped
            ? $"Read {Walked} of {TotalRows} rows (capped){free}."
            : $"Read {Walked} rows from 0x{Start:X8} to 0x{End:X8}{free}.";
    }
}

/// <summary>
/// MOBY_TABLE names two pointer words and a stride; this reads the pointers, then walks the
/// array with MEM_READ in chunks of at most <see cref="MaxReadBytes"/> and decodes each row with
/// the game's layout file. Nothing about a moby is known here beyond those offsets.
/// </summary>
public static class MobyTableReader
{
    /// <summary>Rows past this are not read: a full table can be tens of thousands of entries.</summary>
    public const int DefaultRowCap = 2048;

    /// <summary>
    /// What one MEM_READ may ask for. Revision 1.11 made this a quarter of what it was, so a
    /// capped table takes four times the round trips and reads exactly the same rows; a row
    /// wider than this could never be read at all, which is why the stride is checked against it.
    /// </summary>
    public const int MaxReadBytes = QwarkClient.MaxReadLength;

    public static async Task<MobyTableSnapshot> ReadAsync(
        QwarkClient client,
        MobyLayout? layout,
        int rowCap = DefaultRowCap,
        CancellationToken cancellationToken = default)
    {
        var info = await client.MobyTableAsync(cancellationToken).ConfigureAwait(false);
        int stride = info.Stride;
        if (stride is <= 0 or > MaxReadBytes)
        {
            throw new ProtocolException($"MOBY_TABLE stride {stride} is unusable");
        }

        uint start = ReadPointer(await client.MemReadAsync(info.TablePointerAddress, 4, cancellationToken).ConfigureAwait(false));
        uint endWord = ReadPointer(await client.MemReadAsync(info.TableEndPointerAddress, 4, cancellationToken).ConfigureAwait(false));
        uint end = EndOf(info, start, endWord);

        if (end < start)
        {
            throw new ProtocolException($"moby table end 0x{end:X8} is below its start 0x{start:X8}");
        }

        int total = (int)((end - start) / (uint)stride);
        int wanted = Math.Min(total, Math.Max(rowCap, 0));

        var rows = new List<MobyRow>(wanted);
        int perChunk = Math.Max(1, MaxReadBytes / stride);

        for (int first = 0; first < wanted; first += perChunk)
        {
            int count = Math.Min(perChunk, wanted - first);
            uint address = start + (uint)(first * stride);
            var chunk = await client.MemReadAsync(address, (uint)(count * stride), cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < count && (i + 1) * stride <= chunk.Length; i++)
            {
                // Revision 1.34: a free slot of a pool that keeps them among the live rows.
                if (info.IsDead(chunk.AsSpan(i * stride, stride))) continue;

                rows.Add(Decode(layout, first + i, address + (uint)(i * stride), chunk.AsSpan(i * stride, stride)));
            }
        }

        return new MobyTableSnapshot(info, start, end, total, rows.ToArray()) { RowsWalked = wanted };
    }

    public static uint ReadPointer(byte[] bytes) =>
        bytes.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : 0u;

    /// <summary>
    /// Where the table ends, out of the end word MOBY_TABLE names: a pointer past the last row, or
    /// since revision 1.34 (<see cref="MobyTableFlags.LastIndex"/>) the index of the last row the
    /// table can have live, negative when there is none.
    /// </summary>
    public static uint EndOf(MobyTableInfo info, uint start, uint endWord)
    {
        if (!info.EndIsLastIndex) return endWord;

        int last = unchecked((int)endWord);
        if (last < 0) return start;
        return start + (uint)((long)(last + 1) * info.Stride);
    }

    public static MobyRow Decode(MobyLayout? layout, int index, uint address, ReadOnlySpan<byte> row)
    {
        float x = 0, y = 0, z = 0;
        long? oClass = null, uid = null, state = null;

        if (layout is not null)
        {
            layout.TryReadVector(row, "position", out x, out y, out z);
            if (layout.TryReadInteger(row, "oClass", out long c)) oClass = c;
            if (layout.TryReadInteger(row, "uid", out long u)) uid = u;
            if (layout.TryReadInteger(row, "state", out long s)) state = s;
        }
        else if (row.Length >= 0x1C)
        {
            // All four games put the position Vec4 at 0x10; with no layout file that is the
            // only thing this client is willing to assume about a row.
            x = BinaryPrimitives.ReadSingleBigEndian(row[0x10..]);
            y = BinaryPrimitives.ReadSingleBigEndian(row[0x14..]);
            z = BinaryPrimitives.ReadSingleBigEndian(row[0x18..]);
        }

        return new MobyRow(index, address, x, y, z, oClass, uid, state);
    }
}
