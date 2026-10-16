namespace RaCMAN.Protocol;

/// <summary>
/// The savefiles a release ships: <c>premade-savefiles/&lt;TITLEID&gt;/&lt;name&gt;.sav</c> beside the
/// executable, read where they are and never written. The Save files panel shows them as one more
/// category, <see cref="Category"/>, at the bottom of a game's list.
/// <para>
/// The application folder is the release's and the updater replaces it whole, so this class has
/// nothing that writes: no store, no rename, no delete. Loading one is a load like any other, with
/// this as the source the bytes are read from (<see cref="ISaveFileSource"/>); a copy the user wants
/// to keep, rename or delete goes into their own library with <see cref="CopyTo"/>.
/// </para>
/// <para>
/// The folder is not called <c>savefiles</c>: a folder of that name beside the executable is an
/// older build's library to <c>DataFolderMigration</c>, which would copy it into the data folder.
/// </para>
/// <para>
/// A game runs under more title ids than the four folders are named after, and a save fits every
/// one of them that runs the same executable. <see cref="TitlesFileName"/> beside the folders says
/// which: RaC2's two repacks take NPEA00386's, and the disc trilogy BCES01503 takes RaC1's, RaC2's
/// or UYA's by the game that is running, since one title id holds all three. Everything here that
/// is asked about a title is therefore asked about a title and a game.
/// </para>
/// </summary>
public sealed class PremadeSaveFiles : ISaveFileSource
{
    /// <summary>The folder beside the executable, and in the repository, that holds them.</summary>
    public const string FolderName = "premade-savefiles";

    /// <summary>
    /// The list of other title ids, in the folder itself: one <c>TITLEID[/game] folder</c> a line,
    /// the game being rac1..rac4 and there for a title that holds more than one.
    /// </summary>
    public const string TitlesFileName = "titles.txt";

    /// <summary>
    /// The name the panel lists them under, and the console category a load uploads one into. qwark
    /// takes any printable name without a separator or a leading dot, so the console's copy lives
    /// under the same name the list shows, and its category is never offered as one of the user's.
    /// A title that holds several games has one console folder for all of them, so there the
    /// category carries the game as well (<see cref="ConsoleCategory"/>).
    /// </summary>
    public const string Category = "(premade savefiles)";

    private readonly Lazy<Dictionary<string, string>> _titles;

    public PremadeSaveFiles(string? rootPath)
    {
        RootPath = rootPath;

        // Read once: the application folder is the release's and does not change under a running
        // client. A list that cannot be read is no list: every title has only its own folder.
        _titles = new Lazy<Dictionary<string, string>>(() =>
        {
            if (rootPath is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var path = Path.Combine(rootPath, TitlesFileName);
                return File.Exists(path)
                    ? ParseTitles(File.ReadAllText(path))
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        });
    }

    /// <summary>The shipped folder, or null for a client that has none to look in.</summary>
    public string? RootPath { get; }

    /// <summary>
    /// Whether a category name is the premade one, however it is spelt: the name the panel lists,
    /// or one of the per-game names the console keeps them under for a title that holds several
    /// games.
    /// </summary>
    public static bool IsCategory(string? category)
    {
        var trimmed = (category ?? string.Empty).Trim();
        return string.Equals(trimmed, Category, StringComparison.OrdinalIgnoreCase)
               || (trimmed.Length > Category.Length + 1
                   && trimmed.StartsWith(Category + " ", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads the title list: <c>TITLEID[/game] folder</c> a line, blank lines and <c>#</c> comments
    /// skipped, keyed without regard to case. The folder name is kept inside the premade folder.
    /// </summary>
    public static Dictionary<string, string> ParseTitles(string text)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] == '#') continue;

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) continue;

            var folder = SaveFileLibrary.Sanitise(fields[1], string.Empty);
            if (folder.Length == 0) continue;

            titles[fields[0].Replace('\\', '/').Trim('/')] = folder;
        }

