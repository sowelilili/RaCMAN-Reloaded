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

    public int PatchWordCount { get; init; }

    /// <summary>Referenced .bin files, in the order they first appear in patch.txt.</summary>
    public IReadOnlyList<string> BinFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>();

    /// <summary>CRC32 of patch.txt followed by every referenced .bin in patch order.</summary>
    public uint Hash { get; init; }

    public string PatchFile => System.IO.Path.Combine(Directory, "patch.txt");

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

    public ModLibrary(string rootPath)
    {
        RootPath = rootPath;
    }

    /// <summary>The local library root, holding one folder per title id.</summary>
    public string RootPath { get; }

    public string TitleFolder(string titleId) => System.IO.Path.Combine(RootPath, titleId);

    public static string ConsoleFolder(string titleId, string dirName) => $"{ConsoleRoot}/{titleId}/{dirName}";

    /// <summary>Scans mods/&lt;TITLEID&gt;/, skipping folders without a patch.txt and mods marked invisible.</summary>
    public IReadOnlyList<LocalMod> Scan(string titleId)
    {
        var folder = TitleFolder(titleId);
        if (!System.IO.Directory.Exists(folder)) return Array.Empty<LocalMod>();

        var mods = new List<LocalMod>();
        foreach (var dir in System.IO.Directory.EnumerateDirectories(folder))
        {
            var mod = Read(dir);
            if (mod is { Visible: true }) mods.Add(mod);
        }

        return mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Reads one mod folder, or null when it has no patch.txt.</summary>
    public static LocalMod? Read(string folder)
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

        var dirName = new DirectoryInfo(folder).Name;
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
    /// differs, writes qwark.sum and rescans. Returns true when anything was uploaded.
    /// </summary>
    public async Task<bool> EnsureUploadedAsync(
        QwarkClient client,
        string titleId,
        LocalMod mod,
        uint consoleHash,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
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
                mod = rootMod;
            }
            else
            {
                foreach (var dir in System.IO.Directory.EnumerateDirectories(tempRoot))
                {
                    mod = Read(dir);
                    if (mod is null) continue;
                    sourceDir = dir;
                    break;
                }
            }

            if (sourceDir is null || mod is null)
            {
                throw new InvalidDataException("Invalid or corrupt mod: no folder in the ZIP contains a patch.txt.");
            }

            var installed = Read(System.IO.Path.Combine(TitleFolder(titleId), mod.DirName));
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
        var target = System.IO.Path.Combine(TitleFolder(titleId), candidate.Mod.DirName);
        CopyAll(new DirectoryInfo(candidate.SourceDirectory), new DirectoryInfo(target));
        return Read(target) ?? throw new InvalidDataException("The installed mod has no patch.txt.");
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
