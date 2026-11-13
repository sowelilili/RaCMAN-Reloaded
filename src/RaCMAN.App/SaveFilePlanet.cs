using System.Buffers.Binary;
using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>How the Save files panel orders the saves of a category (Settings, Files).</summary>
public enum SaveFileSort
{
    /// <summary>By the planet each save was made on, read out of the save (<see cref="SaveFilePlanet"/>).</summary>
    ByPlanet,

    /// <summary>By name alone, numbers compared as numbers (<see cref="SaveFileOrder.CompareNatural"/>).</summary>
    Alphabetical,
}

/// <summary>
/// The planet a save was made on, read out of the save itself. This is the one thing the client
/// knows about a save's format, and it is used for nothing but the order of the Save files list:
/// a save is still opaque bytes to every save and load.
/// <para>
/// The four original games write a save the same way (each game's <c>SaveData_serialize</c>):
/// the sizes of the global block and of one planet's block, then the global block, a length and a
/// checksum word followed by tagged fields <c>{u32 id, u32 size, bytes}</c> in the order of the
/// game's field table. Field 0 is the current level, 4 bytes, so every save has it at the same
/// place: its tag at 0x10, the level, big-endian, at 0x18. The block sizes are the ones the
/// game's own restore accepts, so anything else is not a save of that game and has no planet.
/// The checksum word (16 bits in UYA and Deadlocked, 0 in RaC1 and RaC2) is part of the head
/// in all four and moves nothing.
/// </para>
/// <para>
/// Tools of Destruction's save is the game's GAME.SAV image, 0x906F0 bytes, which ends with the
/// current level, a big-endian <c>LEVEL_</c> at 0x906E8 (docs/tod-savefile.md in qwark).
/// </para>
/// <para>
/// The number is the game's own level index, the line of the game's <c>&lt;game&gt;-planets.txt</c>
/// (<see cref="AutosplitRoutes"/>), so a revisit (RaC1's second Veldin, RaC2's second Aranos,
/// ToD's Fastoon II) has an index of its own after the planets before it.
/// </para>
/// </summary>
public static class SaveFilePlanet
{
    /// <summary>The bytes of an original game's save that say its planet: the head up to and with field 0.</summary>
    public const int ClassicHeadLength = 0x1C;

    /// <summary>Where an original game's save keeps its level.</summary>
    public const int ClassicLevelOffset = 0x18;

    /// <summary>The length of every Tools of Destruction save.</summary>
    public const long TodSaveLength = 0x906F0;

    /// <summary>Where a Tools of Destruction save keeps its level.</summary>
    public const long TodLevelOffset = 0x906E8;

    /// <summary>The bytes of a save to read for its planet.</summary>
    public readonly record struct Range(long Offset, int Length);

    /// <summary>What to read for <paramref name="game"/>'s planet, or null for a game this client cannot read one for.</summary>
    public static Range? RangeFor(GameId game) => game switch
    {
        GameId.Rac1 or GameId.Rac2 or GameId.Rac3 or GameId.Rac4 => new Range(0, ClassicHeadLength),
        GameId.Tod => new Range(TodLevelOffset, 4),
        _ => null,
    };

