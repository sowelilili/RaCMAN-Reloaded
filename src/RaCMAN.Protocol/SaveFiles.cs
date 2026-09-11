namespace RaCMAN.Protocol;

/// <summary>
/// The PC-side savefile library: <c>savefiles/&lt;TITLEID&gt;/&lt;category&gt;/&lt;name&gt;.sav</c> in the
/// data folder, the same layout the console keeps. Nothing here knows anything about the game: a
/// saved file is whatever bytes the console's aside buffer held.
/// <para>
/// Since qwark build 12 this is a <em>mirror</em>. The library of record is the console's, because
/// a save is up to 2 MB and it used to be streamed from here on every single load; what this keeps
/// is a second copy, so nothing is lost when a console is wiped, and a file that only exists here
/// is uploaded once and then lives over there.
/// </para>
/// </summary>
public sealed class SaveFileLibrary
{
    /// <summary>The folder offered when a title has no categories yet.</summary>
    public const string DefaultCategory = "misc";

    /// <summary>
    /// What a saved file is called on disk. The library itself takes whole names, so that rename
    /// and delete work on exactly what the listing shows; <see cref="EnsureExtension"/> is what
    /// puts it on a name the user typed.
    /// </summary>
    public const string Extension = ".sav";

    /// <summary>Adds <see cref="Extension"/> unless the name already ends in it.</summary>
    public static string EnsureExtension(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return trimmed;
        return trimmed.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + Extension;
    }

