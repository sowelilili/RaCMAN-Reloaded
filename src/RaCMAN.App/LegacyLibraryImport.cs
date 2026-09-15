using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>
/// The other half of the import from the old RaCMAN: the two libraries that sat beside its
/// config.txt. The old client kept everything in its own folder — <c>savefiles/&lt;TITLEID&gt;/
/// &lt;category&gt;/</c> and <c>mods/&lt;TITLEID&gt;/&lt;modfolder&gt;/</c> — and this client keeps
/// the same two layouts in the data folder, so the import is a copy with the duplicates left behind.
/// <para>
/// Nothing is overwritten and nothing is deleted. The old folder is left exactly as it was, and a
/// name this client already uses is imported beside what is there rather than over it. The one
/// thing that changes on the way in is the suffix: a save is <c>&lt;name&gt;.sav</c> here, and the
/// old client took any name at all.
/// </para>
/// <para>
/// No ImGui and no console: this is a file copy, so it runs off the render thread and works with
/// nothing plugged in.
/// </para>
/// </summary>
public static class LegacyLibraryImport
{
    /// <summary>The two folders beside the old racman.exe, by the names it used.</summary>
    public const string SaveFolderName = "savefiles";

    public const string ModFolderName = "mods";

    /// <summary>
    /// What the old folder holds, counted before anything is copied, so the panel can say what
    /// pressing the button would bring over. A folder without either library is simply nothing.
    /// </summary>
    public sealed record Survey(int Saves, int Categories, int Mods)
    {
        public static readonly Survey Nothing = new(0, 0, 0);

        public bool Anything => Saves > 0 || Mods > 0;

        /// <summary>"12 saves in 3 categories, 4 mods", in the words the Settings panel prints.</summary>
        public string Describe()
        {
            var parts = new List<string>(2);
            if (Saves > 0)
            {
                parts.Add($"{Saves} save{(Saves == 1 ? string.Empty : "s")} in "
                          + $"{Categories} categor{(Categories == 1 ? "y" : "ies")}");
            }

            if (Mods > 0) parts.Add($"{Mods} mod{(Mods == 1 ? string.Empty : "s")}");

            return parts.Count == 0 ? "no save files or mods" : string.Join(", ", parts);
        }
    }

    /// <summary>
    /// What one import did. Every count is of what was actually written; a file or a folder this
    /// client already had is counted as already here rather than imported again.
    /// </summary>
    public sealed record Result(
        int Saves,
        int SavesAlreadyHere,
        int SavesRenamed,
        int Mods,
        int ModsAlreadyHere,
        IReadOnlyList<string> Shipped,
        IReadOnlyList<string> BesideYours)
    {
        public static readonly Result Nothing =
            new(0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>());

        public bool Anything => Saves > 0 || Mods > 0;

        /// <summary>The lines the Settings panel adds to what it says it imported.</summary>
        public IReadOnlyList<string> Lines
        {
            get
            {
                var lines = new List<string>(4);

                if (Saves > 0)
                {
                    lines.Add(SavesRenamed > 0
                        ? $"{Saves} save file(s), {SavesRenamed} renamed"
                        : $"{Saves} save file(s)");
                }

                if (SavesAlreadyHere > 0) lines.Add($"{SavesAlreadyHere} save file(s) already here");

                if (Mods > 0)
                {
                    lines.Add(BesideYours.Count > 0
                        ? $"{Mods} mod(s), {string.Join(", ", BesideYours)} beside the one you have"
                        : $"{Mods} mod(s)");
                }

                if (ModsAlreadyHere > 0)
                {
                    lines.Add(Shipped.Count > 0
                        ? $"{ModsAlreadyHere} mod(s) already here (this client ships {string.Join(", ", Shipped)})"
                        : $"{ModsAlreadyHere} mod(s) already here");
                }

                return lines;
            }
        }
    }

    /// <summary>Counts <paramref name="oldFolder"/>'s two libraries without touching anything.</summary>
    public static Survey Look(string? oldFolder)
    {
        if (string.IsNullOrWhiteSpace(oldFolder) || !Directory.Exists(oldFolder)) return Survey.Nothing;

        try
        {
            int saves = 0;
            int categories = 0;
            int mods = 0;

            var saveRoot = new DirectoryInfo(Path.Combine(oldFolder, SaveFolderName));
            if (saveRoot.Exists)
            {
                foreach (var title in saveRoot.GetDirectories())
                {
                    foreach (var category in title.GetDirectories())
                    {
                        int files = category.GetFiles().Length;
                        if (files == 0) continue;

                        saves += files;
                        categories++;
                    }
                }
            }

            var modRoot = new DirectoryInfo(Path.Combine(oldFolder, ModFolderName));
            if (modRoot.Exists)
            {
                foreach (var title in modRoot.GetDirectories()) mods += title.GetDirectories().Length;
            }

            return new Survey(saves, categories, mods);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Survey.Nothing;
        }
    }

