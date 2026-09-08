using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>One field of a moby row: where it sits and how to read it.</summary>
public sealed class MobyField
{
    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    /// <summary>u8, i8, u16, i16, u32, i32, f32 or vec3f.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "u32";

    /// <summary>The struct field this offset was derived from, for the record.</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    public int Length => Type switch
    {
        "u8" or "i8" => 1,
        "u16" or "i16" => 2,
        "u32" or "i32" or "f32" => 4,
        "vec3f" => 12,
        _ => 0,
    };
}

/// <summary>
/// View metadata for the moby table of one game: which byte of a row holds the position, the
/// class, the UID and the state. These are offsets into a row the console hands us, not game
/// logic and not an address table; a game without a file gets index, address and position only.
/// </summary>
public sealed class MobyLayout
{
    [JsonPropertyName("game")]
    public byte Game { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Where the offsets came from, so they can be checked against the struct again.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>The stride the struct implies. MOBY_TABLE's stride wins at runtime.</summary>
    [JsonPropertyName("stride")]
    public int Stride { get; set; }

    [JsonPropertyName("fields")]
    public Dictionary<string, MobyField> Fields { get; set; } = new();

    public GameId GameId => (GameId)Game;

    public bool Has(string field) => Fields.ContainsKey(field);

    /// <summary>Reads one integer field out of a row, sign-extended when the field is signed.</summary>
    public bool TryReadInteger(ReadOnlySpan<byte> row, string field, out long value)
    {
        value = 0;
        if (!Fields.TryGetValue(field, out var layout)) return false;
        if (layout.Offset < 0 || layout.Offset + layout.Length > row.Length) return false;

        var span = row[layout.Offset..];
        switch (layout.Type)
        {
            case "u8": value = span[0]; return true;
            case "i8": value = (sbyte)span[0]; return true;
            case "u16": value = BinaryPrimitives.ReadUInt16BigEndian(span); return true;
            case "i16": value = BinaryPrimitives.ReadInt16BigEndian(span); return true;
            case "u32": value = BinaryPrimitives.ReadUInt32BigEndian(span); return true;
            case "i32": value = BinaryPrimitives.ReadInt32BigEndian(span); return true;
            default: return false;
        }
    }

    /// <summary>Reads a vec3f field (the first three floats of the game's Vec4).</summary>
    public bool TryReadVector(ReadOnlySpan<byte> row, string field, out float x, out float y, out float z)
    {
        x = y = z = 0;
        if (!Fields.TryGetValue(field, out var layout)) return false;
        if (layout.Type != "vec3f") return false;
        if (layout.Offset < 0 || layout.Offset + 12 > row.Length) return false;

        var span = row[layout.Offset..];
        x = BinaryPrimitives.ReadSingleBigEndian(span);
        y = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
        z = BinaryPrimitives.ReadSingleBigEndian(span[8..]);
        return true;
    }
}

/// <summary>Loads data/moby/*.json once and indexes it by the telemetry <c>game</c> byte.</summary>
public static class MobyLayouts
{
    public const string FolderName = "data/moby";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static IReadOnlyDictionary<GameId, MobyLayout>? _cache;

    /// <summary>Non-fatal problems found while loading, shown in the panel rather than thrown.</summary>
    public static IReadOnlyList<string> Problems { get; private set; } = Array.Empty<string>();

    public static string DefaultFolder =>
        Path.Combine(AppContext.BaseDirectory, "data", "moby");

    public static IReadOnlyDictionary<GameId, MobyLayout> All => _cache ??= Load(DefaultFolder);

    public static MobyLayout? For(GameId game) => All.TryGetValue(game, out var layout) ? layout : null;

    public static IReadOnlyDictionary<GameId, MobyLayout> Load(string folder)
    {
        var problems = new List<string>();
        var layouts = new Dictionary<GameId, MobyLayout>();

        if (!Directory.Exists(folder))
        {
            Problems = new[] { $"no moby layout folder at {folder}" };
            return layouts;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var layout = JsonSerializer.Deserialize<MobyLayout>(File.ReadAllText(file), Options);
                if (layout is null || layout.Game == 0)
                {
                    problems.Add($"{Path.GetFileName(file)}: no game byte");
                    continue;
                }

                if (!layouts.TryAdd(layout.GameId, layout))
                {
                    problems.Add($"{Path.GetFileName(file)}: game {layout.Game} is already claimed by {layouts[layout.GameId].Name}");
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Problems = problems;
        return layouts;
    }

    /// <summary>Forces the next <see cref="All"/> to re-read the folder.</summary>
    public static void Invalidate() => _cache = null;
}
