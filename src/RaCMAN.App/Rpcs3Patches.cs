using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using RaCMAN.Protocol;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace RaCMAN.App;

/// <summary>
/// What the search for RPCS3's folder looks at: the platform, the environment, the home folder and
/// the RPCS3 programs that are running. <see cref="Current"/> is this machine; a test hands in one
/// of its own, so no test ever looks at a real RPCS3.
/// </summary>
public sealed record Rpcs3Environment(
    bool IsWindows,
    bool IsMacOS,
    Func<string, string?> Variable,
    string Home,
    Func<IReadOnlyList<string>> RunningExecutables)
{
    public static Rpcs3Environment Current { get; } = new(
        OperatingSystem.IsWindows(),
        OperatingSystem.IsMacOS(),
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Rpcs3Patches.RunningRpcs3Executables);
}

/// <summary>
/// RPCS3's configuration folder, which RPCS3's own source calls <c>fs::get_config_dir()</c>, and the
/// three places in and around it this client reads or writes.
/// <para>
/// The layout is RPCS3's, checked in its source: patch files are in <c>patches/</c> under the
/// folder on every platform, <c>patch_config.yml</c> is in <c>fs::get_config_dir(true)</c>, which is
/// a <c>config/</c> subfolder on Windows and the folder itself elsewhere, and the log is
/// <c>fs::get_log_dir() + "RPCS3.log"</c>, which is <c>log/</c> under the folder on Windows and the
/// cache folder elsewhere. Older Windows builds wrote the log beside rpcs3.exe, so that is a
/// candidate too, and the newest file of the candidates is the one read.
/// </para>
/// </summary>
public sealed record Rpcs3Folder(string Root, bool WindowsLayout, IReadOnlyList<string> LogCandidates, string FoundBy)
{
    public string PatchesFolder => Path.Combine(Root, "patches");

    public string PatchConfigFile => WindowsLayout
        ? Path.Combine(Root, "config", Rpcs3Patches.PatchConfigName)
        : Path.Combine(Root, Rpcs3Patches.PatchConfigName);

    /// <summary>The per-title patch file RPCS3 loads for <paramref name="titleId"/> at every boot of it.</summary>
    public string PatchFile(string titleId) => Path.Combine(PatchesFolder, titleId + "_patch.yml");

    /// <summary>The log candidate written last, or null when none of them exists.</summary>
    public string? NewestLog()
    {
        string? newest = null;
        var newestTime = DateTime.MinValue;

        foreach (var candidate in LogCandidates)
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var written = File.GetLastWriteTimeUtc(candidate);
                if (newest is not null && written <= newestTime) continue;

                newest = candidate;
                newestTime = written;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A candidate that cannot even be looked at is not the log.
            }
        }

        return newest;
    }
}

/// <summary>Where RPCS3 is, or why it was not found.</summary>
public sealed record Rpcs3FolderLookup(Rpcs3Folder? Folder, string Problem);

/// <summary>
/// The PPU executable hash of the running game, read out of RPCS3's log, or why it could not be.
/// <see cref="PatchApplied"/> says the log also shows RPCS3 applying the savefile helper at that
/// boot, and <see cref="Applied"/> names every patch of this executable the log shows RPCS3
/// applying at that boot, by its description: the helper, the code switches and the mods alike.
/// </summary>
public sealed record ExecutableHashLookup(
    string? Hash,
    string Problem,
    bool PatchApplied = false,
    string? Log = null,
    IReadOnlyList<string>? Applied = null)
{
    /// <summary>Whether RPCS3 applied the patch filed under <paramref name="description"/> when this boot loaded the executable.</summary>
    public bool AppliedAtBoot(string description) =>
        Applied is not null
            ? Applied.Contains(description, StringComparer.Ordinal)
            : PatchApplied && description == Rpcs3Patches.Description;
}

public enum PatchFileKind
{
    /// <summary>There is no patch file for the title yet.</summary>
    Missing,

    /// <summary>The file carries RaCMAN's marker: this client wrote it and may rewrite it.</summary>
    Ours,

    /// <summary>A file without the marker: somebody else's, never overwritten.</summary>
    Foreign,

    /// <summary>The marker is there but the file cannot be read back.</summary>
    Broken,
}