        return titles;
    }

    /// <summary>The key a game is listed under: the lower-case enum name, "rac1".."rac4".</summary>
    private static string GameKey(GameId game) => game.ToString().ToLowerInvariant();

    /// <summary>
    /// The folder of saves a title takes while <paramref name="game"/> runs under it: the list's
    /// line for the title and that game, then its line for the title alone, then the title's own
    /// folder. Null when there is no shipped folder at all.
    /// </summary>
    public string? FolderFor(string titleId, GameId game = GameId.None)
    {
        if (RootPath is null) return null;

        string title = SaveFileLibrary.Sanitise(titleId, "unknown");
        var titles = _titles.Value;
        if (game != GameId.None && titles.TryGetValue($"{title}/{GameKey(game)}", out var forGame))
        {
            return Path.Combine(RootPath, forGame);
        }

        return Path.Combine(RootPath, titles.TryGetValue(title, out var folder) ? folder : title);
    }

    /// <summary>
    /// The console category a load uploads into and the console's listing is read from.
    /// <see cref="Category"/>, except under a title the list gives one folder per game: the
    /// console keeps one savefile folder per title, and RaC1's and UYA's NG+.sav would otherwise
    /// be one file there. Within the console's 32 bytes either way.
    /// </summary>
    public string ConsoleCategory(string titleId, GameId game)
    {
        if (game == GameId.None) return Category;

        string title = SaveFileLibrary.Sanitise(titleId, "unknown");
        return _titles.Value.ContainsKey($"{title}/{GameKey(game)}") ? $"{Category} {GameKey(game)}" : Category;
    }

    /// <summary>The game a console category names, or <see cref="GameId.None"/> for the plain one.</summary>
    public static GameId GameIn(string? category)
    {
        var trimmed = (category ?? string.Empty).Trim();
        if (!IsCategory(trimmed) || trimmed.Length <= Category.Length) return GameId.None;

        return Enum.TryParse<GameId>(trimmed[(Category.Length + 1)..].Trim(), ignoreCase: true, out var game)
               && Enum.IsDefined(game)
            ? game
            : GameId.None;
    }

    /// <summary>
    /// Thrown in place of anything that would change a premade savefile or put a file among them.
    /// An <see cref="IOException"/>, as the mod library's refusals for a shipped mod are, so every
    /// caller that already reports a failed file operation reports this one too.
    /// </summary>
    public static IOException ReadOnly(string what) =>
        new($"The premade savefiles ship with RaCMAN Reloaded and are read-only, so {what}.");

    /// <summary>
    /// The premade savefiles of one game, sorted: every visible <c>.sav</c> in its folder
    /// (<see cref="FolderFor"/>). Empty for a game the release ships none for, and for a folder that
    /// cannot be read, since the answer decides whether the category is offered at all and a
    /// missing one is the honest reply.
    /// </summary>
    public string[] Files(string titleId, GameId game = GameId.None)
    {
        var folder = FolderFor(titleId, game);
        if (folder is null) return Array.Empty<string>();

        try
        {
            if (!Directory.Exists(folder)) return Array.Empty<string>();

            return Directory.EnumerateFiles(folder)
                .Where(IsSave)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Whether the release ships any savefile for this game.</summary>
    public bool Has(string titleId, GameId game = GameId.None) => Files(titleId, game).Length > 0;

    /// <summary>
    /// The same files with their sizes and CRC32s, which the merge view compares against the
    /// console's copies in <see cref="ConsoleCategory"/>. Cached per path, as the user's library is.
    /// </summary>
    public LocalSaveFile[] FilesWithCrc(string titleId, GameId game = GameId.None)
    {
        var folder = FolderFor(titleId, game);
        if (folder is null) return Array.Empty<LocalSaveFile>();

        var files = new List<LocalSaveFile>();
        foreach (var name in Files(titleId, game))
        {
            var path = Path.Combine(folder, name);
            var info = new FileInfo(path);
            files.Add(new LocalSaveFile(name, info.Length, CachedCrc(path, info)));
        }

        return files.ToArray();
    }

    /// <summary>
    /// The bytes of one premade savefile. Only a name the listing has is read, so nothing outside
    /// the game's own folder can be reached through this.
    /// </summary>
    public byte[] Read(string titleId, string name, GameId game = GameId.None)
    {
        var folder = FolderFor(titleId, game);
        var file = Files(titleId, game).FirstOrDefault(n => string.Equals(n, name, StringComparison.Ordinal));
        if (folder is null || file is null)
        {
            throw new FileNotFoundException($"RaCMAN Reloaded ships no premade savefile called '{SaveFileLibrary.DisplayName(name)}'.");
        }

        return File.ReadAllBytes(Path.Combine(folder, file));
    }

    /// <summary>
    /// The upload a load does reads through here. The category is the console's
    /// (<see cref="ConsoleCategory"/>), which names the game where the title holds several.
    /// </summary>
    byte[] ISaveFileSource.Read(string titleId, string category, string name) => Read(titleId, name, GameIn(category));

    /// <summary>
    /// Copies one premade savefile into the user's own library, into <paramref name="category"/>,
    /// where it can be renamed, deleted and saved over like any of theirs. The name it ships with is
    /// kept when it is free; a different save of that name already there is left alone and the copy
    /// lands beside it as "<c>name (2)</c>"; the same bytes already there under the name or one of
    /// those is not copied twice. Nothing in the shipped folder is touched.
    /// </summary>
    public PremadeCopy CopyTo(SaveFileLibrary library, string titleId, string name, string category,
        GameId game = GameId.None)
    {
        if (IsCategory(category)) throw ReadOnly("nothing can be copied into them");

        var data = Read(titleId, name, game);
        var folder = library.EnsureCategory(titleId, category);
        var stem = SaveFileLibrary.DisplayName(SaveFileLibrary.Sanitise(name, "savefile"));

        for (int n = 1; ; n++)
        {
            var candidate = n == 1 ? stem + SaveFileLibrary.Extension : $"{stem} ({n}){SaveFileLibrary.Extension}";
            var path = Path.Combine(folder, candidate);

            if (Directory.Exists(path)) continue;

            if (!File.Exists(path))
            {
                library.WriteAtomic(titleId, category, candidate, data);
                return new PremadeCopy(category, candidate, AlreadyThere: false);
            }

            if (Holds(path, data)) return new PremadeCopy(category, candidate, AlreadyThere: true);
        }
    }

    private static bool IsSave(string path)
    {
        if (!path.EndsWith(SaveFileLibrary.Extension, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            return (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Holds(string path, byte[] data)
    {
        var info = new FileInfo(path);
        return info.Length == data.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(data);
    }

    private readonly Dictionary<string, (long Length, DateTime Written, uint Crc)> _crcCache = new(StringComparer.OrdinalIgnoreCase);

    private uint CachedCrc(string path, FileInfo info)
    {
        if (_crcCache.TryGetValue(path, out var cached)
            && cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc)
        {
            return cached.Crc;
        }

        uint crc = Crc32.Compute(File.ReadAllBytes(path));
        _crcCache[path] = (info.Length, info.LastWriteTimeUtc, crc);
        return crc;
    }
}

/// <summary>
/// Where <see cref="PremadeSaveFiles.CopyTo"/> put a premade savefile: the category and the whole
/// file name in the user's library. <see cref="AlreadyThere"/> says those exact bytes were there
/// already, so nothing was written.
/// </summary>
public readonly record struct PremadeCopy(string Category, string Name, bool AlreadyThere);