    /// <summary>
    /// The other direction: what a saved file is called on screen. Every file in a category is a
    /// savefile and so every one of them ends in <see cref="Extension"/>, which makes the suffix
    /// four characters of noise on every row. Only the suffix goes: a name with dots of its own
    /// keeps them, and a file that somehow has no suffix is shown as it is.
    /// </summary>
    public static string DisplayName(string? fileName)
    {
        var trimmed = (fileName ?? string.Empty).Trim();
        return trimmed.Length > Extension.Length
               && trimmed.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^Extension.Length]
            : trimmed;
    }

    public SaveFileLibrary(string rootPath)
    {
        RootPath = rootPath;
    }

    public string RootPath { get; }

    public string TitleFolder(string titleId) => Path.Combine(RootPath, Sanitise(titleId, "unknown"));

    public string CategoryFolder(string titleId, string category) =>
        Path.Combine(TitleFolder(titleId), Sanitise(category, DefaultCategory));

    public string FilePath(string titleId, string category, string name) =>
        Path.Combine(CategoryFolder(titleId, category), Sanitise(name, "savefile"));

    /// <summary>Every folder under the title, sorted. Never empty: the default is offered when none exist.</summary>
    public string[] Categories(string titleId)
    {
        var folder = TitleFolder(titleId);
        if (!Directory.Exists(folder)) return new[] { DefaultCategory };

        var names = Directory.EnumerateDirectories(folder)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return names.Length == 0 ? new[] { DefaultCategory } : names;
    }

    /// <summary>Creates the category folder if it is missing and returns its path.</summary>
    public string EnsureCategory(string titleId, string category)
    {
        var folder = CategoryFolder(titleId, category);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public string[] Files(string titleId, string category)
    {
        var folder = CategoryFolder(titleId, category);
        if (!Directory.Exists(folder)) return Array.Empty<string>();

        return Directory.EnumerateFiles(folder)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public byte[] Read(string titleId, string category, string name) =>
        File.ReadAllBytes(FilePath(titleId, category, name));

    /// <summary>
    /// Every file in a category with its size and its CRC32, which is what the merge view compares
    /// against the console's listing.
    /// <para>
    /// The CRC is cached per path against the file's length and last-write time, so a rescan of a
    /// category holding twenty 2 MB saves reads them once rather than on every keystroke that
    /// happens to redraw the panel.
    /// </para>
    /// </summary>
    public LocalSaveFile[] FilesWithCrc(string titleId, string category)
    {
        var folder = CategoryFolder(titleId, category);
        if (!Directory.Exists(folder)) return Array.Empty<LocalSaveFile>();

        var files = new List<LocalSaveFile>();
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) continue;

            var info = new FileInfo(path);
            files.Add(new LocalSaveFile(name, info.Length, CachedCrc(path, info)));
        }

        return files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>The CRC32 of one file in the library, cached the same way.</summary>
    public uint Crc(string titleId, string category, string name)
    {
        var path = FilePath(titleId, category, name);
        return CachedCrc(path, new FileInfo(path));
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

    public string Write(string titleId, string category, string name, byte[] data)
    {
        EnsureCategory(titleId, category);
        var path = FilePath(titleId, category, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>The suffix the half-written file carries while <see cref="WriteAtomic"/> runs.</summary>
    public const string PartialExtension = ".part";

    /// <summary>
    /// The same as <see cref="Write"/>, except that the target never exists half written: the
    /// bytes go to a temporary name in the same folder and are moved over the target once every
    /// one of them is there. A save that fails part way through leaves the library as it was,
    /// which matters because a truncated .sav is exactly what crashes the game when it is loaded
    /// back onto the console.
    /// </summary>
    public string WriteAtomic(string titleId, string category, string name, byte[] data)
    {
        EnsureCategory(titleId, category);
        var path = FilePath(titleId, category, name);
        var partial = path + PartialExtension;

        try
        {
            File.WriteAllBytes(partial, data);
            File.Move(partial, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
            catch (IOException)
            {
                // The failure that is being thrown is the one worth reporting.
            }

            throw;
        }

        return path;
    }

    public void Delete(string titleId, string category, string name)
    {
        var path = FilePath(titleId, category, name);
        if (File.Exists(path)) File.Delete(path);
    }

    public void Rename(string titleId, string category, string from, string to)
    {
        var source = FilePath(titleId, category, from);
        var destination = FilePath(titleId, category, to);
        if (string.Equals(source, destination, StringComparison.Ordinal)) return;
        if (File.Exists(destination)) throw new IOException($"'{Path.GetFileName(destination)}' already exists");
        File.Move(source, destination);
    }

    /// <summary>
    /// Keeps a typed name inside the library: no separators, no traversal, no characters the
    /// host filesystem rejects. An empty result falls back to <paramref name="fallback"/>.
    /// </summary>
    public static string Sanitise(string? value, string fallback)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(trimmed.Where(c => Array.IndexOf(invalid, c) < 0 && c is not ('/' or '\\')).ToArray())
            .Trim(' ', '.');

        return cleaned.Length == 0 ? fallback : cleaned;
    }
}

/// <summary>One file in the PC's mirror: its name, its size and its CRC32.</summary>
public readonly record struct LocalSaveFile(string Name, long Size, uint Crc);

/// <summary>Which side of the wire a save is on, once the two listings are merged.</summary>
public enum SaveFileLocation
{
    /// <summary>Only on the console. Loading it needs nothing sent.</summary>
    Console,

    /// <summary>Only in the PC's mirror. Loading it uploads it once, and then it is on both.</summary>
    Pc,

    /// <summary>Both sides have it, whether or not the two copies are the same bytes.</summary>
    Both,
}

/// <summary>
/// One row of the merged savefile list: a name, which sides have it, and whether the two copies
/// agree. The console is the library of record and the PC keeps a mirror, so a row is the whole
/// truth about one save rather than one side's idea of it.
/// </summary>
public readonly record struct SaveFileEntry(
    string Name,
    bool OnConsole,
    bool OnPc,
    uint ConsoleCrc,
    uint PcCrc,
    uint ConsoleSize,
    long PcSize)
{
    public SaveFileLocation Location =>
        OnConsole && OnPc ? SaveFileLocation.Both
        : OnConsole ? SaveFileLocation.Console
        : SaveFileLocation.Pc;

    /// <summary>Both sides have it and the bytes are the same.</summary>
    public bool Agrees => OnConsole && OnPc && ConsoleCrc == PcCrc;

    /// <summary>Both sides have it and the bytes are not. The PC's copy is the one a load sends.</summary>
    public bool Differs => OnConsole && OnPc && ConsoleCrc != PcCrc;

    /// <summary>The name without the <c>.sav</c> every file in the library carries.</summary>
    public string DisplayName => SaveFileLibrary.DisplayName(Name);

    /// <summary>What the row says about where it lives, in the words the panel prints.</summary>
    public string Where => Location switch
    {
        SaveFileLocation.Console => "console",
        SaveFileLocation.Pc => "PC",
        _ => Differs ? "both, differ" : "both",
    };

    /// <summary>Whatever size is known, the console's for preference: it is the record.</summary>
    public long Size => OnConsole ? ConsoleSize : PcSize;
}

/// <summary>What loading a save has to do before the console can be asked to take it.</summary>
public enum SaveFileLoadPlan
{
    /// <summary>The console already has these exact bytes: RESTORE and nothing else.</summary>
    Restore,

    /// <summary>Upload the PC's copy once with the file ops, then RESTORE.</summary>
    UploadThenRestore,

    /// <summary>Neither side has the file. Nothing to do but say so.</summary>
    Nothing,
}

/// <summary>
/// Merging the console's listing with the PC's mirror. Pure, so the four cases - console only, PC
/// only, both agreeing, both differing - are checked without a socket.
/// </summary>
public static class SaveFileMerge
{
    public static SaveFileEntry[] Build(
        IEnumerable<ConsoleSaveFile>? console,
        IEnumerable<LocalSaveFile>? pc)
    {
        var rows = new Dictionary<string, SaveFileEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in console ?? Enumerable.Empty<ConsoleSaveFile>())
        {
            if (string.IsNullOrEmpty(file.Name)) continue;
            rows[file.Name] = new SaveFileEntry(file.Name, true, false, file.Crc, 0, file.Size, 0);
        }

        foreach (var file in pc ?? Enumerable.Empty<LocalSaveFile>())
        {
            if (string.IsNullOrEmpty(file.Name)) continue;

            // The PC keeps .part files while a mirror is being written and nothing else; they are
            // not saves and must never show up as one.
            if (file.Name.EndsWith(SaveFileLibrary.PartialExtension, StringComparison.OrdinalIgnoreCase)) continue;

            rows[file.Name] = rows.TryGetValue(file.Name, out var existing)
                ? existing with { OnPc = true, PcCrc = file.Crc, PcSize = file.Size }
                : new SaveFileEntry(file.Name, false, true, 0, file.Crc, 0, file.Size);
        }

        return rows.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// What a load has to do. The console's copy is used whenever it is the same bytes, which is
    /// the whole point of moving the library over there; the PC's copy wins when they differ,
    /// because the row the user picked is the one they can see the date and the size of.
    /// </summary>
    public static SaveFileLoadPlan PlanLoad(SaveFileEntry entry)
    {
        if (entry.OnConsole && (!entry.OnPc || entry.ConsoleCrc == entry.PcCrc)) return SaveFileLoadPlan.Restore;
        if (entry.OnPc) return SaveFileLoadPlan.UploadThenRestore;
        return SaveFileLoadPlan.Nothing;
    }
}

/// <summary>
/// The two savefile sequences, written against the savefile block (section 5.12) and the two
/// flagged ACTIONs: no addresses, no game knowledge, no file on the console. Kept out of the
/// panel so it can be tested against the fake server without a window.
/// </summary>
public static class SaveFileTransfer
{
    /// <summary>
    /// How long the console is given to answer a request before the sequence gives up. The
    /// helper runs once a frame, so a request that is still outstanding after this has not been
    /// reached at all: the game is on a screen that does not call the hook, most likely a
    /// loading screen or a menu.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How often the pending bits are polled while a request is outstanding.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Thrown when a request the helper should have answered is still outstanding after
    /// <see cref="DefaultTimeout"/>, so the caller can say something better than "it hung".
    /// </summary>
    public sealed class NotAnsweredException : Exception
    {
        public NotAnsweredException(string message) : base(message) { }
    }

    /// <summary>
    /// FEATURE_TRIGGER the SAVE_ASIDE action, poll SAVEFILE_INFO until the set-aside bit clears,
    /// then read the whole aside buffer in 64 KB chunks.
    /// <para>
    /// Every chunk is read before this returns, and nothing is written to disk here at all: see
    /// <see cref="SaveToLibraryAsync"/> for the reason. A chunk the console refuses throws
    /// straight out of the loop, and a transfer that came back short of the size INFO reported is
    /// refused here rather than handed on as a save file.
    /// </para>
    /// </summary>
    public static async Task<byte[]> DownloadAsync(
        QwarkClient client,
        byte saveActionId,
        TimeSpan? timeout = null,
        IProgress<string>? status = null,
        IProgress<long>? bytes = null,
        CancellationToken cancellationToken = default)
    {
        var info = await client.SaveFileInfoAsync(cancellationToken).ConfigureAwait(false);
        if (!info.Supported)
        {
            throw new NotAnsweredException("this game has no savefile helper on the console");
        }

        status?.Report($"FEATURE_TRIGGER {saveActionId} (set aside)");
        await client.FeatureTriggerAsync(saveActionId, cancellationToken).ConfigureAwait(false);

        status?.Report("Waiting for the save...");
        info = await WaitAsync(client, i => !i.SetAsidePending, timeout, cancellationToken)
            .ConfigureAwait(false);

        status?.Report($"Reading {info.Size} bytes from the console");
        var data = await client.SaveFileDownloadAsync(info.Size, bytes, cancellationToken)
            .ConfigureAwait(false);

        if (data.Length != info.Size)
        {
            throw new NotAnsweredException(
                $"the console sent {data.Length} bytes of a {info.Size}-byte save");
        }

        return data;
    }

    /// <summary>
    /// A save, end to end: read the whole buffer and only then put a file in the library, under a
    /// temporary name that is moved over the target once the bytes are all there.
    /// <para>
    /// The order is the point. A file written chunk by chunk as they arrive is a whole save file
    /// as far as the library is concerned the moment the first chunk lands, and a save that is cut
    /// short - by a console that stops answering, by a game that quits mid-transfer - is the file
    /// that crashes the game when it is loaded back weeks later. Nothing is left behind when this
    /// throws.
    /// </para>
    /// </summary>
    public static async Task<string> SaveToLibraryAsync(
        QwarkClient client,
        byte saveActionId,
        SaveFileLibrary library,
        string titleId,
        string category,
        string name,
        TimeSpan? timeout = null,
        IProgress<string>? status = null,
        IProgress<long>? bytes = null,
        CancellationToken cancellationToken = default)
    {
        var data = await DownloadAsync(client, saveActionId, timeout, status, bytes, cancellationToken)
            .ConfigureAwait(false);

        status?.Report($"Writing {data.Length} bytes to the library");
        return library.WriteAtomic(titleId, category, name, data);
    }

    /// <summary>
    /// Write the local bytes into the aside buffer, then FEATURE_TRIGGER the LOAD_ASIDE action
    /// and wait for the helper to take it.
    /// <para>
    /// The file has to be exactly the size INFO reports. A save file for a game is one fixed
    /// length, so anything else is either a file for another game or one that was truncated on
    /// the way in; the console cannot tell, and the game would take the buffer either way. The
    /// chunks then go out strictly in order, each one answered before the next is sent, and the
    /// action fires only after the last of them, so the game never sees a half-written buffer.
    /// </para>
    /// </summary>
    public static async Task UploadAsync(
        QwarkClient client,
        byte loadActionId,
        ReadOnlyMemory<byte> data,
        TimeSpan? timeout = null,
        IProgress<string>? status = null,
        IProgress<long>? bytes = null,
        CancellationToken cancellationToken = default)
    {
        var info = await client.SaveFileInfoAsync(cancellationToken).ConfigureAwait(false);
        if (!info.Supported)
        {
            throw new NotAnsweredException("this game has no savefile helper on the console");
        }

        if (data.Length != info.Size)
        {
            throw new NotAnsweredException(
                $"the file is {data.Length} bytes and this game's save is exactly {info.Size}, " +
                "so it is not a save this game can take");
        }

        status?.Report($"Writing {data.Length} bytes to the console");
        await client.SaveFileUploadAsync(data, bytes, cancellationToken).ConfigureAwait(false);

        status?.Report($"FEATURE_TRIGGER {loadActionId} (load set aside)");
        await client.FeatureTriggerAsync(loadActionId, cancellationToken).ConfigureAwait(false);

        status?.Report("Waiting for game...");
        await WaitAsync(client, i => !i.LoadPending, timeout, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------- section 5.13, the console's library

    /// <summary>
    /// Thrown when the console's own copy stopped early. The <see cref="Error"/> is what
    /// SAVEFILE_INFO reported, so the panel can name it rather than say "it failed".
    /// </summary>
    public sealed class TransferFailedException : Exception
    {
        public TransferFailedException(SaveFileError error)
            : base($"the console could not finish: {error.Describe()}")
        {
            Error = error;
        }

        public SaveFileError Error { get; }
    }

    /// <summary>
    /// Polls SAVEFILE_INFO until the transfer bit clears, reporting the byte count as it moves,
    /// and throws when the console reports an error rather than letting it pass as success.
    /// <para>
    /// The timeout is generous on purpose. The console copies 128 KB a tick, so a 2 MB save is
    /// about an eighth of a second of copying, but a STORE waits for the game's helper first and
    /// the helper only runs while the game is actually running a frame.
    /// </para>
    /// </summary>
    public static async Task<SaveFileInfo> WaitForTransferAsync(
        QwarkClient client,
        TimeSpan? timeout = null,
        IProgress<long>? bytes = null,
        CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? DefaultTimeout;
        var deadline = DateTime.UtcNow + limit;

        while (true)
        {
            var info = await client.SaveFileInfoAsync(cancellationToken).ConfigureAwait(false);
            bytes?.Report(info.Done);

            if (!info.TransferPending)
            {
                if (info.Error != SaveFileError.None) throw new TransferFailedException(info.Error);
                return info;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new NotAnsweredException(
                    $"the console was still copying after {limit.TotalSeconds:0.#} s. The helper only " +
                    "runs while the game is running, so try again once the game is in play.");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A save, end to end, with the console keeping it: SAVEFILE_STORE, poll to completion, and
    /// then, when <paramref name="mirror"/> is on, read the file back down into the PC's copy.
    /// <para>
    /// The console writes its file whole before it says it is done, and the mirror is written the
    /// way it always was: every byte first, then one atomic move over the target. A mirror that
    /// fails leaves the console's copy exactly where it is, which is why it is reported separately
    /// rather than turning the whole save into a failure.
    /// </para>
    /// </summary>
    public static async Task<string> StoreAsync(
        QwarkClient client,
        SaveFileLibrary library,
        string titleId,
        string category,
        string name,
        bool mirror,
        TimeSpan? timeout = null,
        IProgress<string>? status = null,
        IProgress<long>? bytes = null,
        CancellationToken cancellationToken = default)
    {
        status?.Report($"Asking the console to save '{SaveFileLibrary.DisplayName(name)}'...");
        await client.SaveFileStoreAsync(category, name, cancellationToken).ConfigureAwait(false);

        status?.Report("Waiting for save...");
        var info = await WaitForTransferAsync(client, timeout, bytes, cancellationToken).ConfigureAwait(false);

        if (!mirror) return string.Empty;

        status?.Report($"Mirroring {info.Total} bytes to this PC");
        var path = QwarkClient.SaveFileConsolePath(titleId, category, name);
        var data = await client.ReadFileAsync(path, bytes, cancellationToken).ConfigureAwait(false);

        return library.WriteAtomic(titleId, category, name, data);
    }

    /// <summary>
    /// Uploads the PC's copy to the console with the file ops and writes the CRC sidecar beside
    /// it, exactly as the mod library writes <c>qwark.sum</c>. Writing the sum here is what saves
    /// the console reading a 2 MB file back to sum it the first time it lists the category.
    /// </summary>
    public static async Task UploadToConsoleAsync(
        QwarkClient client,
        SaveFileLibrary library,
        string titleId,
        string category,
        string name,
        IProgress<string>? status = null,
        IProgress<long>? bytes = null,
        CancellationToken cancellationToken = default)
    {
        var data = library.Read(titleId, category, name);
        var path = QwarkClient.SaveFileConsolePath(titleId, category, name);

        status?.Report($"Uploading {data.Length} bytes to the console");

        // The category may only exist here, and a file cannot be written into a folder that is
        // not there. Creating one that already exists is not an error.
        await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, category, cancellationToken)
            .ConfigureAwait(false);

        await client.WriteFileAsync(path, data, bytes, cancellationToken).ConfigureAwait(false);
        await client.WriteFileAsync(path + QwarkClient.SaveFileSumExtension,
                System.Text.Encoding.ASCII.GetBytes(Crc32.ToSumText(Crc32.Compute(data))),
                progress: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A load, end to end: RESTORE when the console already has these bytes, upload once and then
    /// RESTORE when it does not. Returns whether anything had to be uploaded, so the panel can say
    /// so the one time it happens.
    /// </summary>
    public static async Task<bool> LoadAsync(
        QwarkClient client,
        SaveFileLibrary library,
        string titleId,
        string category,
        SaveFileEntry entry,
        TimeSpan? timeout = null,
        IProgress<string>? status = null,
        IProgress<long>? bytes = null,
        CancellationToken cancellationToken = default)
    {
        var plan = SaveFileMerge.PlanLoad(entry);
        if (plan == SaveFileLoadPlan.Nothing)
        {
            throw new NotAnsweredException($"neither side has '{entry.DisplayName}' any more");
        }

        bool uploaded = plan == SaveFileLoadPlan.UploadThenRestore;
        if (uploaded)
        {
            await UploadToConsoleAsync(client, library, titleId, category, entry.Name, status, bytes, cancellationToken)
                .ConfigureAwait(false);
        }

        status?.Report($"Asking the console to load '{entry.DisplayName}'...");
        await client.SaveFileRestoreAsync(category, entry.Name, cancellationToken).ConfigureAwait(false);

        status?.Report("Waiting for game...");
        await WaitForTransferAsync(client, timeout, bytes, cancellationToken).ConfigureAwait(false);

        return uploaded;
    }

    /// <summary>
    /// Deletes a save from both sides. The console's sidecar goes with it, and a sidecar that is
    /// not there is not a failure: a file the client uploaded before this revision has none.
    /// </summary>
    public static async Task DeleteAsync(
        QwarkClient client,
        SaveFileLibrary library,
        string titleId,
        string category,
        SaveFileEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (entry.OnPc) library.Delete(titleId, category, entry.Name);
        if (!entry.OnConsole) return;

        var path = QwarkClient.SaveFileConsolePath(titleId, category, entry.Name);
        await client.FileDeleteAsync(path, cancellationToken).ConfigureAwait(false);
        await IgnoreMissingAsync(client.FileDeleteAsync(path + QwarkClient.SaveFileSumExtension, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Renames a save on both sides, the sidecar with it.</summary>
    public static async Task RenameAsync(
        QwarkClient client,
        SaveFileLibrary library,
        string titleId,
        string category,
        SaveFileEntry entry,
        string newName,
        CancellationToken cancellationToken = default)
    {
        if (entry.OnPc) library.Rename(titleId, category, entry.Name, newName);
        if (!entry.OnConsole) return;

        var from = QwarkClient.SaveFileConsolePath(titleId, category, entry.Name);
        var to = QwarkClient.SaveFileConsolePath(titleId, category, newName);
        await client.FileRenameAsync(from, to, cancellationToken).ConfigureAwait(false);
        await IgnoreMissingAsync(client.FileRenameAsync(
                from + QwarkClient.SaveFileSumExtension,
                to + QwarkClient.SaveFileSumExtension,
                cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>A sidecar that is not there is not a failure; every other status still is.</summary>
    private static async Task IgnoreMissingAsync(Task work)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (QwarkStatusException ex) when (ex.Status is Status.NotFound or Status.BadArg)
        {
            // No sum beside the file, or nowhere to move one to. Nothing to put right.
        }
    }

    /// <summary>Polls SAVEFILE_INFO until <paramref name="done"/> or the timeout runs out.</summary>
    private static async Task<SaveFileInfo> WaitAsync(
        QwarkClient client,
        Func<SaveFileInfo, bool> done,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var limit = timeout ?? DefaultTimeout;
        var deadline = DateTime.UtcNow + limit;

        while (true)
        {
            var info = await client.SaveFileInfoAsync(cancellationToken).ConfigureAwait(false);
            if (done(info)) return info;

            if (DateTime.UtcNow >= deadline)
            {
                throw new NotAnsweredException(
                    $"the console did not answer within {limit.TotalSeconds:0.#} s. The helper only " +
                    "runs while the game is running, so try again once the game is in play.");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
