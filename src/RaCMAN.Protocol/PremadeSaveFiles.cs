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
/// </summary>
public sealed class PremadeSaveFiles : ISaveFileSource
{
    /// <summary>The folder beside the executable, and in the repository, that holds them.</summary>
    public const string FolderName = "premade-savefiles";

    /// <summary>
    /// The name the panel lists them under, and the console category a load uploads one into. qwark
    /// takes any printable name without a separator or a leading dot, so the console's copy lives
    /// under the same name the list shows, and its category is never offered as one of the user's.
    /// </summary>
    public const string Category = "(premade savefiles)";

    public PremadeSaveFiles(string? rootPath)
    {
        RootPath = rootPath;
    }

    /// <summary>The shipped folder, or null for a client that has none to look in.</summary>
    public string? RootPath { get; }

    /// <summary>Whether a category name is the premade one, however it is spelt.</summary>
    public static bool IsCategory(string? category) =>
        string.Equals((category ?? string.Empty).Trim(), Category, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Thrown in place of anything that would change a premade savefile or put a file among them.
    /// An <see cref="IOException"/>, as the mod library's refusals for a shipped mod are, so every
    /// caller that already reports a failed file operation reports this one too.
    /// </summary>
    public static IOException ReadOnly(string what) =>
        new($"The premade savefiles ship with RaCMAN Reloaded and are read-only, so {what}.");

    public string? TitleFolder(string titleId) =>
        RootPath is null ? null : Path.Combine(RootPath, SaveFileLibrary.Sanitise(titleId, "unknown"));

    /// <summary>
    /// The premade savefiles of one game, sorted: every visible <c>.sav</c> in its folder. Empty for
    /// a game the release ships none for, and for a folder that cannot be read, since the answer
    /// decides whether the category is offered at all and a missing one is the honest reply.
    /// </summary>
    public string[] Files(string titleId)
    {
        var folder = TitleFolder(titleId);
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
    public bool Has(string titleId) => Files(titleId).Length > 0;

    /// <summary>
    /// The same files with their sizes and CRC32s, which the merge view compares against the
    /// console's copies in <see cref="Category"/>. Cached per path, as the user's library is.
    /// </summary>
    public LocalSaveFile[] FilesWithCrc(string titleId)
    {
        var folder = TitleFolder(titleId);
        if (folder is null) return Array.Empty<LocalSaveFile>();

        var files = new List<LocalSaveFile>();
        foreach (var name in Files(titleId))
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
    public byte[] Read(string titleId, string name)
    {
        var folder = TitleFolder(titleId);
        var file = Files(titleId).FirstOrDefault(n => string.Equals(n, name, StringComparison.Ordinal));
        if (folder is null || file is null)
        {
            throw new FileNotFoundException($"RaCMAN Reloaded ships no premade savefile called '{SaveFileLibrary.DisplayName(name)}'.");
        }

        return File.ReadAllBytes(Path.Combine(folder, file));
    }

    /// <summary>The upload a load does reads through here; the category is always <see cref="Category"/>.</summary>
    byte[] ISaveFileSource.Read(string titleId, string category, string name) => Read(titleId, name);

    /// <summary>
    /// Copies one premade savefile into the user's own library, into <paramref name="category"/>,
    /// where it can be renamed, deleted and saved over like any of theirs. The name it ships with is
    /// kept when it is free; a different save of that name already there is left alone and the copy
    /// lands beside it as "<c>name (2)</c>"; the same bytes already there under the name or one of
    /// those is not copied twice. Nothing in the shipped folder is touched.
    /// </summary>
    public PremadeCopy CopyTo(SaveFileLibrary library, string titleId, string name, string category)
    {
        if (IsCategory(category)) throw ReadOnly("nothing can be copied into them");

        var data = Read(titleId, name);
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