    /// <summary>
    /// Copies both libraries out of <paramref name="oldFolder"/>, which is the folder the old
    /// racman.exe and its config.txt sat in. <paramref name="shippedModsRoot"/> is the library this
    /// release ships, read only to recognise a mod the user never had to bring over in the first
    /// place; null when there is not one.
    /// </summary>
    public static Result Run(string? oldFolder, string saveFilesRoot, string modsRoot, string? shippedModsRoot)
    {
        if (string.IsNullOrWhiteSpace(oldFolder) || !Directory.Exists(oldFolder)) return Result.Nothing;

        var tally = new Tally();
        CopySaves(Path.Combine(oldFolder, SaveFolderName), saveFilesRoot, tally);
        CopyMods(Path.Combine(oldFolder, ModFolderName), modsRoot, shippedModsRoot, tally);

        return new Result(
            tally.Saves, tally.SavesAlreadyHere, tally.SavesRenamed,
            tally.Mods, tally.ModsAlreadyHere, tally.Shipped, tally.BesideYours);
    }

    /// <summary>What the copy has done so far, passed down rather than threaded back up.</summary>
    private sealed class Tally
    {
        public int Saves;
        public int SavesAlreadyHere;
        public int SavesRenamed;
        public int Mods;
        public int ModsAlreadyHere;

        public List<string> Shipped { get; } = new();

        public List<string> BesideYours { get; } = new();
    }

    // ---------------------------------------------------------------- save files

