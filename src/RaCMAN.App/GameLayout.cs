using System.Text.Json;
using System.Text.Json.Serialization;
using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>The layout for one game (or one title): the section order, and per-feature moves out of their qwark group.</summary>
public sealed class TitleLayout
{
    /// <summary>Preferred section order. Sections a feature lands in but not listed here follow, in first-seen order.</summary>
    [JsonPropertyName("tabOrder")]
    public string[] TabOrder { get; set; } = Array.Empty<string>();

    /// <summary>Feature label -> section name. Overrides where a feature would otherwise sit.</summary>
    [JsonPropertyName("moves")]
    public Dictionary<string, string> Moves { get; set; } = new();

    /// <summary>
    /// Section name -> heading text -> the feature labels that heading is drawn in front of. A
    /// section with more than one thing in it can be broken up this way (UYA's Cosmetics page has
    /// the chargeboot colours under a "Chargeboots" header), without any of it being hard-coded in
    /// a panel.
    /// </summary>
    [JsonPropertyName("headings")]
    public Dictionary<string, Dictionary<string, string[]>> Headings { get; set; } = new();
}

/// <summary>The whole data/gamelayout.json file.</summary>
public sealed class LayoutFile
{
    /// <summary>
    /// The sections that become a sub-page of their own, indented under Game in the side nav,
    /// rather than stacking on the Game page. A list of section names; this is the raw JSON rather
    /// than <c>string[]</c> because the short-lived table keyed by panel is read as well, so an
    /// override file written against that still loads.
    /// </summary>
    [JsonPropertyName("subPages")]
    public JsonElement SubPages { get; set; }

    /// <summary>
    /// The sections the Unlocks panel draws as tabs of its own, beside the unlock categories, for
    /// the controls that belong with the table they rewrite.
    /// </summary>
    [JsonPropertyName("unlocksTabs")]
    public string[]? UnlocksTabs { get; set; }

    /// <summary>
    /// The old spelling of <c>subPages</c>, read when a file has no <c>subPages</c> at all so that
    /// a copy of the file someone edited before the key was renamed keeps working.
    /// </summary>
    [JsonPropertyName("sideSections")]
    public string[]? SideSections { get; set; }

    /// <summary>
    /// Keyed by game ("rac1".."rac4"), or by a title id for a layout that should only apply to that
    /// one disc. A title-id entry is honoured before the game key.
    /// </summary>
    [JsonPropertyName("games")]
    public Dictionary<string, TitleLayout> Games { get; set; } = new();

    /// <summary>
    /// The sub-page list as every reader sees it, filled in on load: the file's, the old key's or
    /// the shipped default, with the names that cannot be a page of their own already gone.
    /// </summary>
    [JsonIgnore]
    public string[] Pages { get; set; } = Array.Empty<string>();

    /// <summary>The Unlocks panel's tab list, cleaned the same way and on the same pass.</summary>
    [JsonIgnore]
    public string[] Tabs { get; set; } = Array.Empty<string>();
}

/// <summary>
/// The feature layout, owned by the client rather than qwark. qwark's DESCRIBE groups are the
/// default; <c>data/gamelayout.json</c> (shipped, and editable by the user) overrides where a feature
/// goes, which sections become sub-pages of the Game page and which become tabs on the Unlocks
/// panel, so the layout can be tuned without touching the console module.
/// <para>
/// Entries are keyed by game ("rac1".."rac4") rather than by title id, because BCES01503 hosts RaC1,
/// RaC2 and RaC3 under a single title id. An entry keyed by a title id still wins over the game key,
/// for the odd release that needs its own layout.
/// </para>
/// </summary>
public static class GameLayout
{
    /// <summary>
    /// The block at the very top of the Game page: the controls a run reaches for every few
    /// seconds. The page fills it with things that are not features at all (die, the position
    /// pair, the planet and slot controls) and with the two flagged savefile ACTIONs; a layout
    /// that moves a feature here has it drawn beside them instead of in a section of its own.
    /// </summary>
    public const string QuickSection = "Quick";

