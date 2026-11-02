using System.Text.Json;
using System.Text.Json.Serialization;
using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>
/// One game's level-flag names as data/levelflags/*.json holds them. View data, like the moby
/// layouts: names for the bits LEVELFLAGS_GET returns, grouped by planet, never an address. The
/// values themselves always come from your console.
/// </summary>
public sealed class LevelFlagNameFile
{
    /// <summary>The telemetry <c>game</c> byte the names are for.</summary>
    [JsonPropertyName("game")]
    public byte Game { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Where the names came from, so they can be checked against it again.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>What the bits number: which region, and how a flag's number becomes its bit.</summary>
    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("groups")]
    public List<GroupEntry> Groups { get; set; } = new();

    /// <summary>The flags in the order the list shows them: by group, then by number.</summary>
    [JsonPropertyName("flags")]
    public List<FlagEntry> Flags { get; set; } = new();

    public sealed class GroupEntry
    {
        /// <summary>The PLANET_LIST id whose flags these are, or 255 for a group that is no planet's.</summary>
        [JsonPropertyName("planet")]
        public int Planet { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    public sealed class FlagEntry
    {
        /// <summary>The game's own number for the flag.</summary>
        [JsonPropertyName("number")]
        public int Number { get; set; }

        /// <summary>An index into <see cref="Groups"/>.</summary>
        [JsonPropertyName("group")]
        public int Group { get; set; }

        /// <summary>byte * 8 + bit of LEVELFLAGS_GET's region, the numbering LEVELFLAGS_BIT takes.</summary>
        [JsonPropertyName("bit")]
        public int Bit { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// The model the Level flags panel draws from, or the first thing wrong with the file: a group
    /// index past the groups, a planet, number or bit out of range.
    /// </summary>
    public LevelFlagNames ToNames()
    {
        if (Groups.Count == 0 || Groups.Count > byte.MaxValue) throw new InvalidDataException($"{Groups.Count} groups");

        var groups = new LevelFlagGroup[Groups.Count];
        for (int i = 0; i < groups.Length; i++)
        {
            var g = Groups[i];
            if (g.Planet is < 0 or > byte.MaxValue) throw new InvalidDataException($"group {i}: planet {g.Planet}");
            groups[i] = new LevelFlagGroup((byte)g.Planet, g.Name ?? string.Empty);
        }

        var flags = new LevelFlagName[Flags.Count];
        for (int i = 0; i < flags.Length; i++)
        {
            var f = Flags[i];
            if (f.Group < 0 || f.Group >= groups.Length) throw new InvalidDataException($"flag {f.Number}: group {f.Group}");
            if (f.Bit is < 0 or > ushort.MaxValue) throw new InvalidDataException($"flag {f.Number}: bit {f.Bit}");
            if (f.Number is < 0 or > ushort.MaxValue) throw new InvalidDataException($"flag {f.Number}: number out of range");
            flags[i] = new LevelFlagName((ushort)f.Bit, (ushort)f.Number, (byte)f.Group, f.Name ?? string.Empty);
        }

        return new LevelFlagNames(groups, flags);
    }
}

/// <summary>
/// Loads data/levelflags/*.json once and indexes the names by the telemetry <c>game</c> byte. A game
/// without a file has no names, and the Level flags panel shows its bytes. A file that cannot be
/// read or is wrong gives no names either: it goes into <see cref="Problems"/> and the rest still load.
/// </summary>
public static class LevelFlagNameFiles
{
    public const string FolderName = "data/levelflags";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static IReadOnlyDictionary<GameId, LevelFlagNames>? _cache;

    /// <summary>Non-fatal problems <see cref="All"/> found while loading, written to the log rather than thrown.</summary>
    public static IReadOnlyList<string> Problems { get; private set; } = Array.Empty<string>();

    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "data", "levelflags");

    public static IReadOnlyDictionary<GameId, LevelFlagNames> All => _cache ??= LoadAndLog(DefaultFolder);

    /// <summary>The names for <paramref name="game"/>, or null for a game that has no names file.</summary>
    public static LevelFlagNames? For(GameId game) => All.TryGetValue(game, out var names) ? names : null;

    /// <summary>Reads every names file in <paramref name="folder"/>; what was wrong comes back in <paramref name="problems"/>.</summary>
    public static IReadOnlyDictionary<GameId, LevelFlagNames> Load(string folder, out IReadOnlyList<string> problems)
    {
        var found = new List<string>();
        var names = new Dictionary<GameId, LevelFlagNames>();
        problems = found;

        if (!Directory.Exists(folder))
        {
            found.Add($"no level flag names folder at {folder}");
            return names;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var data = JsonSerializer.Deserialize<LevelFlagNameFile>(File.ReadAllText(file), Options);
                if (data is null || data.Game == 0)
                {
                    found.Add($"{Path.GetFileName(file)}: no game byte");
                    continue;
                }

                if (!names.TryAdd((GameId)data.Game, data.ToNames()))
                {
                    found.Add($"{Path.GetFileName(file)}: game {data.Game} already has a names file");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                           or InvalidDataException)
            {
                found.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return names;
    }

    private static IReadOnlyDictionary<GameId, LevelFlagNames> LoadAndLog(string folder)
    {
        var names = Load(folder, out var problems);
        Problems = problems;
        foreach (var problem in problems) Console.WriteLine($"level flag names: {problem}");
        return names;
    }

    /// <summary>Forces the next <see cref="All"/> to re-read the folder.</summary>
    public static void Invalidate() => _cache = null;
}
