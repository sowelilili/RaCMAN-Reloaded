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
    public bool Capped => Rows.Length < TotalRows;

    public string Describe() => Capped
        ? $"Read {Rows.Length} of {TotalRows} rows (capped)."
        : $"Read {Rows.Length} rows from 0x{Start:X8} to 0x{End:X8}.";
}

/// <summary>
/// MOBY_TABLE names two pointer words and a stride; this reads the pointers, then walks the
/// array with MEM_READ in chunks of at most 64 KB and decodes each row with the game's layout
/// file. Nothing about a moby is known here beyond those offsets.
/// </summary>
public static class MobyTableReader
{
    /// <summary>Rows past this are not read: a full table can be tens of thousands of entries.</summary>
    public const int DefaultRowCap = 2048;

    public const int MaxReadBytes = 65536;

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
        uint end = ReadPointer(await client.MemReadAsync(info.TableEndPointerAddress, 4, cancellationToken).ConfigureAwait(false));

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
                rows.Add(Decode(layout, first + i, address + (uint)(i * stride), chunk.AsSpan(i * stride, stride)));
            }
        }

        return new MobyTableSnapshot(info, start, end, total, rows.ToArray());
    }

    public static uint ReadPointer(byte[] bytes) =>
        bytes.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : 0u;

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
