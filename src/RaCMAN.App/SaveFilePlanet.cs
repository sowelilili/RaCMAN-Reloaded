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
/// ToD's Fastoon II) has an index of its own after the planets before it. The list puts the
/// levels in the game's order, <see cref="SaveFilePlanetOrder"/>.
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
/// The order By planet lists a game's levels in: the planet picker's order (the Positions panel's
/// planet combo, PLANET_LIST's menu order), with a few levels moved for the save list alone. The
/// level a save names is its line in the planets file, which is the game's numbering and not the
/// order the game is played in: UYA numbers its Clank and ranger levels and the vid comics after
/// every planet, Deadlocked numbers Valix before Orxon, RaC2 its orbits after the Museum.
/// <para>
/// The picker's order comes from the console, with the planet names (qwark's
/// <c>planet_order</c>, build 53). While the running game's reply carried one, that is the order
/// (<see cref="SaveFileOrder.Sort"/> is handed it); otherwise, before PLANET_LIST answers or
/// against a module before build 53, <see cref="PickerOrder"/> is the same order as qwark sends
/// it, so the list sorts the same either way.
/// </para>
/// <para>
/// The picker is left exactly as the console orders it. The moves are the save list's own: a
/// save made on a level that belongs to a planet sits right after that planet's saves, where the
/// player meets it, rather than wherever the picker lists the level among the others.
/// </para>
/// </summary>
public static class SaveFilePlanetOrder
{
    /// <summary>A level moved, for the save list alone, to right after <see cref="After"/>.</summary>
    public readonly record struct Move(int Level, int After);

    /// <summary>
    /// UYA's picker order, qwark's <c>rac3_planet_order</c> (src/games/rac3.c): the planets in the
    /// order the game visits them, each mission level after its planet, the arena and ranger levels
    /// after the story, the vid comics by their number, then the two gaps and the menu.
    /// </summary>
    private static readonly int[] Rac3Picker =
    {
        1, 2, 3, 4, 5,          // Veldin, Florana, Starship Phoenix, Marcadia, Daxx
        11, 12,                 // Obani Gemini, Blackwater City
        7,                      // Annihilation Nation
        8, 27, 28,              // Aquatos, Aquatos Clank, Aquatos Sewers
        9, 10,                  // Tyhrranosis, Zeldrin Starport
        13, 23,                 // Holostar, Holostar Clank
        21,                     // Obani Draco
        16, 17, 18, 19,         // Metropolis, Crash Site, Aridia, Qwark's Hideout
        6, 14, 22, 20,          // Phoenix Rescue, Koros, Command Center, Launch Site
        24,                     // Insomniac Museum
        29, 26,                 // Tyhrranosis Rangers, Metropolis Rangers
        31, 33, 34, 32, 35, 30, // Vid Comics 1 to 6
        36,                     // Vid Comic 1 SE
        15, 25, 0,              // the two gaps and the menu
    };

    /// <summary>
    /// Deadlocked's picker order, qwark's <c>rac4_planet_order</c> (src/games/rac4.c): the
    /// numbering but for Orxon (8) before Valix (7), then the unused id and the three loops.
    /// </summary>
    private static readonly int[] Rac4Picker = { 1, 2, 4, 5, 6, 8, 7, 10, 11, 13, 14, 15, 0, 3, 9, 12 };

    /// <summary>
    /// RaC2, whose picker is in id order: each orbit after its planet (Dobbo Orbit, 22, after
    /// Dobbo, 9; Damosel Orbit, 23, after Damosel, 18), the Jamming Array (26) after Maktar (2)
    /// and Wupash (25) after Oozla (1). Slim Cognito's Ship Shack (24) names
    /// no planet and stays where the picker has it, after the Museum.
    /// </summary>
    private static readonly Move[] Rac2Moves = { new(25, 1), new(26, 2), new(22, 9), new(23, 18) };

    /// <summary>UYA: each ranger level after its planet, Tyhrranosis (9) and Metropolis (16).</summary>
    private static readonly Move[] Rac3Moves = { new(29, 9), new(26, 16) };

    /// <summary>
    /// The picker's order of <paramref name="game"/>'s levels when the console has not said one: the
    /// order qwark's PLANET_LIST sends, every id of its list once. RaC1, RaC2 and ToD number their
    /// levels in the order the game visits them and qwark sends the ids in turn (19, 27 and 19 of
    /// them); UYA and Deadlocked have orders of their own. Empty for a game with no planet list.
    /// </summary>
    public static IReadOnlyList<int> PickerOrder(GameId game) => game switch
    {
        GameId.Rac1 => Ids(19),
        GameId.Rac2 => Ids(27),
        GameId.Rac3 => Rac3Picker,
        GameId.Rac4 => Rac4Picker,
        GameId.Tod => Ids(19),
        _ => Array.Empty<int>(),
    };

    /// <summary>The levels the save list moves in <paramref name="game"/>, in the order they are made.</summary>
    public static IReadOnlyList<Move> Moves(GameId game) => game switch
    {
        GameId.Rac2 => Rac2Moves,
        GameId.Rac3 => Rac3Moves,
        _ => Array.Empty<Move>(),
    };

    /// <summary>
    /// The levels of <paramref name="game"/> in the save list's order: <paramref name="pickerOrder"/>,
    /// the console's, or <see cref="PickerOrder"/> without one, then each of <see cref="Moves"/>
    /// taken out and put back right after its planet. A move whose level or planet the order does
    /// not have is left out, and a level named twice counts where it first stands.
    /// </summary>
    public static int[] For(GameId game, IReadOnlyList<int>? pickerOrder = null)
    {
        var order = (pickerOrder is { Count: > 0 } ? pickerOrder : PickerOrder(game)).Distinct().ToList();
        foreach (var move in Moves(game))
        {
            if (move.Level == move.After || !order.Contains(move.After) || !order.Remove(move.Level)) continue;
            order.Insert(order.IndexOf(move.After) + 1, move.Level);
        }

        return order.ToArray();
    }

    /// <summary>
    /// Where each level of <paramref name="game"/> goes in the save list, from <see cref="For"/>.
    /// A level the order does not name goes after every one it does, by its number.
    /// </summary>
    public static Func<int, int> Ranks(GameId game, IReadOnlyList<int>? pickerOrder = null)
    {
        var order = For(game, pickerOrder);
        var rank = new Dictionary<int, int>(order.Length);
        for (int i = 0; i < order.Length; i++) rank[order[i]] = i;
        return level => rank.TryGetValue(level, out int at) ? at : order.Length + level;
    }

    private static int[] Ids(int count) => Enumerable.Range(0, count).ToArray();
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
