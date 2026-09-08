using System.Text.Json;
using System.Text.Json.Serialization;
using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>The layout for one title: the section order, and per-feature moves out of their qwark group.</summary>
public sealed class TitleLayout
{
    /// <summary>Preferred section order. Sections a feature lands in but not listed here follow, in first-seen order.</summary>
    [JsonPropertyName("tabOrder")]
    public string[] TabOrder { get; set; } = Array.Empty<string>();

    /// <summary>Feature label -> section name. Overrides where a feature would otherwise sit.</summary>
    [JsonPropertyName("moves")]
    public Dictionary<string, string> Moves { get; set; } = new();
}

/// <summary>The whole data/gamelayout.json file.</summary>
public sealed class LayoutFile
{
    /// <summary>
    /// Sections that are their own sub-page under Game in the side nav rather than stacked on the
    /// Game page. Everything else stays on the Game page, always visible.
    /// </summary>
    [JsonPropertyName("sideSections")]
    public string[]? SideSections { get; set; }

    [JsonPropertyName("titles")]
    public Dictionary<string, TitleLayout> Titles { get; set; } = new();
}

/// <summary>
/// The Game panel's layout, owned by the client rather than qwark. qwark's DESCRIBE groups are the
/// default; <c>data/gamelayout.json</c> (shipped, and editable by the user) overrides where a feature
/// goes and which sections become side sub-pages, so the layout can be tuned without touching the
/// console module. Titles are keyed by title id.
/// </summary>
public static class GameLayout
{
    /// <summary>The pinned section at the top of the Game page, rendered as the player-value table.</summary>
    public const string ValuesSection = "Values";

    /// <summary>What counts as a side sub-page when the file does not say.</summary>
    private static readonly string[] DefaultSideSections = { "Collectables", "Cosmetics", "Debug" };

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static LayoutFile? _cache;

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "data", "gamelayout.json");

    public static IReadOnlyList<string> Problems { get; private set; } = Array.Empty<string>();

    private static LayoutFile Config => _cache ??= Load(DefaultPath);

    /// <summary>Re-read the file next time (after the user edits it).</summary>
    public static void Invalidate() => _cache = null;

    /// <summary>Load a specific file into the cache (tests point this at the source tree's data/gamelayout.json).</summary>
    public static void LoadFrom(string path) => _cache = Load(path);

    /// <summary>Sections that get their own sub-page under Game in the side nav.</summary>
    public static IReadOnlyList<string> SideSections => Config.SideSections ?? DefaultSideSections;

    private static LayoutFile Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(path), Options);
                if (loaded is not null)
                {
                    Problems = Array.Empty<string>();
                    return loaded;
                }
            }
            Problems = Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Problems = new[] { $"gamelayout.json: {ex.Message}" };
        }

        return new LayoutFile();
    }

    /// <summary>
    /// The section a feature belongs in: a configured move if there is one, otherwise the top value
    /// table for a VALUE, otherwise the game's own DESCRIBE group.
    /// </summary>
    public static string SectionFor(string titleId, Feature feature, DescribeResult describe)
    {
        if (Config.Titles.TryGetValue(titleId ?? string.Empty, out var layout)
            && layout.Moves.TryGetValue(feature.Label, out var section)
            && !string.IsNullOrWhiteSpace(section))
        {
            return section;
        }

        return feature.Kind == FeatureKind.Value ? ValuesSection : describe.GroupName(feature.Group);
    }

    /// <summary>The section order for a title: the configured order first, then any other used sections. Values is never listed.</summary>
    public static IReadOnlyList<string> TabOrder(string titleId, IEnumerable<string> used)
    {
        var order = new List<string>();

        if (Config.Titles.TryGetValue(titleId ?? string.Empty, out var layout))
        {
            foreach (var tab in layout.TabOrder)
            {
                if (tab != ValuesSection && !order.Contains(tab)) order.Add(tab);
            }
        }

        foreach (var tab in used)
        {
            if (tab != ValuesSection && !order.Contains(tab)) order.Add(tab);
        }

        return order;
    }
}
