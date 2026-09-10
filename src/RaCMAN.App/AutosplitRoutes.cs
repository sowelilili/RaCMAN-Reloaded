using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>
/// The planet split route: which split names count as "this planet", one line per planet in
/// PLANET_LIST order, aliases separated by commas, <c>null</c> for an index the game never enters.
/// <para>
/// This is the same file format and the same comparison the old LiveSplit scripts used
/// (<c>rac2planets.txt</c>, <c>dlplanets.txt</c>): the split name is lower-cased and asked whether
/// it <em>contains</em> any of the planet's aliases, so "Kerwan (Metropolis)" matches "kerwan".
/// The files ship under <c>data/autosplit/</c> beside the app; a copy of one in the data folder's
/// <c>autosplit/</c> is read instead, which is where an edited route belongs since an update
/// replaces the application folder. A missing file simply means the route cannot be used for that
/// game.
/// </para>
/// </summary>
public static class AutosplitRoutes
{
    /// <summary>The alias line that means "the game never uses this planet index".</summary>
    public const string UnusedMarker = "null";

    private static readonly Dictionary<GameId, IReadOnlyList<string[]>> Cache = new();
    private static readonly List<string> ProblemList = new();

    private static string _folder = DefaultFolder;

    public static string DefaultFolder => Path.Combine(AppPaths.ShippedData, "autosplit");

    /// <summary>Where the shipped files are read from. Tests point this at the source tree.</summary>
    public static string Folder => _folder;

    /// <summary>
    /// The user's own route files. The shipped ones sit in the application folder, which an update
    /// replaces whole, so an edited route goes here instead and is read in place of the shipped one
    /// of the same name. The same rule as <c>gamelayout.json</c>, for the same reason.
    /// </summary>
    public static string OverrideFolder => Path.Combine(AppPaths.Root, "autosplit");

    /// <summary>Files that could not be read, for the panel to show rather than silently mismatch.</summary>
    public static IReadOnlyList<string> Problems => ProblemList;

    /// <summary>The file's stem: the game's own name in lower case, "rac1".."rac4".</summary>
    public static string KeyFor(GameId game) => game.ToString().ToLowerInvariant();

    public static string FileFor(GameId game)
    {
        string name = $"{KeyFor(game)}-planets.txt";
        string mine = Path.Combine(OverrideFolder, name);
        return File.Exists(mine) ? mine : Path.Combine(_folder, name);
    }

    /// <summary>Reads from a different folder (and forgets what was cached).</summary>
    public static void LoadFrom(string folder)
    {
        lock (Cache)
        {
            _folder = folder;
            Cache.Clear();
            ProblemList.Clear();
        }
    }

    public static void Invalidate() => LoadFrom(_folder);

    /// <summary>
    /// The alias lists for one game, indexed by planet. Empty when the game has no route file, and
    /// an entry is empty for an unused index; either way nothing ever matches, so a route the
    /// client cannot check never splits on its own.
    /// </summary>
    public static IReadOnlyList<string[]> For(GameId game)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(game, out var cached)) return cached;

            var planets = Load(FileFor(game));
            Cache[game] = planets;
            return planets;
        }
    }

    private static IReadOnlyList<string[]> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<string[]>();

            var planets = new List<string[]>();
            foreach (var line in File.ReadAllLines(path))
            {
                planets.Add(line
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(alias => alias.ToLowerInvariant())
                    .Where(alias => alias.Length > 0 && alias != UnusedMarker)
                    .ToArray());
            }

            return planets;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (Cache) ProblemList.Add($"{Path.GetFileName(path)}: {ex.Message}");
            return Array.Empty<string[]>();
        }
    }

    /// <summary>True when the game has a route file with a usable line for this planet.</summary>
    public static bool Knows(GameId game, int planet)
    {
        var planets = For(game);
        return planet >= 0 && planet < planets.Count && planets[planet].Length > 0;
    }

    /// <summary>
    /// True when the file covers this planet and says <c>null</c>: an index the game never enters,
    /// or the main menu, which is index 0 in every game's list. Nothing is wrong with one of these
    /// and nothing should be said about it — a planet the file does not cover <em>at all</em> is a
    /// different thing, and <see cref="Knows"/> is false for both.
    /// </summary>
    public static bool Unused(GameId game, int planet)
    {
        var planets = For(game);
        return planet >= 0 && planet < planets.Count && planets[planet].Length == 0;
    }

    /// <summary>
    /// The old scripts' test: does the split name contain one of this planet's aliases? False for
    /// a planet the file does not cover and for an empty split name, so an unknown planet or a
    /// run with no split loaded never splits by route.
    /// </summary>
    public static bool Matches(GameId game, int planet, string? splitName)
    {
        if (string.IsNullOrWhiteSpace(splitName)) return false;

        var planets = For(game);
        if (planet < 0 || planet >= planets.Count) return false;

        string name = splitName.ToLowerInvariant();
        foreach (var alias in planets[planet])
        {
            if (name.Contains(alias, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>The first alias for a planet, for the log line. Empty when the file does not cover it.</summary>
    public static string AliasFor(GameId game, int planet)
    {
        var planets = For(game);
        return planet >= 0 && planet < planets.Count && planets[planet].Length > 0 ? planets[planet][0] : string.Empty;
    }
}
