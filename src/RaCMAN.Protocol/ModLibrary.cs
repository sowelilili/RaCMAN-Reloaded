using System.IO.Compression;
using System.Text;

namespace RaCMAN.Protocol;

/// <summary>
/// One mod folder in the PC-side library: <c>mods/&lt;TITLEID&gt;/&lt;dir&gt;/patch.txt</c> plus the
/// .bin code caves it references. Same format as racman-official, metadata from the "#-" lines.
/// </summary>
public sealed class LocalMod
{
    public required string Directory { get; init; }

    public required string DirName { get; init; }

    public required string Name { get; init; }

    public string Version { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Link { get; init; } = string.Empty;

    public bool Visible { get; init; } = true;

    /// <summary>True when the mod carries a Lua "automation:" line, which qwark cannot run yet.</summary>
    public bool NeedsLua { get; init; }

    /// <summary>
    /// True when this mod came out of the library the release ships rather than out of the user's
    /// own folder. A shipped mod is replaced whole when the client updates itself, so it is not
    /// somewhere to keep an edit; a user mod of the same folder name is used instead of it.
    /// </summary>
    public bool Shipped { get; init; }

    public int PatchWordCount { get; init; }

    /// <summary>Referenced .bin files, in the order they first appear in patch.txt.</summary>
    public IReadOnlyList<string> BinFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>();

    /// <summary>CRC32 of patch.txt followed by every referenced .bin in patch order.</summary>
    public uint Hash { get; init; }

    public string PatchFile => System.IO.Path.Combine(Directory, "patch.txt");

    /// <summary>
    /// True when the folder name is longer than qwark can use (<see cref="ModFolderName.MaxBytes"/>):
    /// the mod is listed but never uploaded or loaded, and the Mods panel offers a rename.
    /// </summary>
    public bool FolderNameTooLong => !ModFolderName.Fits(DirName);

    public override string ToString() => $"{Name} {Version}".TrimEnd();
}

public enum ZipInstallKind
{
    /// <summary>Nothing installed under that folder name yet.</summary>
    New,

    /// <summary>The ZIP is newer; install without asking.</summary>
    Upgrade,

    /// <summary>Same version; the UI must confirm the replace.</summary>
    Replace,

    /// <summary>The ZIP is older; the UI must confirm the downgrade.</summary>
    Downgrade,
}

/// <summary>An extracted ZIP waiting for the UI to confirm before it lands in the library.</summary>
public sealed class ZipCandidate : IDisposable
{
    internal ZipCandidate(string tempRoot, string sourceDirectory, LocalMod mod, LocalMod? installed, ZipInstallKind kind)
    {
        TempRoot = tempRoot;
        SourceDirectory = sourceDirectory;
        Mod = mod;
        Installed = installed;
        Kind = kind;
    }

    public string TempRoot { get; }

    public string SourceDirectory { get; }

    public LocalMod Mod { get; }

    public LocalMod? Installed { get; }

    public ZipInstallKind Kind { get; }

    public bool NeedsConfirmation => Kind is ZipInstallKind.Replace or ZipInstallKind.Downgrade;

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(TempRoot)) System.IO.Directory.Delete(TempRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing the install over.
        }
    }
}

/// <summary>
/// The PC-side mod library: scanning, CRC32, ZIP install, and uploading a mod to the console
/// with the file opcodes when its content differs from the hash MOD_LIST reports.
/// </summary>
public sealed class ModLibrary
{
    public const string ConsoleRoot = "/dev_hdd0/qwark/mods";

    /// <summary>
    /// The shipped library's list of what the release put in it, one <c>TITLEID</c> or
    /// <c>TITLEID/modfolder</c> per line, written by publish.ps1. See <see cref="ReadShippedManifest"/>.
    /// </summary>
    public const string ShippedManifestName = "shipped.txt";

    private readonly Lazy<HashSet<string>?> _shippedManifest;

