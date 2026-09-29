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
/// <see cref="PatchApplied"/> says the log also shows RPCS3 applying this client's patch at that boot.
/// </summary>
public sealed record ExecutableHashLookup(string? Hash, string Problem, bool PatchApplied = false, string? Log = null);

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
/// One executable's entry in RaCMAN's patch file: its hash, the game it is for, the words and the
/// single bytes.
/// </summary>
public sealed record PatchFileEntry(
    string Hash,
    string Game,
    IReadOnlyList<PatchWord> Words,
    IReadOnlyList<PatchByte> Bytes,
    string Notes)
{
    /// <summary>Whether this entry holds exactly the helper <paramref name="patch"/> describes.</summary>
    public bool Holds(SaveFilePatch patch) => patch.SameHelper(Words, Bytes);
}

/// <summary>What is in a title's patch file, as far as this client is concerned.</summary>
public sealed record PatchFileState(string Path, PatchFileKind Kind, IReadOnlyList<PatchFileEntry> Entries, string Problem)
{
    public PatchFileEntry? EntryFor(string hash) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Hash, hash, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Where one of this client's patches is switched on in patch_config.yml.</summary>
public sealed record PatchConfigKey(string Hash, string Game, string Serial);

/// <summary>Everything an install is about to write, worked out before a byte of it is.</summary>
public sealed record Rpcs3PatchPlan(
    string PatchFile,
    string PatchText,
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
/// The savefile helper as an RPCS3 patch: finding RPCS3's folder, reading the running game's
/// executable hash out of its log, writing the per-title patch file and switching the patch on in
/// patch_config.yml. Nothing here draws anything and nothing here knows an address: the words come
/// from qwark (SAVEFILE_PATCH) and are copied into the file as they are.
/// <para>
/// Why a file at all: RPCS3 recompiles the game's code, so qwark cannot write the helper into a
/// running game the way it does on a console. RPCS3 does apply patch files when it loads the
/// executable, before it compiles anything, so the helper goes in as a patch and the game has to be
/// started again to take it.
/// </para>
/// </summary>
public static class Rpcs3Patches
{
    /// <summary>The description every entry this client writes is filed under, in the file and in RPCS3's Patch Manager.</summary>
    public const string Description = "qwark savefile helper";

    public const string Author = "qwark";

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

    public const string MarkerLine = Marker + " and rewrites it whenever qwark's savefile helper changes. Edits made here are lost.";

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
        bool applied = false;

        string? line;
        while ((line = log.ReadLine()) is not null)
        {
            int at = line.IndexOf(SerialMarker, StringComparison.Ordinal);
            if (at >= 0)
            {
                serial = FirstToken(line, at + SerialMarker.Length);
                hash = null;
                applied = false;
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
                    applied = false;
                }

                continue;
            }

            // patch_engine::apply's own line for a patch it applied, which comes after the hash.
            if (hash is not null && line.Contains($"Applied patch (hash='{hash}', description='{Description}'", StringComparison.Ordinal))
            {
                applied = true;
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
            : new ExecutableHashLookup(hash, string.Empty, applied);
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
    /// Reads a patch file back. Only a file whose first line is <see cref="MarkerLine"/> is this
    /// client's; anything else is somebody's own patches for the title, and is reported rather than
    /// read. Of this client's file only the entries under <see cref="Description"/> are taken.
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
                if (Child(descriptions, Description) is not YamlMappingNode entry) continue;

                entries.Add(ReadEntry(hash, entry));
            }

            return new PatchFileState(path, PatchFileKind.Ours, entries, string.Empty);
        }
        catch (Exception ex) when (ex is YamlException or FormatException or OverflowException)
        {
            return new PatchFileState(path, PatchFileKind.Broken, Array.Empty<PatchFileEntry>(),
                $"{path} is RaCMAN's but could not be read back ({ex.Message}). Delete it, then install again.");
        }
    }