/// <summary>
/// One of this client's entries in RaCMAN's patch file: the executable's hash, the description it
/// is filed under (the savefile helper's, the code switches' or one mod's), the game it is for,
/// the words and the single bytes, and the three lines RPCS3's Patch Manager shows about it. What
/// is read back is everything that is written, so an entry that is kept is written back exactly as
/// it was.
/// </summary>
public sealed record PatchFileEntry(
    string Hash,
    string Game,
    IReadOnlyList<PatchWord> Words,
    IReadOnlyList<PatchByte> Bytes,
    string Notes,
    string Description = Rpcs3Patches.Description,
    string Author = Rpcs3Patches.Author,
    string PatchVersion = Rpcs3Patches.PatchVersion)
{
    /// <summary>Whether this entry holds exactly the words and bytes of <paramref name="patch"/>.</summary>
    public bool Holds(PatchReply patch) => patch.SameWords(Words, Bytes);

    /// <summary>qwark's savefile helper.</summary>
    public bool IsHelper => Description == Rpcs3Patches.Description;

    /// <summary>qwark's code switches (build 50).</summary>
    public bool IsSwitches => Description == Rpcs3Patches.SwitchesDescription;

    /// <summary>
    /// One of qwark's own patches, the savefile helper or the code switches: the game's rather than
    /// the user's, installed from the Connection panel, and never switched off from the Mods panel.
    /// </summary>
    public bool IsQwarks => IsHelper || IsSwitches;

    /// <summary>The folder name of the mod this entry is, or null for one of qwark's own patches.</summary>
    public string? ModDir => Rpcs3Patches.ModDirOf(Description);

    /// <summary>Where patch_config.yml switches this entry on for <paramref name="serial"/>.</summary>
    public PatchConfigKey Key(string serial) => new(Hash, Game, serial, Description);

    /// <summary>
    /// Whether this and <paramref name="other"/> are the same patch of the same executable, so that
    /// writing one replaces the other: the helper and the helper, the switches and the switches, or
    /// two entries of one mod folder even when the mod has been renamed since.
    /// </summary>
    public bool SameSlot(PatchFileEntry other)
    {
        if (!string.Equals(Hash, other.Hash, StringComparison.OrdinalIgnoreCase)) return false;
        if (Description == other.Description) return true;
        return ModDir is { } dir && string.Equals(dir, other.ModDir, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>What is in a title's patch file, as far as this client is concerned.</summary>
public sealed record PatchFileState(string Path, PatchFileKind Kind, IReadOnlyList<PatchFileEntry> Entries, string Problem)
{
    /// <summary>The entry of one executable filed under <paramref name="description"/>, the savefile helper's by default.</summary>
    public PatchFileEntry? EntryFor(string hash, string description = Rpcs3Patches.Description) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Hash, hash, StringComparison.OrdinalIgnoreCase)
                                        && entry.Description == description);

    /// <summary>The entry of one executable for the mod in folder <paramref name="dirName"/>, whatever the mod was called when it was written.</summary>
    public PatchFileEntry? ModEntryFor(string hash, string dirName) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Hash, hash, StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(entry.ModDir, dirName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every entry of one executable.</summary>
    public IEnumerable<PatchFileEntry> EntriesFor(string hash) =>
        Entries.Where(entry => string.Equals(entry.Hash, hash, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Where one of this client's patches is switched on in patch_config.yml.</summary>
public sealed record PatchConfigKey(string Hash, string Game, string Serial, string Description = Rpcs3Patches.Description);

/// <summary>
/// Everything a write is about to do, worked out before a byte of it is written. A null
/// <see cref="PatchText"/> leaves the patch file as it is, which is what switching a patch off does.
/// </summary>
public sealed record Rpcs3PatchPlan(
    string PatchFile,
    string? PatchText,
    bool PatchFileExists,
    string ConfigFile,
    string ConfigText,
    bool ConfigExists,
    IReadOnlyList<PatchFileEntry> Entries);

/// <summary>An install or a check that cannot go ahead, with the reason in words the user can act on.</summary>
public sealed class Rpcs3PatchException : Exception
{
    public Rpcs3PatchException(string message) : base(message) { }
}

/// <summary>
/// qwark's savefile helper, qwark's code switches and the mods as RPCS3 patches: finding RPCS3's
/// folder, reading the running game's executable hash out of its log, writing the per-title patch
/// file and switching its entries on and off in patch_config.yml. Nothing here draws anything and
/// nothing here knows an address: the words come from qwark (SAVEFILE_PATCH, SWITCH_PATCH,
/// MOD_PATCH) and are copied into the file as they are, and the only thing ever worked out from
/// them is which addresses they cover.
/// <para>
/// Why a file at all: RPCS3 recompiles the game's code, so qwark cannot write the helper, a code
/// switch or a mod into a running game the way it does on a console. RPCS3 does apply patch files
/// when it loads the executable, before it compiles anything, so they go in as patches and the game
/// has to be started again to take them.
/// </para>
/// <para>
/// The file holds any number of this client's entries, per executable hash: the helper, filed
/// under <see cref="Description"/>, the code switches, filed under <see cref="SwitchesDescription"/>,
/// and one per mod, filed under <see cref="ModDescription"/>. Every write reads the file first and
/// writes every entry it is not about back exactly as it was.
/// </para>
/// </summary>
public static class Rpcs3Patches
{
    /// <summary>The description the savefile helper is filed under, in the file and in RPCS3's Patch Manager.</summary>
    public const string Description = "qwark savefile helper";

    /// <summary>
    /// The description the code switches are filed under (qwark build 50): every instruction a
    /// WRITES_CODE feature patches, turned into a branch to a trampoline that reads that feature's
    /// flag byte, so qwark-rpcs3 toggles those features by writing data.
    /// </summary>
    public const string SwitchesDescription = "qwark code switches";

    /// <summary>How the description of a mod's entry begins; <see cref="ModDescription"/> has the rest.</summary>
    public const string ModDescriptionPrefix = "RaCMAN mod: ";

    public const string Author = "qwark";

    /// <summary>The author a mod's entry names when the mod names none.</summary>
    public const string ModAuthorFallback = "RaCMAN Reloaded";

    public const string PatchVersion = "1.0";

    /// <summary>
    /// RPCS3's <c>patch_engine_version</c>: a file whose <c>Version</c> is anything else, or missing,
    /// is rejected whole.
    /// </summary>
    public const string EngineVersion = "1.2";

    /// <summary>RPCS3's <c>patch_key::all</c>, the app version that matches every version of a serial.</summary>
    public const string AllVersions = "All";

    public const string PatchConfigName = "patch_config.yml";

    /// <summary>The copy of patch_config.yml kept beside it before this client first changes it in a session.</summary>
    public const string BackupSuffix = ".racman-bak";

    public const string LogName = "RPCS3.log";

    /// <summary>How the first line of a patch file this client owns begins. A file without it is left alone.</summary>
    public const string Marker = "# RaCMAN Reloaded writes this file";

    /// <summary>
    /// The first line of the file. Older builds wrote other words after the marker; theirs begin
    /// with <see cref="Marker"/> too, so their files are still this client's.
    /// </summary>
    public const string MarkerLine = Marker + ": qwark's savefile helper and code switches, and the mods enabled in RaCMAN. "
                                     + "Edits made here are lost.";

    /// <summary>
    /// A mod's description: its name, and its folder in brackets, which is what makes two mods of
    /// the same name two entries and what finds the entry again after the mod is renamed.
    /// </summary>
    public static string ModDescription(string name, string dirName)
    {
        string shown = string.IsNullOrWhiteSpace(name) ? dirName : name.Trim();
        return $"{ModDescriptionPrefix}{shown} [{dirName}]";
    }

    /// <summary>The mod folder a description names, or null when it is not a mod's.</summary>
    public static string? ModDirOf(string description)
    {
        if (!description.StartsWith(ModDescriptionPrefix, StringComparison.Ordinal) || !description.EndsWith(']')) return null;

        int open = description.LastIndexOf(" [", StringComparison.Ordinal);
        if (open < ModDescriptionPrefix.Length) return null;

        string dir = description[(open + 2)..^1];
        return dir.Length == 0 ? null : dir;
    }

    /// <summary>
    /// What to call the patch filed under <paramref name="description"/> in a sentence. The code
    /// switches go by the name RPCS3's Patch Manager lists them under, which is the name a user
    /// who looks there will find.
    /// </summary>
    public static string NameOf(string description)
    {
        if (description == Description) return "qwark's savefile helper";
        if (description == SwitchesDescription) return "the " + SwitchesDescription;
        if (ModDirOf(description) is null) return description;

        int open = description.LastIndexOf(" [", StringComparison.Ordinal);
        return description[ModDescriptionPrefix.Length..open];
    }

    /// <summary>Whether this client wrote the entry filed under <paramref name="description"/>.</summary>
    public static bool IsOurs(string description) =>
        description == Description || description == SwitchesDescription || ModDirOf(description) is not null;

    /// <summary>The Flatpak's application id, whose sandbox keeps RPCS3's folders under ~/.var/app.</summary>
    public const string FlatpakId = "net.rpcs3.RPCS3";

    private const string SerialMarker = "SYS: Serial: ";

    private const string HashMarker = "PPU executable hash: ";

    private const string HashPrefix = "PPU-";

    // ---------------------------------------------------------------- finding RPCS3

    /// <summary>
    /// Where RPCS3 keeps its patches. The folder set in the settings wins whenever it is set; after
    /// that it is RPCS3's own rule for its configuration folder (<c>fs::get_config_dir</c>): on
    /// Windows the folder of the running rpcs3.exe, or the <c>portable</c> folder beside it, or
    /// RPCS3_CONFIG_DIR; on Linux <c>$XDG_CONFIG_HOME/rpcs3</c> or <c>~/.config/rpcs3</c>, and the
    /// Flatpak's own copy of that; on macOS <c>~/Library/Application Support/rpcs3</c>.
    /// </summary>
    public static Rpcs3FolderLookup Locate(string? overrideFolder, Rpcs3Environment environment)
    {
        string wanted = CleanPath(overrideFolder, environment.Home);
        if (wanted.Length > 0) return LocateOverride(wanted, environment);

        return environment.IsWindows ? LocateOnWindows(environment) : LocateByConvention(environment);
    }

    private static Rpcs3FolderLookup LocateOverride(string wanted, Rpcs3Environment environment)
    {
        if (!Path.IsPathRooted(wanted))
        {
            return Missing($"The RPCS3 folder set below, {wanted}, is not a whole path. Enter it from the drive or the root.");
        }

        if (!Directory.Exists(wanted)) return Missing($"The RPCS3 folder set below, {wanted}, does not exist.");

        string root = PortableOr(wanted);
        if (!LooksLikeRpcs3(root))
        {
            return Missing($"{wanted} does not look like RPCS3's folder: it has no rpcs3 program, "
                           + "no config folder and no patches folder in it.");
        }

        return Found(root, environment, "set below");
    }

    private static Rpcs3FolderLookup LocateOnWindows(Rpcs3Environment environment)
    {
        foreach (var executable in environment.RunningExecutables())
        {
            string? folder = Path.GetDirectoryName(executable);
            if (string.IsNullOrEmpty(folder)) continue;

            // fs::get_config_dir's own order: a "portable" folder beside the program, then the
            // variable, then the program's folder. The variable read here is this process's, which
            // is the user's too unless somebody set it for RPCS3 alone.
            string portable = Path.Combine(folder, "portable");
            if (Directory.Exists(portable)) return Found(portable, environment, "found from the running rpcs3.exe");
            if (ConfigDirVariable(environment) is { } configured) return Found(configured, environment, "found from RPCS3_CONFIG_DIR");

            return Found(folder, environment, "found from the running rpcs3.exe");
        }

        if (ConfigDirVariable(environment) is { } fromVariable) return Found(fromVariable, environment, "found from RPCS3_CONFIG_DIR");

        return Missing("No running rpcs3.exe was found, so RPCS3's folder is not known. Enter the folder below.");
    }

    /// <summary>
    /// Linux and macOS: the configuration folders RPCS3 itself uses. When more than one exists — a
    /// native build and the Flatpak side by side — the one whose log was written last is the RPCS3
    /// that is running.
    /// </summary>
    private static Rpcs3FolderLookup LocateByConvention(Rpcs3Environment environment)
    {
        var roots = ConventionalRoots(environment);

        Rpcs3Folder? best = null;
        var bestLog = DateTime.MinValue;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            var folder = Build(root, environment, "found in RPCS3's standard place");
            var log = folder.NewestLog();
            var written = log is null ? DateTime.MinValue : SafeWriteTime(log);
            if (best is not null && written <= bestLog) continue;

            best = folder;
            bestLog = written;
        }

        return best is null
            ? Missing($"RPCS3's folder was not found (looked in {string.Join(", ", roots)}). Enter it below.")
            : new Rpcs3FolderLookup(best, string.Empty);
    }

    /// <summary>fs::get_config_dir on Linux and macOS, and the Flatpak's copy of the Linux one.</summary>
    public static IReadOnlyList<string> ConventionalRoots(Rpcs3Environment environment)
    {
        var roots = new List<string>();
        if (environment.IsMacOS)
        {
            roots.Add(Path.Combine(environment.Home, "Library", "Application Support", "rpcs3"));
            return roots;
        }

        if (environment.Variable("XDG_CONFIG_HOME") is { Length: > 0 } config) roots.Add(Path.Combine(config, "rpcs3"));
        roots.Add(Path.Combine(environment.Home, ".config", "rpcs3"));
        roots.Add(Path.Combine(environment.Home, ".var", "app", FlatpakId, "config", "rpcs3"));
        return roots.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// RPCS3_CONFIG_DIR as RPCS3 reads it: backslashes become slashes and everything after the last
    /// slash is dropped, so the value has to end in a separator to name the folder itself. Null when
    /// it is not set or names no folder that exists.
    /// </summary>
    public static string? ConfigDirVariable(Rpcs3Environment environment)
    {
        string? value = environment.Variable("RPCS3_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(value)) return null;

        string slashed = value.Replace('\\', '/');
        int cut = slashed.LastIndexOf('/');
        if (cut < 0) return null;

        string folder = slashed[..(cut + 1)];
        return Directory.Exists(folder) ? TrimSeparators(Path.GetFullPath(folder)) : null;
    }

    /// <summary>The folder layout for a configuration folder, with its log candidates for this platform.</summary>
    public static Rpcs3Folder Build(string root, Rpcs3Environment environment, string foundBy)
    {
        root = TrimSeparators(root);
        var logs = new List<string>();

        if (environment.IsWindows)
        {
            // fs::get_log_dir is <config>/log/ on Windows; builds before it wrote beside the program.
            logs.Add(Path.Combine(root, "log", LogName));
            logs.Add(Path.Combine(root, LogName));
        }
        else
        {
            // fs::get_log_dir is fs::get_cache_dir here: $XDG_CACHE_HOME, else $XDG_CONFIG_HOME,
            // else ~/.cache on Linux and ~/Library/Caches on macOS, each with rpcs3/ under it.
            logs.Add(Path.Combine(root, LogName));
            logs.Add(Path.Combine(root, "log", LogName));
            if (environment.Variable("XDG_CACHE_HOME") is { Length: > 0 } cache) logs.Add(Path.Combine(cache, "rpcs3", LogName));
            if (environment.Variable("XDG_CONFIG_HOME") is { Length: > 0 } config) logs.Add(Path.Combine(config, "rpcs3", LogName));

            logs.Add(environment.IsMacOS
                ? Path.Combine(environment.Home, "Library", "Caches", "rpcs3", LogName)
                : Path.Combine(environment.Home, ".cache", "rpcs3", LogName));

            // The Flatpak points XDG_CONFIG_HOME and XDG_CACHE_HOME at config/ and cache/ side by
            // side under its own folder, so its log is the sibling of its configuration.
            var parent = Directory.GetParent(root);
            if (parent is { Name: "config" } && parent.Parent is { } sandbox)
            {
                logs.Add(Path.Combine(sandbox.FullName, "cache", "rpcs3", LogName));
            }
        }

        return new Rpcs3Folder(root, environment.IsWindows, logs.Distinct(StringComparer.Ordinal).ToList(), foundBy);
    }

    /// <summary>
    /// The rpcs3 programs running on this PC, by their full path. The one thing the search reads out
    /// of another process, and only its path: a process that will not say (it runs elevated, say) is
    /// skipped rather than fought with.
    /// </summary>
    public static IReadOnlyList<string> RunningRpcs3Executables()
    {
        var paths = new List<string>();
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("rpcs3");
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            return paths;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { Length: > 0 } file) paths.Add(file);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Gone, or not ours to ask.
                }
            }
        }

        return paths;
    }

    private static bool LooksLikeRpcs3(string folder) =>
        File.Exists(Path.Combine(folder, "rpcs3.exe"))
        || File.Exists(Path.Combine(folder, "rpcs3"))
        || File.Exists(Path.Combine(folder, "config.yml"))
        || Directory.Exists(Path.Combine(folder, "config"))
        || Directory.Exists(Path.Combine(folder, "patches"))
        || Directory.Exists(Path.Combine(folder, "dev_hdd0"));

    private static string PortableOr(string folder)
    {
        string portable = Path.Combine(folder, "portable");
        return Directory.Exists(portable) ? portable : folder;
    }

    private static Rpcs3FolderLookup Found(string root, Rpcs3Environment environment, string foundBy) =>
        new(Build(root, environment, foundBy), string.Empty);

    private static Rpcs3FolderLookup Missing(string problem) => new(null, problem);

    /// <summary>A path as typed: quotes and spaces trimmed, and a leading <c>~/</c> as the home folder.</summary>
    public static string CleanPath(string? typed, string home)
    {
        string path = (typed ?? string.Empty).Trim().Trim('"').Trim();
        if (path == "~") return home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = Path.Combine(home, path[2..]);
        }

        return TrimSeparators(path);
    }

    private static string TrimSeparators(string path)
    {
        string trimmed = path.TrimEnd('/', '\\');
        if (trimmed.Length == 0) return path;

        // "C:\" and "/" are roots, and a root keeps its separator.
        return trimmed.EndsWith(':') ? trimmed + Path.DirectorySeparatorChar : trimmed;
    }

    private static DateTime SafeWriteTime(string file)
    {
        try
        {
            return File.GetLastWriteTimeUtc(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    // ---------------------------------------------------------------- the executable hash

    /// <summary>
    /// The running game's executable hash, from the newest log in <paramref name="folder"/>. RPCS3
    /// keeps its log open while it runs, so the file is opened for reading with every kind of
    /// sharing allowed.
    /// </summary>
    public static ExecutableHashLookup FindExecutableHash(Rpcs3Folder folder, string titleId)
    {
        string? log = folder.NewestLog();
        if (log is null)
        {
            return new ExecutableHashLookup(null,
                $"RPCS3's log was not found ({string.Join(", ", folder.LogCandidates)}). "
                + $"Boot {titleId} in RPCS3 first, or check the RPCS3 folder.");
        }

        try
        {
            using var stream = new FileStream(log, System.IO.FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 16);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                bufferSize: 1 << 16);
            return FindExecutableHash(reader, titleId) with { Log = log };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ExecutableHashLookup(null, $"RPCS3's log {log} could not be read: {ex.Message}", Log: log);
        }
    }

    /// <summary>
    /// Reads a log from the top. Every boot logs <c>SYS: Serial: &lt;TITLEID&gt;</c> and then, once the
    /// executable is loaded, <c>ppu_loader: PPU executable hash: PPU-...</c>; the hash wanted is the
    /// last one after the last serial line, and only when that serial is the running title. A log
    /// whose last boot is another game, or that names no hash for this one yet, is a refusal with
    /// the reason, never a guess.
    /// </summary>
    public static ExecutableHashLookup FindExecutableHash(TextReader log, string titleId)
    {
        string? serial = null;
        string? hash = null;
        var applied = new List<string>();

        string? line;
        while ((line = log.ReadLine()) is not null)
        {
            int at = line.IndexOf(SerialMarker, StringComparison.Ordinal);
            if (at >= 0)
            {
                serial = FirstToken(line, at + SerialMarker.Length);
                hash = null;
                applied.Clear();
                continue;
            }

            if (serial is null) continue;

            at = line.IndexOf(HashMarker, StringComparison.Ordinal);
            if (at >= 0)
            {
                string token = FirstToken(line, at + HashMarker.Length);
                if (IsPpuHash(token))
                {
                    hash = token;
                    applied.Clear();
                }

                continue;
            }

            // patch_engine::apply's own line for a patch it applied, which comes after the hash.
            if (hash is not null && AppliedDescription(line, hash) is { } description && !applied.Contains(description))
            {
                applied.Add(description);
            }
        }

        if (serial is null)
        {
            return new ExecutableHashLookup(null, $"RPCS3's log shows no game booted. Boot {titleId} in RPCS3 first.");
        }

        if (!string.Equals(serial, titleId, StringComparison.OrdinalIgnoreCase))
        {
            return new ExecutableHashLookup(null,
                $"The last game RPCS3 booted is {serial}, not {titleId}. Boot {titleId} in RPCS3 first.");
        }

        return hash is null
            ? new ExecutableHashLookup(null,
                $"RPCS3's log has no executable hash for this boot of {titleId}. Boot the game in RPCS3 first.")
            : new ExecutableHashLookup(hash, string.Empty, applied.Contains(Description), Applied: applied.ToArray());
    }

    private const string AppliedMarker = "Applied patch (hash='";

    private const string AppliedDescriptionMarker = "', description='";

    private const string AppliedAuthorMarker = "', author='";

    /// <summary>
    /// The description in patch_engine::apply's line for a patch it applied to <paramref name="hash"/>,
    /// <c>Applied patch (hash='...', description='...', author='...', ...</c>, or null when the line
    /// is not one of those or is about another executable. RPCS3 does not escape the quotes, so the
    /// description runs up to the author that follows it.
    /// </summary>
    public static string? AppliedDescription(string line, string hash)
    {
        int at = line.IndexOf(AppliedMarker, StringComparison.Ordinal);
        if (at < 0) return null;

        var rest = line.AsSpan(at + AppliedMarker.Length);
        if (!rest.StartsWith(hash, StringComparison.Ordinal)) return null;

        rest = rest[hash.Length..];
        if (!rest.StartsWith(AppliedDescriptionMarker, StringComparison.Ordinal)) return null;

        rest = rest[AppliedDescriptionMarker.Length..];
        int end = rest.IndexOf(AppliedAuthorMarker, StringComparison.Ordinal);
        if (end < 0) end = rest.LastIndexOf('\'');
        return end < 0 ? null : rest[..end].ToString();
    }

    private static string FirstToken(string line, int start)
    {
        var rest = line.AsSpan(start).Trim();
        int space = rest.IndexOfAny(' ', '\t');
        return (space < 0 ? rest : rest[..space]).ToString();
    }

    /// <summary>"PPU-" and at least one hex digit, which is how RPCS3 names an executable in the log and in a patch.</summary>
    public static bool IsPpuHash(string? text)
    {
        if (text is null || text.Length <= HashPrefix.Length) return false;
        if (!text.StartsWith(HashPrefix, StringComparison.Ordinal)) return false;

        foreach (char c in text.AsSpan(HashPrefix.Length))
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }

        return true;
    }

    // ---------------------------------------------------------------- the patch file

    /// <summary>What <paramref name="path"/> holds: nothing, this client's file, somebody else's, or a broken one of ours.</summary>
    public static PatchFileState ReadPatchFile(string path)
    {
        if (!File.Exists(path)) return new PatchFileState(path, PatchFileKind.Missing, Array.Empty<PatchFileEntry>(), string.Empty);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PatchFileState(path, PatchFileKind.Broken, Array.Empty<PatchFileEntry>(),
                $"{path} could not be read: {ex.Message}");
        }

        return ParsePatchFile(text, path);
    }

    /// <summary>
    /// Reads a patch file back. Only a file whose first line begins with <see cref="Marker"/> is
    /// this client's; anything else is somebody's own patches for the title, and is reported rather
    /// than read. Of this client's file every entry it writes is taken, the helper's and every
    /// mod's, for every executable, in the order the file has them.
    /// </summary>
    public static PatchFileState ParsePatchFile(string text, string path)
    {
        string body = text.TrimStart('\uFEFF');
        int newline = body.IndexOf('\n');
        string first = (newline < 0 ? body : body[..newline]).TrimEnd('\r');

        if (!first.StartsWith(Marker, StringComparison.Ordinal))
        {
            return new PatchFileState(path, PatchFileKind.Foreign, Array.Empty<PatchFileEntry>(),
                $"{path} was not written by RaCMAN, so RaCMAN will not replace it. "
                + "Move or rename it, then check again.");
        }

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(body));

            var entries = new List<PatchFileEntry>();
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return new PatchFileState(path, PatchFileKind.Ours, entries, string.Empty);
            }

            foreach (var (key, value) in root.Children)
            {
                if (key is not YamlScalarNode { Value: { } hash } || !IsPpuHash(hash)) continue;
                if (value is not YamlMappingNode descriptions) continue;

                foreach (var (name, content) in descriptions.Children)
                {
                    if (name is not YamlScalarNode { Value: { } description } || !IsOurs(description)) continue;
                    if (content is not YamlMappingNode entry) continue;

                    entries.Add(ReadEntry(hash, description, entry));
                }
            }

            return new PatchFileState(path, PatchFileKind.Ours, entries, string.Empty);
        }
        catch (Exception ex) when (ex is YamlException or FormatException or OverflowException)
        {
            return new PatchFileState(path, PatchFileKind.Broken, Array.Empty<PatchFileEntry>(),
                $"{path} is RaCMAN's but could not be read back ({ex.Message}). Delete it, then install again.");
        }
    }

    private static PatchFileEntry ReadEntry(string hash, string description, YamlMappingNode entry)
    {
        string game = Child(entry, "Games") is YamlMappingNode games
                      && games.Children.Keys.FirstOrDefault() is YamlScalarNode { Value: { } title }
            ? title
            : string.Empty;

        string notes = Child(entry, "Notes") is YamlScalarNode { Value: { } text } ? text : string.Empty;
        string author = Child(entry, "Author") is YamlScalarNode { Value: { } by } ? by : Author;
        string version = Child(entry, "Patch Version") is YamlScalarNode { Value: { Length: > 0 } v } ? v : PatchVersion;

        var words = new List<PatchWord>();
        var bytes = new List<PatchByte>();
        if (Child(entry, "Patch") is YamlSequenceNode patch)
        {
            foreach (var item in patch.Children)
            {
                if (item is not YamlSequenceNode { Children.Count: 3 } parts
                    || parts.Children[0] is not YamlScalarNode { Value: { } type }
                    || parts.Children[1] is not YamlScalarNode { Value: { } address }
                    || parts.Children[2] is not YamlScalarNode { Value: { } value })
                {
                    throw new FormatException($"the entry \"{description}\" for {hash} holds a patch line that is not [ type, address, value ]");
                }

                switch (type)
                {
                    case "be32":
                        words.Add(new PatchWord(ParseHex(address), ParseHex(value)));
                        break;

                    case "byte":
                        bytes.Add(new PatchByte(ParseHex(address), checked((byte)ParseHex(value))));
                        break;

                    default:
                        throw new FormatException($"the entry \"{description}\" for {hash} holds a {type} line, which RaCMAN never writes");
                }
            }
        }

        return new PatchFileEntry(hash, game, words, bytes, notes, description, author, version);
    }

    private static uint ParseHex(string text)
    {
        string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return uint.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    /// <summary>What the Notes line of the helper's entry says: which qwark the words came from, and their stamp.</summary>
    public static string NotesFor(byte qwarkBuild, uint stamp) =>
        $"qwark build {qwarkBuild}, helper stamp 0x{stamp:x8}";

    /// <summary>What the Notes line of the code switches' entry says, the same way.</summary>
    public static string NotesForSwitches(byte qwarkBuild, uint stamp) =>
        $"qwark build {qwarkBuild}, switches stamp 0x{stamp:x8}";

    private const string LibraryHashLabel = "library hash ";

    /// <summary>
    /// What the Notes line of a mod's entry says: the CRC of the library copy the words were made
    /// from, which is the hash the client uploads the mod under and what tells a newer copy in the
    /// library from this one, then which qwark parsed it and the stamp of its reply.
    /// </summary>
    public static string NotesForMod(uint libraryHash, byte qwarkBuild, uint stamp) =>
        $"{LibraryHashLabel}{Crc32.ToSumText(libraryHash)}, qwark build {qwarkBuild}, patch stamp 0x{stamp:x8}";

    /// <summary>The library hash a mod entry's notes record, or null when they record none.</summary>
    public static uint? LibraryHashIn(string notes)
    {
        int at = notes.IndexOf(LibraryHashLabel, StringComparison.Ordinal);
        if (at < 0) return null;

        var digits = notes.AsSpan(at + LibraryHashLabel.Length);
        int end = 0;
        while (end < digits.Length && char.IsAsciiHexDigit(digits[end])) end++;

        return end == 8 && uint.TryParse(digits[..end], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint hash)
            ? hash
            : null;
    }

    /// <summary>
    /// The entries a rewrite keeps. The entry in the same slot as <paramref name="current"/> — the
    /// same executable and the same patch — is replaced where it stands, or <paramref name="current"/>
    /// goes at the end; every entry of any other patch is kept exactly as it was, for every hash.
    /// <para>
    /// qwark's own patches, the savefile helper and the code switches, are the ones that spread:
    /// every other executable of the same game gets the same words, because the patch is the
    /// game's, and somebody who runs two EBOOTs of one game gets both brought up to date. Such an
    /// entry for another game is kept as it was. That only happens on a title that hosts more than
    /// one game (BCES01503 hosts RaC1 to RaC3), where each game's executable has its own helper and
    /// its own switches, and one game's words written into another's would break it. A mod is
    /// written for the running executable only: its words are that executable's addresses.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PatchFileEntry> MergeEntries(IReadOnlyList<PatchFileEntry> existing, PatchFileEntry current)
    {
        var merged = new List<PatchFileEntry>();
        bool placed = false;

        foreach (var entry in existing)
        {
            if (merged.Any(kept => kept.SameSlot(entry))) continue;

            if (entry.SameSlot(current))
            {
                merged.Add(current);
                placed = true;
            }
            else if (current.IsQwarks && entry.Description == current.Description
                                      && string.Equals(entry.Game, current.Game, StringComparison.Ordinal))
            {
                merged.Add(current with { Hash = entry.Hash });
            }
            else
            {
                merged.Add(entry);
            }
        }

        if (!placed) merged.Add(current);
        return merged;
    }

    /// <summary>
    /// The text of a title's patch file: the marker, the engine version RPCS3 insists on, and one
    /// block per executable holding each of its entries under its own description, for every
    /// version of the title. An executable's block is where its first entry is, and its entries
    /// are in the order given. Laid out the way the patches RPCS3 ships are, so it reads like one
    /// of them. The be32 words come first, in qwark's order (the caves, then the hooks or the mod's
    /// words), and the single bytes after them; RPCS3 applies every line when it loads the
    /// executable, before any of it runs.
    /// </summary>
    public static string BuildPatchFile(string titleId, IReadOnlyList<PatchFileEntry> entries)
    {
        var text = new StringBuilder();
        text.Append(MarkerLine).Append('\n');
        text.Append("Version: ").Append(EngineVersion).Append('\n');

        // One key per hash: a hash written twice would be a duplicate key, which RPCS3 rejects.
        var hashes = new List<string>();
        foreach (var entry in entries)
        {
            if (!hashes.Contains(entry.Hash, StringComparer.OrdinalIgnoreCase)) hashes.Add(entry.Hash);
        }

        foreach (var hash in hashes)
        {
            text.Append('\n');
            text.Append(hash).Append(":\n");

            foreach (var entry in entries.Where(e => string.Equals(e.Hash, hash, StringComparison.OrdinalIgnoreCase)))
            {
                AppendEntry(text, titleId, entry);
            }
        }

        return text.ToString();
    }

    private static void AppendEntry(StringBuilder text, string titleId, PatchFileEntry entry)
    {
        text.Append("  ").Append(Quote(entry.Description)).Append(":\n");
        text.Append("    Games:\n");
        text.Append("      ").Append(Quote(entry.Game)).Append(":\n");
        text.Append("        ").Append(titleId).Append(": [ ").Append(AllVersions).Append(" ]\n");
        text.Append("    Author: ").Append(Quote(entry.Author)).Append('\n');
        text.Append("    Notes: ").Append(Quote(entry.Notes)).Append('\n');
        text.Append("    Patch Version: ").Append(PlainOrQuoted(entry.PatchVersion)).Append('\n');
        text.Append("    Patch:\n");

        foreach (var word in entry.Words)
        {
            text.Append("      - [ be32, 0x").Append(word.Address.ToString("x8", CultureInfo.InvariantCulture))
                .Append(", 0x").Append(word.Word.ToString("x8", CultureInfo.InvariantCulture)).Append(" ]\n");
        }

        foreach (var single in entry.Bytes)
        {
            text.Append("      - [ byte, 0x").Append(single.Address.ToString("x8", CultureInfo.InvariantCulture))
                .Append(", 0x").Append(single.Value.ToString("x2", CultureInfo.InvariantCulture)).Append(" ]\n");
        }
    }

    /// <summary>A version as RPCS3's own patches write it, bare, unless it is something YAML would read otherwise.</summary>
    private static string PlainOrQuoted(string text) =>
        text.Length > 0 && text.All(c => char.IsAsciiDigit(c) || c == '.') ? text : Quote(text);

    /// <summary>A double-quoted YAML scalar.</summary>
    private static string Quote(string text)
    {
        var quoted = new StringBuilder(text.Length + 2).Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"': quoted.Append("\\\""); break;
                case '\\': quoted.Append("\\\\"); break;
                default: quoted.Append(char.IsControl(c) ? ' ' : c); break;
            }
        }

        return quoted.Append('"').ToString();
    }

    /// <summary>A title id is a file name and a YAML key here, so it is letters and digits and nothing else.</summary>
    public static bool IsTitleId(string? titleId) =>
        !string.IsNullOrEmpty(titleId) && titleId.Length <= 16 && titleId.All(char.IsAsciiLetterOrDigit);

    // ---------------------------------------------------------------- patch_config.yml

    /// <summary>
    /// patch_config.yml with <c>Enabled: true</c> set for each of <paramref name="keys"/>, at
    /// <c>[hash][description][title][serial][app version]</c>, which is how RPCS3 both writes the file
    /// and looks a patch up in it (patch_engine::load reads the switch for an entry listed with
    /// <c>[ All ]</c> under the key "All"). Every other entry is left as it was, and a missing or
    /// empty file becomes one with only these in it. A file that is not a YAML map is refused, not
    /// replaced: RPCS3 would not read it either, and it is the user's to put right.
    /// </summary>
    public static string EnablePatches(string? existing, IEnumerable<PatchConfigKey> keys, string path)
    {
        var root = LoadConfig(existing, path);

        foreach (var key in keys)
        {
            var versions = Descend(root, path, key.Hash, key.Description, key.Game, key.Serial);
            var switches = DescendOne(versions, path, AllVersions);
            SetScalar(switches, "Enabled", "true");
        }

        return Emit(root);
    }

    /// <summary>
    /// patch_config.yml with each of <paramref name="keys"/> switched off the way RPCS3 itself
    /// writes a switched-off patch. RPCS3's <c>patch_engine::save_config</c> writes an app version
    /// only when its patch is enabled or has configurable values that differ from the defaults,
    /// never <c>Enabled: false</c>, and leaves out every hash, description, title and serial with
    /// nothing under it; a missing switch reads as off. So the <c>Enabled</c> line goes, then the
    /// app version if nothing is left in it, then each level above that is left empty. Everything
    /// else is left as it was, and a key that is not in the file is already off.
    /// </summary>
    public static string DisablePatches(string? existing, IEnumerable<PatchConfigKey> keys, string path)
    {
        var root = LoadConfig(existing, path);

        foreach (var key in keys)
        {
            var trail = new List<(YamlMappingNode Parent, YamlNode Key, YamlMappingNode Node)>();
            YamlMappingNode current = root;
            bool found = true;

            foreach (var step in new[] { key.Hash, key.Description, key.Game, key.Serial, AllVersions })
            {
                if (ChildEntry(current, step) is not { } child || child.Value is not YamlMappingNode map)
                {
                    found = false;
                    break;
                }

                trail.Add((current, child.Key, map));
                current = map;
            }

            if (!found) continue;

            if (ChildEntry(current, "Enabled") is { } enabled) current.Children.Remove(enabled.Key);

            for (int i = trail.Count - 1; i >= 0; i--)
            {
                var (parent, name, node) = trail[i];
                if (node.Children.Count > 0) break;
                parent.Children.Remove(name);
            }
        }

        return Emit(root);
    }

    /// <summary>Whether patch_config.yml switches one of this client's patches on.</summary>
    public static bool IsEnabled(string? configText, PatchConfigKey key, string path) =>
        IsEnabled(LoadConfig(configText, path), key);

    /// <summary>
    /// Which of <paramref name="keys"/> patch_config.yml switches on, read once. Throws
    /// <see cref="Rpcs3PatchException"/> for a file RPCS3 could not read either.
    /// </summary>
    public static IReadOnlyList<PatchConfigKey> EnabledAmong(string? configText, IEnumerable<PatchConfigKey> keys, string path)
    {
        var root = LoadConfig(configText, path);
        return keys.Where(key => IsEnabled(root, key)).ToList();
    }

    private static bool IsEnabled(YamlMappingNode root, PatchConfigKey key)
    {
        YamlNode? node = root;
        foreach (var step in new[] { key.Hash, key.Description, key.Game, key.Serial, AllVersions, "Enabled" })
        {
            node = node is YamlMappingNode map ? Child(map, step) : null;
            if (node is null) return false;
        }

        // yaml-cpp's own reading of a bool, which is what RPCS3 asks for.
        return node is YamlScalarNode { Value: { } value }
               && value.ToLowerInvariant() is "true" or "yes" or "on" or "y";
    }

    private static YamlMappingNode LoadConfig(string? text, string path)
    {
        if (string.IsNullOrWhiteSpace(text)) return new YamlMappingNode();

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            throw new Rpcs3PatchException(
                $"{path} is not valid YAML ({ex.Message}), so RaCMAN will not change it. Correct or remove it first.");
        }

        if (stream.Documents.Count == 0) return new YamlMappingNode();
        if (stream.Documents.Count > 1)
        {
            throw new Rpcs3PatchException($"{path} holds more than one YAML document, so RaCMAN will not change it.");
        }

        return stream.Documents[0].RootNode switch
        {
            YamlMappingNode map => map,
            YamlScalarNode { Value: null or "" or "~" or "null" } => new YamlMappingNode(),
            _ => throw new Rpcs3PatchException($"{path} is not a YAML map, so RaCMAN will not change it."),
        };
    }

    private static YamlMappingNode Descend(YamlMappingNode root, string path, params string[] keys)
    {
        var node = root;
        foreach (var key in keys) node = DescendOne(node, path, key);
        return node;
    }

    /// <summary>The map under <paramref name="key"/>, made when it is missing or empty.</summary>
    private static YamlMappingNode DescendOne(YamlMappingNode parent, string path, string key)
    {
        foreach (var (existingKey, value) in parent.Children)
        {
            if (existingKey is not YamlScalarNode { Value: { } name } || name != key) continue;

            switch (value)
            {
                case YamlMappingNode map:
                    return map;

                case YamlScalarNode { Value: null or "" or "~" or "null" }:
                    var made = new YamlMappingNode();
                    parent.Children[existingKey] = made;
                    return made;

                default:
                    throw new Rpcs3PatchException(
                        $"{path} has something other than a map under \"{key}\", so RaCMAN will not change it.");
            }
        }

        var created = new YamlMappingNode();
        parent.Children.Add(new YamlScalarNode(key), created);
        return created;
    }

    private static void SetScalar(YamlMappingNode map, string key, string value)
    {
        foreach (var existingKey in map.Children.Keys)
        {
            if (existingKey is not YamlScalarNode { Value: { } name } || name != key) continue;

            map.Children[existingKey] = new YamlScalarNode(value);
            return;
        }

        map.Children.Add(new YamlScalarNode(key), new YamlScalarNode(value));
    }

    private static YamlNode? Child(YamlMappingNode map, string key) => ChildEntry(map, key)?.Value;

    /// <summary>The key node and the value under <paramref name="key"/>, so the pair can be removed by the node it was read with.</summary>
    private static KeyValuePair<YamlNode, YamlNode>? ChildEntry(YamlMappingNode map, string key)
    {
        foreach (var pair in map.Children)
        {
            if (pair.Key is YamlScalarNode { Value: { } name } && name == key) return pair;
        }

        return null;
    }

    /// <summary>
    /// Writes a node back out: one implicit document, no end marker, "\n" line ends as RPCS3's
    /// own writer uses, and no line folding, so a long title stays on its own line. Every node keeps
    /// the style it was read with; anchors are dropped, since the model has already resolved every
    /// alias to its node and an anchor written twice would be an error.
    /// </summary>
    private static string Emit(YamlMappingNode root)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        var settings = new EmitterSettings(
            bestIndent: 2, bestWidth: int.MaxValue, isCanonical: false, maxSimpleKeyLength: 1024,
            skipAnchorName: false, indentSequences: false, newLine: "\n", useUtf16SurrogatePairs: false);
        var emitter = new Emitter(writer, settings);

        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart(null, null, isImplicit: true));
        EmitNode(emitter, root);
        emitter.Emit(new DocumentEnd(isImplicit: true));
        emitter.Emit(new StreamEnd());
        return writer.ToString();
    }

    private static void EmitNode(IEmitter emitter, YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                bool implicitTag = scalar.Tag.IsEmpty;
                emitter.Emit(new Scalar(AnchorName.Empty, scalar.Tag, scalar.Value ?? string.Empty, scalar.Style,
                    implicitTag, implicitTag));
                break;

            case YamlSequenceNode sequence:
                emitter.Emit(new SequenceStart(AnchorName.Empty, sequence.Tag, sequence.Tag.IsEmpty, sequence.Style));
                foreach (var child in sequence.Children) EmitNode(emitter, child);
                emitter.Emit(new SequenceEnd());
                break;

            case YamlMappingNode mapping:
                emitter.Emit(new MappingStart(AnchorName.Empty, mapping.Tag, mapping.Tag.IsEmpty, mapping.Style));
                foreach (var (key, value) in mapping.Children)
                {
                    EmitNode(emitter, key);
                    EmitNode(emitter, value);
                }

                emitter.Emit(new MappingEnd());
                break;

            default:
                throw new Rpcs3PatchException($"patch_config.yml holds a YAML node RaCMAN cannot write back ({node.NodeType}).");
        }
    }

    // ---------------------------------------------------------------- the writes

    /// <summary>The savefile helper's install alone: <see cref="PlanQwark"/> with no code switches.</summary>
    public static Rpcs3PatchPlan Plan(Rpcs3Folder folder, string titleId, string game, string hash,
        PatchReply patch, byte qwarkBuild) =>
        PlanQwark(folder, titleId, game, hash, patch, null, qwarkBuild);

    /// <summary>
    /// Works out both files for installing qwark's own patches, reading what is there now: the
    /// savefile helper's entry, the code switches' entry, or both, go in beside every other entry
    /// the file holds, and patch_config.yml switches on every entry of the kinds written, for every
    /// executable of the game, and leaves every mod's switch as it was. Both go in one write, so the
    /// Connection panel's one confirmation covers both.
    /// <para>
    /// Throws <see cref="Rpcs3PatchException"/> with the reason when it cannot go ahead: somebody
    /// else's patch file, a broken one of ours, a patch_config.yml that is not a map, a reply with no
    /// words, or a mod switched on for this executable that writes an address one of them writes.
    /// RPCS3 would apply one over the other, and qwark would find neither whole.
    /// </para>
    /// </summary>
    public static Rpcs3PatchPlan PlanQwark(Rpcs3Folder folder, string titleId, string game, string hash,
        PatchReply? helper, PatchReply? switches, byte qwarkBuild)
    {
        CheckNames(titleId, hash);
        if (helper is null && switches is null) throw new Rpcs3PatchException("There is none of qwark's patches to write.");
        if (helper is { Words.Length: 0 }) throw new Rpcs3PatchException("qwark sent a savefile helper with no words in it.");
        if (switches is { Words.Length: 0 }) throw new Rpcs3PatchException("qwark sent code switches with no words in them.");

        var file = ReadOwnFile(folder, titleId);
        var (configFile, configExists, configText) = ReadConfig(folder);

        string name = string.IsNullOrWhiteSpace(game) ? titleId : game;
        var parts = new List<PatchFileEntry>();
        if (helper is not null)
        {
            parts.Add(new PatchFileEntry(hash, name, helper.Words, helper.Bytes, NotesFor(qwarkBuild, helper.Stamp)));
        }

        if (switches is not null)
        {
            parts.Add(new PatchFileEntry(hash, name, switches.Words, switches.Bytes, NotesForSwitches(qwarkBuild, switches.Stamp),
                SwitchesDescription));
        }

        var mods = EnabledMods(file, hash, titleId, configText, configFile);
        foreach (var part in parts)
        {
            var clashes = ModsOver(PatchRanges.Of(part), mods);
            if (clashes.Count == 0) continue;

            string names = JoinNames(clashes.Select(clash => clash.Name).ToList());
            throw new Rpcs3PatchException(
                $"{SentenceStart(NameOf(part.Description))} cannot be installed: {names} "
                + $"{(clashes.Count == 1 ? "is" : "are")} enabled on the Mods panel and "
                + $"{(clashes.Count == 1 ? "writes" : "write")} to the same addresses (from 0x{clashes[0].Address:x8}), "
                + $"and RPCS3 would apply one over the other. Disable {names} on the Mods panel first.");
        }

        IReadOnlyList<PatchFileEntry> entries = file.Entries;
        foreach (var part in parts) entries = MergeEntries(entries, part);
        string patchText = BuildPatchFile(titleId, entries);

        string newConfig = EnablePatches(configText,
            entries.Where(entry => parts.Any(part => part.Description == entry.Description)).Select(entry => entry.Key(titleId)),
            configFile);

        return new Rpcs3PatchPlan(file.Path, patchText, file.Kind == PatchFileKind.Ours, configFile, newConfig,
            configExists, entries);
    }

    /// <summary>
    /// The mods patch_config.yml switches on for this executable: what RPCS3 puts into the game at
    /// the next boot besides qwark's own patches.
    /// </summary>
    private static IReadOnlyList<PatchFileEntry> EnabledMods(PatchFileState file, string hash, string titleId,
        string? configText, string configFile)
    {
        var mods = file.EntriesFor(hash).Where(entry => entry.ModDir is not null).ToList();
        var on = EnabledAmong(configText, mods.Select(entry => entry.Key(titleId)), configFile);
        return mods.Where(entry => on.Contains(entry.Key(titleId))).ToList();
    }

    /// <summary>
    /// Every one of <paramref name="mods"/> that writes an address in <paramref name="ranges"/>, by
    /// name, with the first address they share. What stands between one of qwark's own patches and
    /// the game, since RPCS3 would apply one over the other.
    /// </summary>
    public static IReadOnlyList<(string Name, uint Address)> ModsOver(IReadOnlyList<PatchRange> ranges, IEnumerable<PatchFileEntry> mods)
    {
        var found = new List<(string, uint)>();
        foreach (var mod in mods)
        {
            if (PatchRanges.FirstShared(ranges, PatchRanges.Of(mod)) is { } address) found.Add((NameOf(mod.Description), address));
        }

        return found;
    }

    /// <summary>A name from <see cref="NameOf"/> at the start of a sentence: "the" is capitalised, qwark never is.</summary>
    private static string SentenceStart(string name) =>
        name.StartsWith("the ", StringComparison.Ordinal) ? "The" + name[3..] : name;

    /// <summary>
    /// Works out both files for writing mods' entries for the running executable and switching them
    /// on: enabling a mod (and the dependencies the user confirmed), or bringing an enabled one's
    /// words up to date. Each entry replaces the one of its mod folder for this executable, and a
    /// mod renamed since leaves its old switch behind switched off; every other entry and every
    /// other switch is kept exactly as it was.
    /// <para>
    /// Refused, with every other mod named, when one of them writes an address that the savefile
    /// helper's or the code switches' entry for this executable, an enabled mod's, or another of
    /// these writes: RPCS3 would apply one over the other when the game boots and leave neither
    /// whole. This is checked here, against the files as they are when the write happens, rather
    /// than against a look taken earlier.
    /// </para>
    /// </summary>
    public static Rpcs3PatchPlan PlanMods(Rpcs3Folder folder, string titleId, string hash, IReadOnlyList<PatchFileEntry> mods)
    {
        CheckNames(titleId, hash);
        if (mods.Count == 0) throw new Rpcs3PatchException("There is no mod to write.");

        foreach (var mod in mods)
        {
            if (mod.ModDir is null) throw new Rpcs3PatchException($"\"{mod.Description}\" is not a mod's description.");
            if (!string.Equals(mod.Hash, hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new Rpcs3PatchException($"{NameOf(mod.Description)} was worked out for {mod.Hash}, not for {hash}.");
            }

            if (mod.Words.Count == 0 && mod.Bytes.Count == 0)
            {
                throw new Rpcs3PatchException($"qwark sent {NameOf(mod.Description)} with no words in it, so there is nothing to patch.");
            }
        }

        var file = ReadOwnFile(folder, titleId);
        var (configFile, configExists, configText) = ReadConfig(folder);

        // What is already going into the game at the next boot: qwark's own patches, the helper
        // and the code switches, whenever they are in the file (they are switched on by their own
        // button, and nothing may sit under them meanwhile), and every mod whose switch is on. An
        // entry these writes replace is not in the way of itself.
        var present = file.EntriesFor(hash).Where(entry => !mods.Any(mod => mod.SameSlot(entry))).ToList();
        var enabled = EnabledAmong(configText, present.Where(entry => !entry.IsQwarks).Select(entry => entry.Key(titleId)), configFile);
        var inTheWay = present.Where(entry => entry.IsQwarks || enabled.Contains(entry.Key(titleId))).ToList();

        CheckOverlaps(mods, inTheWay);

        IReadOnlyList<PatchFileEntry> entries = file.Entries;
        var renamed = new List<PatchConfigKey>();
        foreach (var mod in mods)
        {
            if (file.ModEntryFor(hash, mod.ModDir!) is { } old && old.Description != mod.Description)
            {
                renamed.Add(old.Key(titleId));
            }

            entries = MergeEntries(entries, mod);
        }

        string newConfig = DisablePatches(configText, renamed, configFile);
        newConfig = EnablePatches(newConfig, mods.Select(mod => mod.Key(titleId)), configFile);

        return new Rpcs3PatchPlan(file.Path, BuildPatchFile(titleId, entries), file.Kind == PatchFileKind.Ours,
            configFile, newConfig, configExists, entries);
    }

    /// <summary>
    /// patch_config.yml with the running executable's entries of <paramref name="modDirs"/> switched
    /// off, and the patch file left as it is: the words stay there for the next time the mod is
    /// enabled, and RPCS3 applies nothing that is switched off. Null when there is nothing to switch
    /// off, so nothing is written.
    /// </summary>
    public static Rpcs3PatchPlan? PlanDisable(Rpcs3Folder folder, string titleId, string hash, IReadOnlyList<string> modDirs)
    {
        CheckNames(titleId, hash);

        var file = ReadOwnFile(folder, titleId);
        var keys = modDirs.Select(dir => file.ModEntryFor(hash, dir))
            .Where(entry => entry is not null)
            .Select(entry => entry!.Key(titleId))
            .ToList();

        var (configFile, configExists, configText) = ReadConfig(folder);
        if (keys.Count == 0 || !configExists) return null;

        return new Rpcs3PatchPlan(file.Path, null, file.Kind == PatchFileKind.Ours, configFile,
            DisablePatches(configText, keys, configFile), configExists, file.Entries);
    }

    /// <summary>
    /// Refuses <paramref name="mods"/> when one of them writes an address that one of
    /// <paramref name="present"/> or another of them writes, naming each one in the way.
    /// </summary>
    public static void CheckOverlaps(IReadOnlyList<PatchFileEntry> mods, IReadOnlyList<PatchFileEntry> present)
    {
        for (int i = 0; i < mods.Count; i++)
        {
            var mod = mods[i];
            var clashes = new List<(PatchFileEntry Other, uint Address)>();

            foreach (var other in present.Concat(mods.Take(i)))
            {
                if (PatchRanges.FirstShared(PatchRanges.Of(mod), PatchRanges.Of(other)) is { } address)
                {
                    clashes.Add((other, address));
                }
            }

            if (clashes.Count == 0) continue;

            string names = JoinNames(clashes.Select(clash => NameOf(clash.Other.Description)).Distinct().ToList());

            // qwark's own patches are never switched off from the Mods panel, so a mod under one of
            // them cannot be used at all, whatever else is in its way; a mod under other mods can,
            // once they are switched off.
            var qwarks = clashes.Where(clash => clash.Other.IsQwarks).Select(clash => NameOf(clash.Other.Description)).Distinct().ToList();

            string advice = qwarks.Count == 0 ? $"Disable {names} first."
                : $"It cannot be used together with {JoinNames(qwarks)}."
                  + (clashes.Any(clash => clash.Other.IsSwitches)
                      ? " It patches the same code as a cheat on the Game panel, which works through the switches instead."
                      : string.Empty);

            throw new Rpcs3PatchException(
                $"{NameOf(mod.Description)} cannot be enabled: it writes to the same addresses as {names} "
                + $"(from 0x{clashes[0].Address:x8}), and RPCS3 would apply one over the other. " + advice);
        }
    }

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    private static void CheckNames(string titleId, string hash)
    {
        if (!IsTitleId(titleId)) throw new Rpcs3PatchException($"\"{titleId}\" is not a title id RaCMAN can name a patch file after.");
        if (!IsPpuHash(hash)) throw new Rpcs3PatchException($"\"{hash}\" is not an RPCS3 executable hash.");
    }

    /// <summary>The title's patch file, when it is missing or this client's; somebody else's or a broken one is a refusal.</summary>
    private static PatchFileState ReadOwnFile(Rpcs3Folder folder, string titleId)
    {
        var file = ReadPatchFile(folder.PatchFile(titleId));
        if (file.Kind is PatchFileKind.Foreign or PatchFileKind.Broken) throw new Rpcs3PatchException(file.Problem);
        return file;
    }

    private static (string File, bool Exists, string? Text) ReadConfig(Rpcs3Folder folder)
    {
        string configFile = folder.PatchConfigFile;
        bool configExists = File.Exists(configFile);
        try
        {
            return (configFile, configExists, configExists ? File.ReadAllText(configFile) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new Rpcs3PatchException($"{configFile} could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes a plan: the patch file when the plan changes it, then a copy of patch_config.yml
    /// beside it the first time this session changes that file (<paramref name="backedUp"/>
    /// remembers which), then the new patch_config.yml. Each file is written whole to a temporary
    /// name and moved over the old one, so RPCS3 never reads half of either.
    /// </summary>
    public static void Write(Rpcs3PatchPlan plan, ISet<string> backedUp)
    {
        if (plan.PatchText is { } patchText) WriteWhole(plan.PatchFile, patchText);

        string config = Path.GetFullPath(plan.ConfigFile);
        if (File.Exists(config) && backedUp.Add(config))
        {
            File.Copy(config, config + BackupSuffix, overwrite: true);
        }

        WriteWhole(config, plan.ConfigText);
    }

    private static void WriteWhole(string path, string text)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);

        string temporary = path + ".racman-tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // The old file is still whole; only the half-made one is left to clear away.
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing more to be done about it; the error that got here is the one to report.
            }

            throw;
        }
    }
}

/// <summary>
/// The one way this client writes RPCS3's files. The install of qwark's patches and the Mods panel
/// both read the patch file and patch_config.yml, change their own part and write the whole of each
/// back, so two of them at once would each write the other's change away: every write is worked out
/// and written under one lock, from the files as they are at that moment. It also remembers which
/// patch_config.yml has already been copied aside this session, so the copy is always of what the
/// user had rather than of this client's first write.
/// </summary>
public sealed class Rpcs3PatchWriter
{
    private readonly object _gate = new();
    private readonly HashSet<string> _backedUp = new(StringComparer.Ordinal);

    /// <summary>
    /// Works out a plan and writes it, both under the lock. A null plan is nothing to write.
    /// Whatever the planning throws — a <see cref="Rpcs3PatchException"/> with the reason — comes
    /// out with nothing written.
    /// </summary>
    public Rpcs3PatchPlan? Commit(Func<Rpcs3PatchPlan?> plan)
    {
        lock (_gate)
        {
            var planned = plan();
            if (planned is not null) Rpcs3Patches.Write(planned, _backedUp);
            return planned;
        }
    }
}