    /// <summary>Whether a <c>TITLEID</c>, <c>modfolder</c> pair is a mod no release ships any more; see <see cref="IsShippedFolder"/>.</summary>
    private readonly Func<string, string, bool>? _retired;

    /// <summary>
    /// <paramref name="retired"/> names the mods a release has dropped for good, the client's
    /// legacy-mod-exclusions.txt: a folder of one of those left in the shipped library by an older
    /// release is not offered (<see cref="IsShippedFolder"/>).
    /// </summary>
    public ModLibrary(string rootPath, string? shippedRootPath = null, Func<string, string, bool>? retired = null)
    {
        RootPath = rootPath;
        ShippedRootPath = shippedRootPath;
        _retired = retired;

        // Read once: the application folder is the release's and does not change under a running
        // client. A manifest that cannot be read counts as none, which is how the library behaved
        // before there was one.
        _shippedManifest = new Lazy<HashSet<string>?>(() =>
        {
            if (shippedRootPath is null) return null;

            try
            {
                return ReadShippedManifest(shippedRootPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });
    }

    /// <summary>
    /// The folders under a shipped <c>mods/</c> its release staged, as <c>TITLEID</c> and
    /// <c>TITLEID/modfolder</c> entries, compared without regard to case. Null when there is no
    /// manifest at all: a development tree, where everything in the folder is the repo's.
    /// </summary>
    public static HashSet<string>? ReadShippedManifest(string modsFolder)
    {
        var path = System.IO.Path.Combine(modsFolder, ShippedManifestName);
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

    /// <summary>
    /// Whether a mod folder in the shipped library is offered. A folder the manifest lists is the
    /// release's, and so is every folder when there is no manifest. A folder it does not list is
    /// one of two things. A mod the release stopped shipping, left behind when a new release is
    /// unzipped over an old one: the Deadlocked crash-patch mod, whose words qwark's Crash patches
    /// toggle now writes, is why this exists, and offering it would load the same patch twice. Or a
    /// mod the user dropped in there themselves, as 1.2.2 and older let them, which they expect to
    /// see. The retired ones are named on the client's exclusion list, so only a folder that list
    /// names is left out, and the rest are offered as before. Nothing is ever deleted: nothing
    /// writes the application folder.
    /// </summary>
    public bool IsShippedFolder(string titleId, string dirName) =>
        _shippedManifest.Value is not { } listed
        || listed.Contains($"{titleId}/{dirName}")
        || _retired?.Invoke(titleId, dirName) != true;

    /// <summary>
    /// The shipped mod of a folder name, or null when the release ships none by that name: no
    /// shipped library, a retired folder its manifest does not list, or no patch.txt in it.
    /// </summary>
    public LocalMod? ReadShipped(string titleId, string dirName) =>
        ShippedTitleFolder(titleId) is { } folder && IsShippedFolder(titleId, dirName)
            ? Read(System.IO.Path.Combine(folder, dirName), shipped: true)
            : null;

    /// <summary>
    /// The user's library root, holding one folder per title id. Everything written by this class
    /// lands here: a ZIP install, an edit, a mod pulled off a forum.
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// The library the release ships, read but never written, or null when there is not one. It is
    /// replaced wholesale when the client updates itself, which is why it is separate.
    /// </summary>
    public string? ShippedRootPath { get; }

    public string TitleFolder(string titleId) => System.IO.Path.Combine(RootPath, titleId);

    /// <summary>The shipped library's folder for a title, or null when there is no shipped library.</summary>
    public string? ShippedTitleFolder(string titleId) =>
        ShippedRootPath is null ? null : System.IO.Path.Combine(ShippedRootPath, titleId);

    public static string ConsoleFolder(string titleId, string dirName) => $"{ConsoleTitleFolder(titleId)}/{dirName}";

    /// <summary>The console's folder holding every mod of a title, which DIR_LIST reads for the folders' whole names.</summary>
    public static string ConsoleTitleFolder(string titleId) => $"{ConsoleRoot}/{titleId}";

    /// <summary>
    /// Every mod for a title, the user's and the release's together, sorted by name: folders without
    /// a patch.txt and mods marked invisible are skipped, and a user mod is used instead of a
    /// shipped one with the same folder name, which is how a shipped mod is edited without the next
    /// update overwriting the edit. A retired mod's folder an older release left in the shipped
    /// library is skipped too (<see cref="IsShippedFolder"/>).
    /// </summary>
    public IReadOnlyList<LocalMod> Scan(string titleId)
    {
        var mods = new List<LocalMod>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The user's folder first, so its DirName claims the name before the shipped one is read.
        ScanInto(TitleFolder(titleId), shipped: false, mods, seen, keep: null);
        if (ShippedTitleFolder(titleId) is { } shippedFolder)
        {
            ScanInto(shippedFolder, shipped: true, mods, seen, keep: dirName => IsShippedFolder(titleId, dirName));
        }

        return mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ScanInto(string folder, bool shipped, List<LocalMod> mods, HashSet<string> seen, Func<string, bool>? keep)
    {
        if (!System.IO.Directory.Exists(folder)) return;

        foreach (var dir in System.IO.Directory.EnumerateDirectories(folder))
        {
            if (keep is not null && !keep(System.IO.Path.GetFileName(dir))) continue;

            var mod = Read(dir, shipped);
            if (mod is not { Visible: true }) continue;
            if (!seen.Add(mod.DirName)) continue;

            mods.Add(mod);
        }
    }

    /// <summary>
    /// Reads one mod folder, or null when it has no patch.txt. <paramref name="dirName"/> is the
    /// folder name the mod is to have, where that is not the name of the folder it is read from:
    /// a ZIP extracted to a temporary folder is read under the name it will be installed as.
    /// </summary>
    public static LocalMod? Read(string folder, bool shipped = false, string? dirName = null)
    {
        var patchFile = System.IO.Path.Combine(folder, "patch.txt");
        if (!File.Exists(patchFile)) return null;

        var patchBytes = File.ReadAllBytes(patchFile);
        var text = Encoding.UTF8.GetString(patchBytes);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bins = new List<string>();
        bool needsLua = false;
        int words = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length > 2 && line.StartsWith("#-", StringComparison.Ordinal))
            {
                var parts = line.Split(':', 2);
                if (parts.Length < 2) continue;
                variables[parts[0][2..].Trim()] = parts[1].Trim();
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length < 2 || trimmed[0] == '#') continue;

            var fields = trimmed.Split(':', 2);
            if (fields.Length < 2) continue;

            var key = fields[0].Trim();
            var value = fields[1].Trim();

            if (key.Equals("automation", StringComparison.OrdinalIgnoreCase))
            {
                needsLua = true;
                continue;
            }

            if (!key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;

            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                words++;
            }
            else if (value.Length > 0 && !bins.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                bins.Add(value);
            }
        }

        dirName ??= new DirectoryInfo(folder).Name;
        var mod = new LocalMod
        {
            Directory = folder,
            DirName = dirName,
            Name = variables.TryGetValue("name", out var name) ? name : dirName,
            Version = variables.TryGetValue("version", out var version) ? version : string.Empty,
            Author = variables.TryGetValue("author", out var author) ? author : string.Empty,
            Description = variables.TryGetValue("description", out var description) ? description : string.Empty,
            Link = variables.TryGetValue("href", out var href) ? href : string.Empty,
            Visible = !variables.TryGetValue("visible", out var visible) || !visible.Equals("false", StringComparison.OrdinalIgnoreCase),
            NeedsLua = needsLua,
            Shipped = shipped,
            PatchWordCount = words,
            BinFiles = bins,
            Variables = variables,
            Hash = ComputeHash(folder, patchBytes, bins),
        };

        return mod;
    }

    /// <summary>CRC32 over patch.txt followed by every referenced .bin in patch order.</summary>
    public static uint ComputeHash(LocalMod mod) =>
        ComputeHash(mod.Directory, File.ReadAllBytes(mod.PatchFile), mod.BinFiles);

    private static uint ComputeHash(string folder, byte[] patchBytes, IReadOnlyList<string> bins)
    {
        uint state = Crc32.Update(Crc32.Start(), patchBytes);
        foreach (var bin in bins)
        {
            var path = System.IO.Path.Combine(folder, bin);
            if (!File.Exists(path)) continue;
            state = Crc32.Update(state, File.ReadAllBytes(path));
        }

        return Crc32.Finish(state);
    }

    /// <summary>
    /// Uploads the mod to /dev_hdd0/qwark/mods/&lt;TITLEID&gt;/&lt;dir&gt;/ when the console's hash
    /// differs, writes qwark.sum and rescans. Returns true when anything was uploaded. A folder name
    /// qwark cannot use is refused with <see cref="ModFolderNameException"/> before anything is
    /// written: the copy would be listed under a shorter name and never load.
    /// </summary>
    public async Task<bool> EnsureUploadedAsync(
        QwarkClient client,
        string titleId,
        LocalMod mod,
        uint consoleHash,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (mod.FolderNameTooLong)
        {
            throw new ModFolderNameException($"{mod.Name} was not uploaded: {ModFolderName.TooLongReason(mod.DirName)}");
        }

        uint localHash = ComputeHash(mod);
        if (consoleHash == localHash && consoleHash != 0) return false;

        string remoteFolder = ConsoleFolder(titleId, mod.DirName);
        progress?.Report($"Uploading {mod.DirName} to {remoteFolder}");
        await client.DirCreateAsync(remoteFolder, cancellationToken).ConfigureAwait(false);

        await UploadFileAsync(client, $"{remoteFolder}/patch.txt", File.ReadAllBytes(mod.PatchFile), cancellationToken).ConfigureAwait(false);

        foreach (var bin in mod.BinFiles)
        {
            var local = System.IO.Path.Combine(mod.Directory, bin);
            if (!File.Exists(local)) continue;

            var remote = $"{remoteFolder}/{bin.Replace('\\', '/')}";
            int slash = remote.LastIndexOf('/');
            if (slash > remoteFolder.Length)
            {
                await client.DirCreateAsync(remote[..slash], cancellationToken).ConfigureAwait(false);
            }

            progress?.Report($"Uploading {bin}");
            await UploadFileAsync(client, remote, File.ReadAllBytes(local), cancellationToken).ConfigureAwait(false);
        }

        await UploadFileAsync(client, $"{remoteFolder}/qwark.sum",
            Encoding.ASCII.GetBytes(Crc32.ToSumText(localHash)), cancellationToken).ConfigureAwait(false);

        await client.ModRescanAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report($"{mod.Name} uploaded ({Crc32.ToSumText(localHash)})");
        return true;
    }

    private static Task UploadFileAsync(QwarkClient client, string path, byte[] data, CancellationToken cancellationToken) =>
        client.WriteFileAsync(path, data, progress: null, cancellationToken);

    // ---------------------------------------------------------------- ZIP install

    /// <summary>
    /// Extracts a ZIP to a temp folder and finds the first directory holding a patch.txt, the same
    /// rule the old ModLoaderForm used. The caller confirms and then calls <see cref="CommitZip"/>.
    /// <para>
    /// The mod's folder name is the name of the folder it came in. A ZIP with patch.txt at its root
    /// has no such folder, so its name comes from the ZIP file's name, or the mod's <c>#- name</c>
    /// when that leaves nothing (<see cref="NewFolderName"/>); the temporary folder it was extracted
    /// to never names anything. A folder whose name qwark could not use is renamed the same way.
    /// </para>
    /// </summary>
    public ZipCandidate OpenZip(string zipPath, string titleId)
    {
        string tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "racman-reloaded", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempRoot);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, tempRoot);

            string? sourceDir = null;
            LocalMod? mod = null;

            if (Read(tempRoot) is { } rootMod)
            {
                sourceDir = tempRoot;
                string dirName = NewFolderName(titleId, System.IO.Path.GetFileNameWithoutExtension(zipPath), rootMod.Name);
                mod = Read(tempRoot, dirName: dirName);
            }
            else
            {
                foreach (var dir in System.IO.Directory.EnumerateDirectories(tempRoot))
                {
                    mod = Read(dir);
                    if (mod is null) continue;
                    sourceDir = dir;

                    if (mod.FolderNameTooLong)
                    {
                        mod = Read(dir, dirName: NewFolderName(titleId, mod.DirName, mod.Name));
                    }

                    break;
                }
            }

            if (sourceDir is null || mod is null)
            {
                throw new InvalidDataException("Invalid or corrupt mod: no folder in the ZIP contains a patch.txt.");
            }

            // What this ZIP would replace: the user's copy if there is one, else the shipped mod of
            // that folder name, since the install lands in front of it either way.
            var installed = Read(System.IO.Path.Combine(TitleFolder(titleId), mod.DirName))
                            ?? ReadShipped(titleId, mod.DirName);
            var kind = ZipInstallKind.New;
            if (installed is not null)
            {
                kind = CompareVersions(installed.Version, mod.Version) switch
                {
                    < 0 => ZipInstallKind.Upgrade,
                    > 0 => ZipInstallKind.Downgrade,
                    _ => ZipInstallKind.Replace,
                };
            }

            return new ZipCandidate(tempRoot, sourceDir, mod, installed, kind);
        }
        catch
        {
            try { System.IO.Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Copies the extracted mod into the library, merging over anything already there.</summary>
    public LocalMod CommitZip(ZipCandidate candidate, string titleId)
    {
        if (candidate.Mod.FolderNameTooLong)
        {
            throw new InvalidDataException($"The mod's folder name {candidate.Mod.DirName} is longer than qwark can use.");
        }

        var target = System.IO.Path.Combine(TitleFolder(titleId), candidate.Mod.DirName);
        CopyAll(new DirectoryInfo(candidate.SourceDirectory), new DirectoryInfo(target));
        return Read(target) ?? throw new InvalidDataException("The installed mod has no patch.txt.");
    }

    // ---------------------------------------------------------------- folder names

    /// <summary>
    /// A folder name for a mod called <paramref name="modName"/> that has none of its own yet: made
    /// out of <paramref name="preferred"/> (a ZIP file's name, a folder name too long to keep), or
    /// out of the mod's name when that leaves nothing, sanitised and at most
    /// <see cref="ModFolderName.MaxBytes"/> long. Where a folder of that name is already in the
    /// user's library or the shipped one, <c>-2</c>, <c>-3</c> and so on are added, unless that
    /// folder holds a mod of the same name: installing the same mod again is a replace or an
    /// upgrade, which the install asks about as it does for any other ZIP.
    /// </summary>
    public string NewFolderName(string titleId, string? preferred, string? modName)
    {
        string wanted = ModFolderName.Sanitise(preferred);
        if (wanted.Length == 0) wanted = ModFolderName.Sanitise(modName);
        if (wanted.Length == 0) wanted = "mod";

        return ModFolderName.Unique(wanted, name => FolderTaken(titleId, name, modName));
    }

    /// <summary>
    /// Whether a folder of this name is in either library, holding anything but a mod called
    /// <paramref name="sameModName"/> (null: holding anything at all).
    /// </summary>
    private bool FolderTaken(string titleId, string name, string? sameModName)
    {
        foreach (var (folder, shipped) in TitleFolders(titleId))
        {
            string path = System.IO.Path.Combine(folder, name);
            if (!System.IO.Directory.Exists(path) && !File.Exists(path)) continue;
            if (sameModName is null) return true;

            var mod = Read(path, shipped);
            if (mod is null || !string.Equals(mod.Name, sameModName, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private IEnumerable<(string Folder, bool Shipped)> TitleFolders(string titleId)
    {
        yield return (TitleFolder(titleId), false);
        if (ShippedTitleFolder(titleId) is { } shipped) yield return (shipped, true);
    }

    /// <summary>
    /// The name <see cref="RenameFolder"/> gives a mod whose folder name qwark cannot use: the
    /// mod's own name, sanitised, or what fits of the folder name when that leaves nothing, made
    /// unique against both libraries.
    /// </summary>
    public string SuggestFolderName(string titleId, LocalMod mod)
    {
        string wanted = ModFolderName.Sanitise(mod.Name);
        if (wanted.Length == 0) wanted = ModFolderName.Sanitise(mod.DirName);
        if (wanted.Length == 0) wanted = "mod";

        return ModFolderName.Unique(wanted, name => FolderTaken(titleId, name, sameModName: null));
    }

    /// <summary>
    /// Renames a mod folder of the user's library to <paramref name="newName"/> and reads it back.
    /// Refused for a shipped mod, which lives in the application folder and is never written, for
    /// a name qwark cannot use, and for a name either library already has.
    /// </summary>
    public LocalMod RenameFolder(string titleId, LocalMod mod, string newName)
    {
        if (mod.Shipped) throw new IOException($"{mod.Name} ships with RaCMAN Reloaded, so its folder cannot be renamed.");
        if (newName.Length == 0 || !ModFolderName.Fits(newName) || newName != ModFolderName.Sanitise(newName))
        {
            throw new IOException($"\"{newName}\" is not a folder name qwark can use.");
        }

        string source = UserFolderOf(titleId, mod);
        if (FolderTaken(titleId, newName, sameModName: null)) throw new IOException($"A mod folder called {newName} already exists.");

        string target = System.IO.Path.Combine(TitleFolder(titleId), newName);
        System.IO.Directory.Move(source, target);
        return Read(target) ?? throw new IOException($"{target} has no patch.txt after the rename.");
    }

    /// <summary>
    /// Deletes a mod folder of the user's library, and nothing else: the folder must be the mod's
    /// own, directly under the title's folder in the user's library. A shipped mod is refused.
    /// </summary>
    public void DeleteUserFolder(string titleId, LocalMod mod)
    {
        if (mod.Shipped) throw new IOException($"{mod.Name} ships with RaCMAN Reloaded, so it cannot be deleted from the application folder.");
        System.IO.Directory.Delete(UserFolderOf(titleId, mod), recursive: true);
    }

    /// <summary>The mod's folder, checked to be a folder directly under the title's folder in the user's library.</summary>
    private string UserFolderOf(string titleId, LocalMod mod)
    {
        string titleFolder = System.IO.Path.GetFullPath(TitleFolder(titleId));
        string folder = System.IO.Path.GetFullPath(mod.Directory);
        string? parent = System.IO.Path.GetDirectoryName(folder.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

        if (parent is null || !string.Equals(parent.TrimEnd(System.IO.Path.DirectorySeparatorChar), titleFolder.TrimEnd(System.IO.Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"{folder} is not a mod folder in your library for {titleId}.");
        }

        if (!System.IO.Directory.Exists(folder)) throw new DirectoryNotFoundException($"{folder} is not there any more.");
        return folder;
    }

    private static int CompareVersions(string? left, string? right)
    {
        if (Version.TryParse(left, out var a) && Version.TryParse(right, out var b)) return a.CompareTo(b);
        return string.Compare(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyAll(DirectoryInfo source, DirectoryInfo target)
    {
        if (string.Equals(source.FullName, target.FullName, StringComparison.OrdinalIgnoreCase)) return;
        System.IO.Directory.CreateDirectory(target.FullName);

        foreach (var file in source.GetFiles())
        {
            file.CopyTo(System.IO.Path.Combine(target.FullName, file.Name), overwrite: true);
        }

        foreach (var dir in source.GetDirectories())
        {
            CopyAll(dir, target.CreateSubdirectory(dir.Name));
        }
    }
}