    private static PatchFileEntry ReadEntry(string hash, YamlMappingNode entry)
    {
        string game = Child(entry, "Games") is YamlMappingNode games
                      && games.Children.Keys.FirstOrDefault() is YamlScalarNode { Value: { } title }
            ? title
            : string.Empty;

        string notes = Child(entry, "Notes") is YamlScalarNode { Value: { } text } ? text : string.Empty;

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
                    throw new FormatException($"the entry for {hash} holds a patch line that is not [ type, address, value ]");
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
                        throw new FormatException($"the entry for {hash} holds a {type} line, which RaCMAN never writes");
                }
            }
        }

        return new PatchFileEntry(hash, game, words, bytes, notes);
    }

    private static uint ParseHex(string text)
    {
        string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return uint.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    /// <summary>What the Notes line of an entry says: which qwark the words came from, and their stamp.</summary>
    public static string NotesFor(byte qwarkBuild, uint stamp) =>
        $"qwark build {qwarkBuild}, helper stamp 0x{stamp:x8}";

    /// <summary>
    /// The entries a rewrite keeps. The running executable's entry is <paramref name="current"/>,
    /// and every other executable of the same game gets the same words, because the helper is the
    /// game's: somebody who runs two EBOOTs of one game gets both brought up to date. An entry for
    /// another game is kept exactly as it was. That only happens on a title that hosts more than
    /// one game (BCES01503 hosts RaC1 to RaC3), where each game's executable has its own helper and
    /// one game's words written into another's would break it.
    /// </summary>
    public static IReadOnlyList<PatchFileEntry> MergeEntries(IReadOnlyList<PatchFileEntry> existing, PatchFileEntry current)
    {
        var merged = new List<PatchFileEntry>();
        bool placed = false;

        foreach (var entry in existing)
        {
            if (merged.Any(kept => string.Equals(kept.Hash, entry.Hash, StringComparison.OrdinalIgnoreCase))) continue;

            if (string.Equals(entry.Hash, current.Hash, StringComparison.OrdinalIgnoreCase))
            {
                merged.Add(current);
                placed = true;
            }
            else if (string.Equals(entry.Game, current.Game, StringComparison.Ordinal))
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
    /// entry per executable, each filed under <see cref="Description"/> for every version of the
    /// title. Laid out the way the patches RPCS3 ships are, so it reads like one of them. The be32
    /// words come first, in qwark's order (the caves, then the hook), and the single bytes after
    /// them; RPCS3 applies every line when it loads the executable, before any of it runs.
    /// </summary>
    public static string BuildPatchFile(string titleId, IReadOnlyList<PatchFileEntry> entries)
    {
        var text = new StringBuilder();
        text.Append(MarkerLine).Append('\n');
        text.Append("Version: ").Append(EngineVersion).Append('\n');

        foreach (var entry in entries)
        {
            text.Append('\n');
            text.Append(entry.Hash).Append(":\n");
            text.Append("  ").Append(Quote(Description)).Append(":\n");
            text.Append("    Games:\n");
            text.Append("      ").Append(Quote(entry.Game)).Append(":\n");
            text.Append("        ").Append(titleId).Append(": [ ").Append(AllVersions).Append(" ]\n");
            text.Append("    Author: ").Append(Quote(Author)).Append('\n');
            text.Append("    Notes: ").Append(Quote(entry.Notes)).Append('\n');
            text.Append("    Patch Version: ").Append(PatchVersion).Append('\n');
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

        return text.ToString();
    }

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
            var versions = Descend(root, path, key.Hash, Description, key.Game, key.Serial);
            var switches = DescendOne(versions, path, AllVersions);
            SetScalar(switches, "Enabled", "true");
        }

        return Emit(root);
    }

    /// <summary>Whether patch_config.yml switches one of this client's patches on.</summary>
    public static bool IsEnabled(string? configText, PatchConfigKey key, string path)
    {
        var root = LoadConfig(configText, path);

        YamlNode? node = root;
        foreach (var step in new[] { key.Hash, Description, key.Game, key.Serial, AllVersions, "Enabled" })
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

    private static YamlNode? Child(YamlMappingNode map, string key)
    {
        foreach (var (existingKey, value) in map.Children)
        {
            if (existingKey is YamlScalarNode { Value: { } name } && name == key) return value;
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

    // ---------------------------------------------------------------- the install

    /// <summary>
    /// Works out both files for an install, reading what is there now. Throws
    /// <see cref="Rpcs3PatchException"/> with the reason when it cannot go ahead: somebody else's
    /// patch file, a broken one of ours, a patch_config.yml that is not a map, a reply with no words.
    /// </summary>
    public static Rpcs3PatchPlan Plan(Rpcs3Folder folder, string titleId, string game, string hash,
        SaveFilePatch patch, byte qwarkBuild)
    {
        if (!IsTitleId(titleId)) throw new Rpcs3PatchException($"\"{titleId}\" is not a title id RaCMAN can name a patch file after.");
        if (!IsPpuHash(hash)) throw new Rpcs3PatchException($"\"{hash}\" is not an RPCS3 executable hash.");
        if (patch.Words.Length == 0) throw new Rpcs3PatchException("qwark sent a savefile helper with no words in it.");

        var file = ReadPatchFile(folder.PatchFile(titleId));
        if (file.Kind is PatchFileKind.Foreign or PatchFileKind.Broken) throw new Rpcs3PatchException(file.Problem);

        string name = string.IsNullOrWhiteSpace(game) ? titleId : game;
        var current = new PatchFileEntry(hash, name, patch.Words, patch.Bytes, NotesFor(qwarkBuild, patch.Stamp));
        var entries = MergeEntries(file.Entries, current);
        string patchText = BuildPatchFile(titleId, entries);

        string configFile = folder.PatchConfigFile;
        bool configExists = File.Exists(configFile);
        string? configText;
        try
        {
            configText = configExists ? File.ReadAllText(configFile) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new Rpcs3PatchException($"{configFile} could not be read: {ex.Message}");
        }

        string newConfig = EnablePatches(configText,
            entries.Select(entry => new PatchConfigKey(entry.Hash, entry.Game, titleId)), configFile);

        return new Rpcs3PatchPlan(file.Path, patchText, file.Kind == PatchFileKind.Ours, configFile, newConfig,
            configExists, entries);
    }

    /// <summary>
    /// Writes a plan: the patch file, then a copy of patch_config.yml beside it the first time this
    /// session changes that file (<paramref name="backedUp"/> remembers which), then the new
    /// patch_config.yml. Each file is written whole to a temporary name and moved over the old one,
    /// so RPCS3 never reads half of either.
    /// </summary>
    public static void Write(Rpcs3PatchPlan plan, ISet<string> backedUp)
    {
        WriteWhole(plan.PatchFile, plan.PatchText);

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
