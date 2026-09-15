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
/// Every save file comes over. Not every mod does: the old defaults this client decided against
/// are named in <see cref="LegacyModExclusions"/>, and a savefile helper or a mod that drives a Lua
/// script is left behind whatever that file says. Each one is reported with its reason, because a
/// mod that quietly did not arrive is a mod the user goes looking for.
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

    /// <summary>What makes a folder under a title a mod at all, in both libraries.</summary>
    public const string PatchFileName = "patch.txt";

    /// <summary>
    /// <c>mods/libs/</c> in the old folder: the Lua helpers its scripts shared, sitting where a
    /// title id otherwise does. Not a title, so never a folder of mods.
    /// </summary>
    public const string LuaLibraryFolder = "libs";

    /// <summary>Why a mod that drives a Lua script is left where it is.</summary>
    public const string NeedsLuaReason = "needs Lua";

    /// <summary>Why a savefile helper is left where it is, whatever it calls itself.</summary>
    public const string BuiltIntoQwarkReason = "built into qwark";

    /// <summary>A mod the import left in the old folder, and the reason it gives for it.</summary>
    public sealed record ExcludedMod(string TitleId, string DirName, string Reason);

    /// <summary>
    /// What the old folder holds, counted before anything is copied, so the panel can say what
    /// pressing the button would bring over. A folder without either library is simply nothing.
    /// The mods this client does not import are counted apart from the ones it does, because the
    /// number to press the button for is the second one.
    /// </summary>
    public sealed record Survey(int Saves, int Categories, int Mods, int Excluded)
    {
        public static readonly Survey Nothing = new(0, 0, 0, 0);

        public bool Anything => Saves > 0 || Mods > 0;

        /// <summary>"12 saves in 3 categories, 4 mods, 6 excluded", in the words the Settings panel prints.</summary>
        public string Describe()
        {
            var parts = new List<string>(3);
            if (Saves > 0)
            {
                parts.Add($"{Saves} save{(Saves == 1 ? string.Empty : "s")} in "
                          + $"{Categories} categor{(Categories == 1 ? "y" : "ies")}");
            }

            if (Mods > 0) parts.Add($"{Mods} mod{(Mods == 1 ? string.Empty : "s")}");
            if (Excluded > 0) parts.Add($"{Excluded} excluded");

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
        IReadOnlyList<string> BesideYours,
        IReadOnlyList<ExcludedMod> Excluded)
    {
        public static readonly Result Nothing =
            new(0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ExcludedMod>());

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

                if (Excluded.Count > 0) lines.Add(DescribeExcluded(Excluded));

                return lines;
            }
        }

        /// <summary>
        /// "excluded: sfhelper and rc2-save (built into qwark), flight and ohko (need Lua)": every
        /// mod that was left behind, gathered under the reason it was left behind for, in the order
        /// the reasons first came up. A folder name that appears under two titles — three games
        /// have a "flight" — is said once, because the reason is what the line is about.
        /// </summary>
        private static string DescribeExcluded(IReadOnlyList<ExcludedMod> excluded)
        {
            var order = new List<string>();
            var byReason = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var mod in excluded)
            {
                if (!byReason.TryGetValue(mod.Reason, out var names))
                {
                    byReason[mod.Reason] = names = new List<string>(1);
                    order.Add(mod.Reason);
                }

                if (!names.Contains(mod.DirName, StringComparer.OrdinalIgnoreCase)) names.Add(mod.DirName);
            }

            var groups = order.Select(reason => $"{Names(byReason[reason])} ({Agree(reason, byReason[reason].Count)})");
            return $"excluded: {string.Join(", ", groups)}";
        }

        /// <summary>"sfhelper", "sfhelper and rc2-save", "sfhelper, rc2-save and rc3-save".</summary>
        private static string Names(IReadOnlyList<string> names) => names.Count switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
        };

        /// <summary>
        /// The reason read as a sentence about the mods in front of it: one "needs Lua", two
        /// "need Lua". Only the reasons written that way have a plural to agree with.
        /// </summary>
        private static string Agree(string reason, int count) =>
            count > 1 && reason.StartsWith("needs ", StringComparison.OrdinalIgnoreCase)
                ? $"need {reason["needs ".Length..]}"
                : reason;
    }

    /// <summary>
    /// Counts <paramref name="oldFolder"/>'s two libraries without touching anything, under the
    /// same rules the import itself applies, so the number it shows is the number that lands.
    /// </summary>
    public static Survey Look(string? oldFolder, LegacyModExclusions exclusions)
    {
        if (string.IsNullOrWhiteSpace(oldFolder) || !Directory.Exists(oldFolder)) return Survey.Nothing;

        try
        {
            int saves = 0;
            int categories = 0;
            int mods = 0;
            int excluded = 0;

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
                foreach (var title in modRoot.GetDirectories())
                {
                    if (IsLuaLibrary(title.Name)) continue;

                    string titleId = SaveFileLibrary.Sanitise(title.Name, "unknown");
                    foreach (var mod in title.GetDirectories())
                    {
                        if (!IsMod(mod)) continue;

                        if (ExcludedReason(exclusions, titleId, mod) is null) mods++;
                        else excluded++;
                    }
                }
            }

            return new Survey(saves, categories, mods, excluded);
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
    /// place; null when there is not one. <paramref name="exclusions"/> is the shipped list of the
    /// old defaults this client decided against, which the tests hand their own.
    /// </summary>
    public static Result Run(
        string? oldFolder,
        string saveFilesRoot,
        string modsRoot,
        string? shippedModsRoot,
        LegacyModExclusions exclusions)
    {
        if (string.IsNullOrWhiteSpace(oldFolder) || !Directory.Exists(oldFolder)) return Result.Nothing;

        var tally = new Tally();
        CopySaves(Path.Combine(oldFolder, SaveFolderName), saveFilesRoot, tally);
        CopyMods(Path.Combine(oldFolder, ModFolderName), modsRoot, shippedModsRoot, exclusions, tally);

        return new Result(
            tally.Saves, tally.SavesAlreadyHere, tally.SavesRenamed,
            tally.Mods, tally.ModsAlreadyHere, tally.Shipped, tally.BesideYours, tally.Excluded);
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

        public List<ExcludedMod> Excluded { get; } = new();
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
    /// Every folder under <c>mods/&lt;TITLEID&gt;/</c>, in the order the rules are asked:
    /// <c>libs/</c> is the old Lua library and not a title; a folder with no patch.txt is not a mod
    /// and is not worth a word either way; a mod this client decided against is left behind with
    /// the reason it was left behind for; and only then is the copy itself considered.
    /// <para>
    /// A mod is the folder as a whole, so it is compared as a whole: one the release ships is not
    /// worth importing, and neither is one the user already has under any name. A folder name this
    /// client uses for something else is imported beside it, because that name is the id qwark
    /// loads the mod by and the user should see both of them.
    /// </para>
    /// </summary>
    private static void CopyMods(
        string source,
        string root,
        string? shippedRoot,
        LegacyModExclusions exclusions,
        Tally tally)
    {
        if (!Directory.Exists(source)) return;

        var shipped = shippedRoot is null ? null : DataFolderMigration.ReadShippedManifest(shippedRoot);

        foreach (var title in new DirectoryInfo(source).GetDirectories())
        {
            if (IsLuaLibrary(title.Name)) continue;

            string titleId = SaveFileLibrary.Sanitise(title.Name, "unknown");
            string destination = Path.Combine(root, titleId);

            foreach (var mod in title.GetDirectories())
            {
                if (!IsMod(mod)) continue;

                if (ExcludedReason(exclusions, titleId, mod) is { } reason)
                {
                    tally.Excluded.Add(new ExcludedMod(titleId, mod.Name, reason));
                    continue;
                }

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

    /// <summary>
    /// <c>mods/libs/</c> sits where a title id does, but it is the Lua the old client's scripts
    /// shared and not a game's mods, so nothing under it is ever imported or reported.
    /// </summary>
    private static bool IsLuaLibrary(string dirName) =>
        string.Equals(dirName, LuaLibraryFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a folder under a title is a mod at all. A mod is its patch.txt, and the old library
    /// also holds the source a few of them were built from — a Makefile and a <c>src/</c>, no patch
    /// file — which is nothing to import and nothing to say anything about.
    /// </summary>
    private static bool IsMod(DirectoryInfo mod) => File.Exists(Path.Combine(mod.FullName, PatchFileName));

    /// <summary>
    /// Why a mod stays in the old folder, or null when it comes over. The shipped list is asked
    /// first because it names its own reason; the two rules after it hold whatever the list says,
    /// since a copy of either kind can be sitting under any folder name at all.
    /// </summary>
    private static string? ExcludedReason(LegacyModExclusions exclusions, string titleId, DirectoryInfo mod)
    {
        if (exclusions.Reason(titleId, mod.Name) is { } listed) return listed;
        if (IsSavefileHelper(mod)) return BuiltIntoQwarkReason;
        return NeedsLua(mod) ? NeedsLuaReason : null;
    }

    /// <summary>
    /// Whether a mod is one of the savefile helpers, which qwark does itself now. Several releases
    /// of the old client shipped one under several names — "Savefile helper" in RaC1 and RaC4,
    /// "Savefile Manager" in RaC2 and RaC3 — so it is recognised by what its patch.txt calls it as
    /// well as by the folder names those copies used, and a renamed copy is caught by the first.
    /// </summary>
    private static bool IsSavefileHelper(DirectoryInfo mod) =>
        IsSavefileHelperName(mod.Name) || IsSavefileHelperTitle(PatchName(mod));

    /// <summary>The folder names the helpers were kept under: <c>sfhelper</c>, <c>rc2-save</c>, ...</summary>
    private static bool IsSavefileHelperName(string dirName) =>
        dirName.Equals("sfhelper", StringComparison.OrdinalIgnoreCase)
        || dirName.EndsWith("-save", StringComparison.OrdinalIgnoreCase)
        || dirName.Contains("sfhelper", StringComparison.OrdinalIgnoreCase)
        || dirName.Contains("savefile", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the mod calls itself, whatever its folder is called.</summary>
    private static bool IsSavefileHelperTitle(string? name) =>
        name is not null
        && (name.Contains("savefile helper", StringComparison.OrdinalIgnoreCase)
            || name.Contains("savefile manager", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The <c>#- name:</c> header of a mod's patch.txt, the one the mod list shows, or null when it
    /// has none. The same header <see cref="ModLibrary"/> reads; this walks a folder that is not a
    /// library yet, so it reads the one line it needs rather than the whole mod.
    /// </summary>
    private static string? PatchName(DirectoryInfo mod)
    {
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(mod.FullName, PatchFileName)))
            {
                if (!line.StartsWith("#-", StringComparison.Ordinal)) continue;

                var fields = line[2..].Split(':', 2);
                if (fields.Length == 2 && fields[0].Trim().Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    return fields[1].Trim();
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a mod drives a Lua script: an <c>automation:</c> line in its patch.txt, which is the
    /// test publish.ps1 applies to the shipped library, or a .lua file anywhere under the folder.
    /// qwark does not run Lua, so importing one would be importing a checkbox that does nothing.
    /// </summary>
    private static bool NeedsLua(DirectoryInfo mod)
    {
        try
        {
            foreach (var file in mod.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (file.Extension.Equals(".lua", StringComparison.OrdinalIgnoreCase)) return true;
            }

            foreach (var line in File.ReadLines(Path.Combine(mod.FullName, PatchFileName)))
            {
                if (IsAutomationLine(line)) return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read is not one to decide about: let the copy try it and
            // report what it could not take.
            return false;
        }
    }

    /// <summary>publish.ps1's <c>^\s*automation\s*:</c>, in the words C# has for it.</summary>
    private static bool IsAutomationLine(string line)
    {
        var text = line.TrimStart();
        if (!text.StartsWith("automation", StringComparison.OrdinalIgnoreCase)) return false;

        return text["automation".Length..].TrimStart().StartsWith(':');
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
