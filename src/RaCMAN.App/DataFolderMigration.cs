namespace RaCMAN.App;

/// <summary>
/// Brings the files of a build that kept everything beside the executable into the data folder,
/// once, on the first start that finds the data folder empty.
/// <para>
/// Nothing is ever deleted. The originals stay where they are, so an older build started from the
/// same folder still finds them and a migration that went wrong costs nobody anything; the copy
/// only happens when the data folder has no settings file of its own, so it cannot overwrite what
/// a newer run has since written.
/// </para>
/// <para>
/// Mods are the one thing that has to be sorted rather than copied wholesale, because the shipped
/// library and the user's own used to share <c>mods/</c>. The release writes what it shipped into
/// <c>mods/shipped.txt</c>, and everything not on that list is the user's. A folder with no such
/// list is a development tree, where the mods are the repo's and nothing is at risk, so nothing is
/// taken from it.
/// </para>
/// </summary>
public static class DataFolderMigration
{
    /// <summary>What one run of the migration did. <see cref="Moved"/> is empty when there was nothing to move.</summary>
    public sealed record Result(bool Ran, IReadOnlyList<string> Moved)
    {
        public static readonly Result NotNeeded = new(false, Array.Empty<string>());

        public bool MovedAnything => Moved.Count > 0;

        /// <summary>The toast: what moved, in the order it was found.</summary>
        public string Describe(string dataFolder) =>
            $"Your {string.Join(", ", Moved)} moved to {dataFolder}. The originals were left where they were.";
    }

    /// <summary>The folders that are copied whole, in the order the toast names them.</summary>
    private static readonly string[] Folders = { "colours", "watchlists", "savefiles" };

    /// <summary>
    /// Copies whatever an older build left beside the executable into the data folder. Does nothing
    /// once the data folder has a settings file, and nothing at all when the two folders are the
    /// same one, which is what a platform with nowhere to put application data falls back to.
    /// </summary>
    public static Result Run(string applicationFolder, string dataFolder)
    {
        try
        {
            return Copy(applicationFolder, dataFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException)
        {
            // A migration is a convenience. A client that cannot start because of one is not.
            return Result.NotNeeded;
        }
    }

    private static Result Copy(string applicationFolder, string dataFolder)
    {
        if (string.IsNullOrEmpty(applicationFolder) || string.IsNullOrEmpty(dataFolder)) return Result.NotNeeded;
        if (Same(applicationFolder, dataFolder)) return Result.NotNeeded;
        if (File.Exists(Path.Combine(dataFolder, AppPaths.SettingsFileName))) return Result.NotNeeded;

        var moved = new List<string>();

        var settings = Path.Combine(applicationFolder, AppPaths.SettingsFileName);
        if (File.Exists(settings))
        {
            Directory.CreateDirectory(dataFolder);
            File.Copy(settings, Path.Combine(dataFolder, AppPaths.SettingsFileName), overwrite: false);
            moved.Add("settings");
        }

        foreach (var name in Folders)
        {
            var source = new DirectoryInfo(Path.Combine(applicationFolder, name));
            if (!source.Exists) continue;

            CopyTree(source, new DirectoryInfo(Path.Combine(dataFolder, name)));
            moved.Add(name);
        }

        int mods = CopyUserMods(Path.Combine(applicationFolder, "mods"), Path.Combine(dataFolder, "mods"));
        if (mods > 0) moved.Add(mods == 1 ? "1 mod" : $"{mods} mods");

        return new Result(true, moved);
    }

    /// <summary>
    /// Copies the mods the user added, which are the ones the release did not ship. Returns how
    /// many folders were taken.
    /// </summary>
    private static int CopyUserMods(string source, string destination)
    {
        if (!Directory.Exists(source)) return 0;

        var shipped = ReadShippedManifest(source);
        if (shipped is null) return 0;

        int copied = 0;
        foreach (var title in new DirectoryInfo(source).GetDirectories())
        {
            // A whole title folder the release never shipped, mods and all.
            if (!shipped.Contains(title.Name))
            {
                CopyTree(title, new DirectoryInfo(Path.Combine(destination, title.Name)));
                copied++;
                continue;
            }

            foreach (var mod in title.GetDirectories())
            {
                if (shipped.Contains($"{title.Name}/{mod.Name}")) continue;

                CopyTree(mod, new DirectoryInfo(Path.Combine(destination, title.Name, mod.Name)));
                copied++;
            }
        }

        return copied;
    }

    /// <summary>
    /// The folders under <c>mods/</c> the release staged, as <c>TITLEID</c> and
    /// <c>TITLEID/modfolder</c> entries. Null when there is no manifest at all, which means nothing
    /// there can be told apart and nothing should be taken.
    /// </summary>
    public static HashSet<string>? ReadShippedManifest(string modsFolder)
    {
        var path = Path.Combine(modsFolder, AppPaths.ShippedModsManifest);
        if (!File.Exists(path)) return null;

        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path))
        {
            var entry = line.Trim().Replace('\\', '/').Trim('/');
            if (entry.Length == 0 || entry[0] == '#') continue;
            entries.Add(entry);
        }

        return entries;
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

    private static bool Same(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
