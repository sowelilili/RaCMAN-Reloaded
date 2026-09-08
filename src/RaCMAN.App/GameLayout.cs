using System.Text.Json;
using System.Text.Json.Serialization;
using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>The tab layout for one title: the tab order, and per-feature moves out of their qwark group.</summary>
public sealed class TitleLayout
{
    /// <summary>Preferred tab order. Tabs a feature actually lands in but not listed here follow, in first-seen order.</summary>
    [JsonPropertyName("tabOrder")]
    public string[] TabOrder { get; set; } = Array.Empty<string>();

    /// <summary>Feature label -> tab name. Overrides where a feature would otherwise sit.</summary>
    [JsonPropertyName("moves")]
    public Dictionary<string, string> Moves { get; set; } = new();
}

/// <summary>
/// The Game panel's tab layout, owned by the client rather than qwark. qwark's DESCRIBE groups are
/// the default; <c>data/gamelayout.json</c> (shipped, and editable by the user) overrides where a
/// feature goes, so the layout can be tuned without touching the console module. Keyed by title id.
/// </summary>
public static class GameLayout
{
    /// <summary>The pinned section at the top of the panel, rendered as the player-value table rather than a tab.</summary>
    public const string ValuesSection = "Values";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static Dictionary<string, TitleLayout>? _cache;

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "data", "gamelayout.json");

    public static IReadOnlyList<string> Problems { get; private set; } = Array.Empty<string>();

    private static Dictionary<string, TitleLayout> Config => _cache ??= Load(DefaultPath);

    /// <summary>Re-read the file next time (after the user edits it).</summary>
    public static void Invalidate() => _cache = null;

    /// <summary>Load a specific file into the cache (tests point this at the source tree's data/gamelayout.json).</summary>
    public static void LoadFrom(string path) => _cache = Load(path);

    private static Dictionary<string, TitleLayout> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, TitleLayout>>(File.ReadAllText(path), Options);
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

        return new Dictionary<string, TitleLayout>();
    }

    /// <summary>
    /// The section a feature belongs in: a configured move if there is one, otherwise the top value
    /// table for a VALUE, otherwise the game's own DESCRIBE group.
    /// </summary>
    public static string SectionFor(string titleId, Feature feature, DescribeResult describe)
    {
        if (Config.TryGetValue(titleId ?? string.Empty, out var layout)
            && layout.Moves.TryGetValue(feature.Label, out var section)
            && !string.IsNullOrWhiteSpace(section))
        {
            return section;
        }

        return feature.Kind == FeatureKind.Value ? ValuesSection : describe.GroupName(feature.Group);
    }

    /// <summary>The tab order for a title: the configured order first, then any other used tabs. Values is never a tab.</summary>
    public static IReadOnlyList<string> TabOrder(string titleId, IEnumerable<string> used)
    {
        var order = new List<string>();

        if (Config.TryGetValue(titleId ?? string.Empty, out var layout))
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