    /// <summary>The player-value table, under its own header just below the quick block.</summary>
    public const string ValuesSection = "Values";

    /// <summary>
    /// The per-file switches that ride in a column to the right of the value table, since that
    /// table never needs the whole width. Placed by the Game page itself, so it is never a side
    /// sub-page, never a stacked header and never in the tab order.
    /// </summary>
    public const string OptionsSection = "Options";

    /// <summary>
    /// Features the Unlocks panel draws instead of the Game page: whole-table actions that belong
    /// with the unlock list they rewrite. The Game page ignores this section entirely.
    /// </summary>
    public const string UnlocksSection = "Unlocks";

    /// <summary>
    /// An ordinary stacked section, named here because the layout file has always called it out and
    /// the shipped tab orders start with it. The position buttons that used to be appended to it
    /// are in the quick block now, so a game with nothing in this section simply has no Player header.
    /// </summary>
    public const string PlayerSection = "Player";

    /// <summary>
    /// The section names the client owns. A panel decides where each of these is drawn, so none of
    /// them may become a sub-page or a stacked header however the layout file is written.
    /// </summary>
    public static bool IsReserved(string section) =>
        section is QuickSection or ValuesSection or OptionsSection or UnlocksSection;

    /// <summary>The sub-pages of the Game page when the file says nothing about them.</summary>
    private static readonly string[] DefaultSubPages = { "Manips", "Cosmetics", "Debug" };

    /// <summary>The Unlocks panel's own tabs when the file says nothing about them.</summary>
    private static readonly string[] DefaultUnlocksTabs = { "Collectables" };

    /// <summary>
    /// The keys of the panel-keyed <c>subPages</c> table this file briefly used. It was never
    /// released, so nothing writes it any more; it is read so that an override file written while
    /// it existed still says what its author meant. The Game key is the sub-page list, the Unlocks
    /// key is what <c>unlocksTabs</c> spells now.
    /// </summary>
    private const string OldGameKey = "Game";

    private const string OldUnlocksKey = "Unlocks";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static LayoutFile? _cache;

    /// <summary>The one the release ships, in the application folder, replaced on every update.</summary>
    public static string ShippedPath => AppPaths.ShippedGameLayout;

    /// <summary>
    /// The file that is actually read: a <c>gamelayout.json</c> in the data folder if the user put
    /// one there, else the shipped one. That is what keeps an edited layout through an update, since
    /// the updater replaces the application folder whole.
    /// </summary>
    public static string DefaultPath => UsingOverride ? AppPaths.GameLayoutOverride : ShippedPath;

    /// <summary>True while the user's own copy in the data folder is the one in use.</summary>
    public static bool UsingOverride => File.Exists(AppPaths.GameLayoutOverride);

    public static IReadOnlyList<string> Problems { get; private set; } = Array.Empty<string>();

    private static LayoutFile Config => _cache ??= Load(DefaultPath);

    /// <summary>Re-read the file next time (after the user edits it).</summary>
    public static void Invalidate() => _cache = null;

    /// <summary>Load a specific file into the cache (tests point this at the source tree's data/gamelayout.json).</summary>
    public static void LoadFrom(string path) => _cache = Load(path);

    /// <summary>
    /// The sections drawn as sub-pages of the Game page, indented under it in the side nav, in the
    /// order the file lists them.
    /// </summary>
    public static IReadOnlyList<string> SubPages => Config.Pages;

    /// <summary>
    /// The sections the Unlocks panel draws as tabs of its own, beside the unlock categories, in
    /// the order the file lists them.
    /// </summary>
    public static IReadOnlyList<string> UnlocksTabs => Config.Tabs;

    /// <summary>
    /// The Game page's sub-pages, with the fallback for a game that has no unlock table: the
    /// Unlocks panel is not in the nav at all then, so the sections it would have drawn as tabs are
    /// listed under Game rather than becoming unreachable.
    /// </summary>
    public static IReadOnlyList<string> SubPagesFor(bool unlocksHidden) =>
        unlocksHidden ? Config.Pages.Concat(Config.Tabs).ToArray() : Config.Pages;