    /// <summary>
    /// Every file under <c>savefiles/&lt;TITLEID&gt;/&lt;category&gt;/</c>, into the same category
    /// of this client's library. A save is its bytes and not its name, so a file already here under
    /// any name at all is not imported a second time.
    /// </summary>
    private static void CopySaves(string source, string root, Tally tally)
    {
        if (!Directory.Exists(source)) return;

        var library = new SaveFileLibrary(root);

        foreach (var title in new DirectoryInfo(source).GetDirectories())
        {
            foreach (var category in title.GetDirectories())
            {
                var files = category.GetFiles();
                if (files.Length == 0) continue;

                string folder;
                try
                {
                    folder = library.EnsureCategory(title.Name, category.Name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                // What the destination category already holds, by length and CRC, so a save that is
                // here under another name is recognised as the save it is. Read once per category.
                var here = Index(folder);

                foreach (var file in files) CopySave(file, folder, here, tally);
            }
        }
    }

    private static void CopySave(FileInfo file, string folder, Dictionary<(long, uint), List<string>> here, Tally tally)
    {
        if ((file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return;

        try
        {
            var data = File.ReadAllBytes(file.FullName);
            var key = (file.Length, Crc32.Compute(data));
            if (here.TryGetValue(key, out var matches)
                && matches.Any(path => SameBytes(path, data)))
            {
                tally.SavesAlreadyHere++;
                return;
            }

            // The old client took any file name; this one needs the suffix, and a name already
            // taken by other bytes is written beside them rather than over them.
            var name = SaveFileLibrary.Sanitise(file.Name, "savefile");
            if (Path.GetExtension(name).Length == 0) name = SaveFileLibrary.EnsureExtension(name);

            var target = SaveFileLibrary.FreeName(
                folder, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
            File.WriteAllBytes(target, data);

            tally.Saves++;
            if (!string.Equals(Path.GetFileName(target), file.Name, StringComparison.Ordinal)) tally.SavesRenamed++;

            if (!here.TryGetValue(key, out var list)) here[key] = list = new List<string>(1);
            list.Add(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // One file that cannot be read or written stops nothing: the original is still in the
            // old folder, which is never touched, so there is always something to try again with.
        }
    }

    /// <summary>Every file in a folder by its length and CRC32, which is what a duplicate is found by.</summary>
    private static Dictionary<(long, uint), List<string>> Index(string folder)
    {
        var index = new Dictionary<(long, uint), List<string>>();
        if (!Directory.Exists(folder)) return index;

        foreach (var path in Directory.EnumerateFiles(folder))
        {
            try
            {
                var info = new FileInfo(path);
                var key = (info.Length, Crc32.Compute(File.ReadAllBytes(path)));
                if (!index.TryGetValue(key, out var list)) index[key] = list = new List<string>(1);
                list.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be read is one nothing can be compared against.
            }
        }

        return index;
    }

    /// <summary>The bytes of a file this client already has against the bytes being imported.</summary>
    private static bool SameBytes(string path, byte[] data)
    {
        try
        {
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- mods

    /// <summary>
    /// Every folder under <c>mods/&lt;TITLEID&gt;/</c>. A mod is the folder as a whole, so it is
    /// compared as a whole: one the release ships is not worth importing, and neither is one the
    /// user already has under any name. A folder name this client uses for something else is
    /// imported beside it, because that name is the id qwark loads the mod by and the user should
    /// see both of them.
    /// </summary>
    private static void CopyMods(string source, string root, string? shippedRoot, Tally tally)
    {
        if (!Directory.Exists(source)) return;

        var shipped = shippedRoot is null ? null : DataFolderMigration.ReadShippedManifest(shippedRoot);

        foreach (var title in new DirectoryInfo(source).GetDirectories())
        {
            string titleId = SaveFileLibrary.Sanitise(title.Name, "unknown");
            string destination = Path.Combine(root, titleId);

            foreach (var mod in title.GetDirectories())
            {
                try
                {
                    CopyMod(mod, destination, titleId, shippedRoot, shipped, tally);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // As with a save: the old folder still has it, so nothing is lost.
                }
            }
        }
    }

    private static void CopyMod(
        DirectoryInfo mod,
        string destination,
        string titleId,
        string? shippedRoot,
        HashSet<string>? shipped,
        Tally tally)
    {
        if (shippedRoot is not null && IsShipped(shipped, titleId, mod.Name))
        {
            var theirs = new DirectoryInfo(Path.Combine(shippedRoot, titleId, mod.Name));
            if (SameTree(mod, theirs))
            {
                tally.ModsAlreadyHere++;
                tally.Shipped.Add(mod.Name);
                return;
            }
        }

        var mine = Folders(destination);
        if (mine.Any(existing => SameTree(mod, existing)))
        {
            tally.ModsAlreadyHere++;
            return;
        }

        string name = mod.Name;
        if (mine.Any(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = FreeFolderName(destination, mod.Name);
            tally.BesideYours.Add(name);
        }

        CopyTree(mod, new DirectoryInfo(Path.Combine(destination, name)));
        tally.Mods++;
    }

    /// <summary>
    /// Whether a mod folder name is one this release shipped. With no <c>shipped.txt</c> beside the
    /// shipped library — a development tree — everything in it is the release's, which is the same
    /// answer the client's own mod library gives.
    /// </summary>
    private static bool IsShipped(HashSet<string>? shipped, string titleId, string dirName) =>
        shipped is null || shipped.Contains($"{titleId}/{dirName}");

    /// <summary>
    /// The name an imported mod takes when this client already has a folder of that name holding
    /// something else: "<c>&lt;modfolder&gt; (imported)</c>", then "(imported 2)" after that.
    /// </summary>
    private static string FreeFolderName(string parent, string dirName)
    {
        string candidate = $"{dirName} (imported)";
        for (int n = 2; Directory.Exists(Path.Combine(parent, candidate)); n++)
        {
            candidate = $"{dirName} (imported {n})";
        }

        return candidate;
    }

    private static DirectoryInfo[] Folders(string path) =>
        Directory.Exists(path) ? new DirectoryInfo(path).GetDirectories() : Array.Empty<DirectoryInfo>();

    /// <summary>
    /// Whether two folders hold exactly the same files: the same names under the same subfolders,
    /// with the same bytes in each. A mod is the folder as a whole, so anything less than that is a
    /// different mod and is worth having beside the first.
    /// </summary>
    public static bool SameTree(DirectoryInfo left, DirectoryInfo right)
    {
        if (!left.Exists || !right.Exists) return false;

        var ours = Tree(left);
        var theirs = Tree(right);
        if (ours.Count != theirs.Count) return false;

        foreach (var (relative, file) in ours)
        {
            if (!theirs.TryGetValue(relative, out var other)) return false;
            if (file.Length != other.Length) return false;
            if (!SaveFileLibrary.SameContent(file.FullName, other.FullName)) return false;
        }

        return true;
    }

    /// <summary>Every file under a folder, by its path relative to it.</summary>
    private static Dictionary<string, FileInfo> Tree(DirectoryInfo folder)
    {
        var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        int prefix = folder.FullName.Length + 1;

        foreach (var file in folder.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            files[file.FullName[prefix..].Replace('\\', '/')] = file;
        }

        return files;
    }

    /// <summary>Copies a folder and everything under it, never replacing a file already there.</summary>
    private static void CopyTree(DirectoryInfo source, DirectoryInfo destination)
    {
        Directory.CreateDirectory(destination.FullName);

        foreach (var file in source.GetFiles())
        {
            var target = Path.Combine(destination.FullName, file.Name);
            if (!File.Exists(target)) file.CopyTo(target);
        }

        foreach (var child in source.GetDirectories())
        {
            CopyTree(child, new DirectoryInfo(Path.Combine(destination.FullName, child.Name)));
        }
    }
}