    /// <summary>
    /// The level of a save of <paramref name="game"/>, from the bytes at <see cref="RangeFor"/> of a
    /// file <paramref name="fileLength"/> bytes long. Null when they are not a save of that game.
    /// </summary>
    public static int? Parse(GameId game, ReadOnlySpan<byte> bytes, long fileLength)
    {
        switch (game)
        {
            case GameId.Rac1:
            case GameId.Rac2:
            case GameId.Rac3:
            case GameId.Rac4:
            {
                if (bytes.Length < ClassicHeadLength || fileLength < ClassicHeadLength) return null;

                uint global = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                uint planet = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
                if (!Heads(game, global, planet)) return null;

                // The global block's own length, its checksum, then field 0's tag.
                if (BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != global - 8) return null;
                if (BinaryPrimitives.ReadUInt32BigEndian(bytes[0x10..]) != 0) return null;
                if (BinaryPrimitives.ReadUInt32BigEndian(bytes[0x14..]) != 4) return null;

                int level = BinaryPrimitives.ReadInt32BigEndian(bytes[ClassicLevelOffset..]);
                return level >= 0 ? level : null;
            }

            case GameId.Tod:
            {
                if (fileLength != TodSaveLength || bytes.Length < 4) return null;

                int level = BinaryPrimitives.ReadInt32BigEndian(bytes);
                return level >= 0 ? level : null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// The block sizes the game's restore takes (its <c>SaveData_restore</c>): the ones its field
    /// tables add up to, and in RaC2 and UYA either of an older pair beside them.
    /// </summary>
    private static bool Heads(GameId game, uint global, uint planet) => game switch
    {
        GameId.Rac1 => global == 0x153C && planet == 0x82A4,
        GameId.Rac2 => global is 0x1D08 or 0x1CF8 && planet is 0x84D0 or 0x0CD0,
        GameId.Rac3 => global is 0x37B0 or 0x1CF8 && planet is 0x84C0 or 0x0CD0,
        GameId.Rac4 => global is 0x41F0 or 0x1CF8 && planet is 0x031C or 0x0CD0,
        _ => false,
    };

    /// <summary>
    /// Reads the level of the save at <paramref name="path"/>: only the bytes at
    /// <see cref="RangeFor"/>, never the whole file. Null for a file that is too short or not a
    /// save of the game. Throws what opening or reading the file throws.
    /// </summary>
    public static int? ReadFile(GameId game, string path)
    {
        if (RangeFor(game) is not { } range) return null;

        using var stream = new FileStream(path, System.IO.FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        long length = stream.Length;
        if (length < range.Offset + range.Length) return null;

        stream.Position = range.Offset;
        Span<byte> bytes = stackalloc byte[range.Length];
        stream.ReadExactly(bytes);
        return Parse(game, bytes, length);
    }

    /// <summary>
    /// Whether a save that is only on the console can have its planet read over the wire. FILE_READ
    /// reads on from where the handle is and has no offset (PROTOCOL.md 5.8), so only a range that
    /// starts the file is a read of just those bytes: the original games', not ToD's, whose level is
    /// in the last bytes of a 578 KB file.
    /// </summary>
    public static bool ReadableOnConsole(GameId game) =>
        RangeFor(game) is { Offset: 0 } range && range.Length <= QwarkClient.FileChunkSize;
}

/// <summary>
/// The planets of the saves the panel has listed, kept per game and CRC32: the same bytes are the
/// same planet wherever they are, the CRC is in every row already (the PC library caches its own by
/// length and write time), and a save written over changes it. So a rescan of an unchanged category
/// reads nothing, and a console copy's few bytes are fetched once.
/// <para>
/// Filled on the render thread from the PC's files and on the network side from the console's, so
/// every access holds the lock.
/// </para>
/// </summary>
public sealed class SaveFilePlanetCache
{
    private readonly Dictionary<(GameId Game, uint Crc), int?> _known = new();

    /// <summary>Whether the planet of these bytes is known, and what it is (null for "not a save").</summary>
    public bool TryGet(GameId game, uint crc, out int? planet)
    {
        lock (_known) return _known.TryGetValue((game, crc), out planet);
    }

    /// <summary>The planet of these bytes, or null when it is unknown or there is none.</summary>
    public int? Planet(GameId game, uint crc) => TryGet(game, crc, out var planet) ? planet : null;

    public void Remember(GameId game, uint crc, int? planet)
    {
        lock (_known) _known[(game, crc)] = planet;
    }

    /// <summary>
    /// The planet of a file on this PC whose CRC32 is <paramref name="crc"/>: the cached answer, or
    /// the few bytes <see cref="SaveFilePlanet.ReadFile"/> reads. A file that cannot be read is
    /// unknown and not remembered, so the next scan tries it again.
    /// </summary>
    public int? Local(GameId game, string path, uint crc)
    {
        if (TryGet(game, crc, out var known)) return known;

        int? planet;
        try
        {
            planet = SaveFilePlanet.ReadFile(game, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        Remember(game, crc, planet);
        return planet;
    }

    /// <summary>
    /// The planet of a save only the console has, at <paramref name="path"/>, from one FILE_READ of
    /// the head of the file (<see cref="SaveFilePlanet.ReadableOnConsole"/>), remembered under the
    /// CRC the console listed. Null without asking for a game whose planet is not in the head.
    /// </summary>
    public async Task<int?> ConsoleAsync(QwarkClient client, GameId game, string path, uint crc, uint size,
        CancellationToken cancellationToken = default)
    {
        if (TryGet(game, crc, out var known)) return known;
        if (!SaveFilePlanet.ReadableOnConsole(game) || SaveFilePlanet.RangeFor(game) is not { } range) return null;

        byte[] head;
        uint handle = await client.FileOpenAsync(path, Protocol.FileMode.Read, cancellationToken).ConfigureAwait(false);
        try
        {
            head = await client.FileReadAsync(handle, (uint)range.Length, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The handle goes back even when the read failed, as QwarkClient.ReadFileAsync's does.
            await client.FileCloseAsync(handle, CancellationToken.None).ConfigureAwait(false);
        }

        var planet = SaveFilePlanet.Parse(game, head, size);
        Remember(game, crc, planet);
        return planet;
    }
}