    /// <summary>
    /// True for a section the Unlocks panel draws as a tab of its own. False for every section
    /// while that panel is hidden, since the tab would then be nowhere: those fall back to
    /// <see cref="SubPagesFor"/> instead.
    /// </summary>
    public static bool IsUnlocksTab(string section, bool unlocksHidden) =>
        !unlocksHidden && Config.Tabs.Contains(section, StringComparer.Ordinal);

    /// <summary>
    /// Every section that is drawn somewhere of its own: a sub-page of the Game page, or a tab on
    /// the Unlocks panel. None of them stacks on the Game page, so this is the list that page
    /// skips, whichever of the two a section is in.
    /// </summary>
    public static IReadOnlyList<string> SectionsDrawnElsewhere => Config.Pages.Concat(Config.Tabs).ToArray();

    /// <summary>The key a game is looked up under: the lower-case enum name, "rac1".."rac4".</summary>
    private static string GameKey(GameId game) => game.ToString().ToLowerInvariant();

    private static LayoutFile Load(string path)
    {
        LayoutFile? loaded = null;

        try
        {
            if (File.Exists(path)) loaded = JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(path), Options);
            Problems = Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Problems = new[] { $"gamelayout.json: {ex.Message}" };
        }

        // A file that is missing or broken is the same as a file that says nothing: the shipped
        // defaults, which is what keeps the client usable while the user fixes their own copy.
        return Normalise(loaded ?? new LayoutFile());
    }

    /// <summary>Keys are title ids and game names, so match them without caring about case.</summary>
    private static LayoutFile Normalise(LayoutFile file)
    {
        var games = new Dictionary<string, TitleLayout>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, layout) in file.Games) games[key] = layout;
        file.Games = games;

        ResolveLists(file);

        return file;
    }

    /// <summary>
    /// The two lists as every reader sees them: what the file says, each key falling back to the
    /// shipped default on its own, with the names that cannot be a page already gone. A reserved
    /// name would otherwise turn a panel-owned section into a page, and a section in both lists
    /// would be drawn twice, so the sub-page list keeps it and the tab list loses it.
    /// </summary>
    private static void ResolveLists(LayoutFile file)
    {
        string[]? pages = null;
        string[]? tabs = null;

        switch (file.SubPages.ValueKind)
        {
            case JsonValueKind.Array:
                pages = Names(file.SubPages);
                break;

            // The panel-keyed table this file briefly used, so an override written against it says
            // the same thing under the two keys that replaced it.
            case JsonValueKind.Object:
                pages = Names(Property(file.SubPages, OldGameKey));
                tabs = Names(Property(file.SubPages, OldUnlocksKey));
                break;
        }

        pages ??= file.SideSections;       // the older spelling of the same list
        tabs = file.UnlocksTabs ?? tabs;   // a file with both means what the current key says

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        file.Pages = Clean(pages ?? DefaultSubPages, claimed);
        file.Tabs = Clean(tabs ?? DefaultUnlocksTabs, claimed);
    }

    /// <summary>The names a list may keep: not blank, not a reserved section, and not already taken.</summary>
    private static string[] Clean(IEnumerable<string?> sections, HashSet<string> claimed) =>
        sections
            .Where(section => !string.IsNullOrWhiteSpace(section) && !IsReserved(section) && claimed.Add(section))
            .Select(section => section!)
            .ToArray();

    /// <summary>The strings in a JSON array, or null for anything that is not one.</summary>
    private static string[]? Names(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray()
            : null;

    /// <summary>
    /// One property of a JSON object, matched the way the property names themselves are: without
    /// caring about case, since these keys are hand-typed.
    /// </summary>
    private static JsonElement? Property(JsonElement table, string name)
    {
        foreach (var property in table.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }

        return null;
    }

    /// <summary>The layout for a title id if there is one, else the one for the game. Null when neither is configured.</summary>
    private static TitleLayout? LayoutFor(string? titleId, GameId game)
    {
        if (!string.IsNullOrWhiteSpace(titleId) && Config.Games.TryGetValue(titleId, out var byTitle)) return byTitle;
        return Config.Games.TryGetValue(GameKey(game), out var byGame) ? byGame : null;
    }

    /// <summary>
    /// The section a feature belongs in: a configured move if there is one, otherwise the top value
    /// table for a VALUE, otherwise the game's own DESCRIBE group.
    /// </summary>
    public static string SectionFor(string titleId, GameId game, Feature feature, DescribeResult describe)
    {
        if (LayoutFor(titleId, game) is { } layout
            && layout.Moves.TryGetValue(feature.Label, out var section)
            && !string.IsNullOrWhiteSpace(section))
        {
            return section;
        }

        return feature.Kind == FeatureKind.Value ? ValuesSection : describe.GroupName(feature.Group);
    }

    /// <summary>
    /// The headings one section draws, as feature label -> heading text: every label the file lists
    /// under a heading maps to it, and the page draws that heading once, in front of the first of
    /// those labels it reaches. Empty for a section the file says nothing about, which is what makes
    /// this cost nothing for the sections that are one list of controls.
    /// </summary>
    public static IReadOnlyDictionary<string, string> HeadingsFor(string titleId, GameId game, string section)
    {
        var headings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(section)) return headings;

        if (LayoutFor(titleId, game) is not { } layout) return headings;
        if (layout.Headings is not { } table || !table.TryGetValue(section, out var forSection)) return headings;
        if (forSection is null) return headings;

        foreach (var (heading, labels) in forSection)
        {
            if (string.IsNullOrWhiteSpace(heading) || labels is null) continue;

            // First heading to name a label wins, so a label listed twice cannot draw two headers.
            foreach (var label in labels)
            {
                if (!string.IsNullOrWhiteSpace(label)) headings.TryAdd(label, heading);
            }
        }

        return headings;
    }

    /// <summary>
    /// One run of a section's features, with the heading drawn above them or null for the ones no
    /// heading names. See <see cref="Blocks"/>.
    /// </summary>
    public sealed record FeatureBlock(string? Heading, IReadOnlyList<Feature> Features);

    /// <summary>
    /// A section's features split into the blocks its headings make: the features a heading names
    /// are collected under it, in their own order, and the blocks follow the order their first
    /// feature appears in. A section with no headings is one block with no heading, which is what
    /// every section was before the file could ask for one.
    /// </summary>
    public static IReadOnlyList<FeatureBlock> Blocks(
        string titleId, GameId game, string section, IEnumerable<Feature> features)
    {
        var headings = HeadingsFor(titleId, game, section);
        var order = new List<string?>();
        var blocks = new Dictionary<string, List<Feature>>(StringComparer.Ordinal);
        var unheaded = new List<Feature>();

        foreach (var feature in features)
        {
            string? heading = headings.GetValueOrDefault(feature.Label);
            if (heading is null)
            {
                if (unheaded.Count == 0) order.Add(null);
                unheaded.Add(feature);
                continue;
            }

            if (!blocks.TryGetValue(heading, out var list))
            {
                list = new List<Feature>();
                blocks[heading] = list;
                order.Add(heading);
            }

            list.Add(feature);
        }

        return order
            .Select(heading => new FeatureBlock(heading, heading is null ? unheaded : blocks[heading]))
            .ToArray();
    }

    /// <summary>
    /// The section order for a game: the configured order first, then any other used sections. The
    /// reserved sections are never listed, since a panel places each of them itself.
    /// </summary>
    public static IReadOnlyList<string> TabOrder(string titleId, GameId game, IEnumerable<string> used)
    {
        var order = new List<string>();

        if (LayoutFor(titleId, game) is { } layout)
        {
            foreach (var tab in layout.TabOrder)
            {
                if (!IsReserved(tab) && !order.Contains(tab)) order.Add(tab);
            }
        }

        foreach (var tab in used)
        {
            if (!IsReserved(tab) && !order.Contains(tab)) order.Add(tab);
        }

        return order;
    }
}
