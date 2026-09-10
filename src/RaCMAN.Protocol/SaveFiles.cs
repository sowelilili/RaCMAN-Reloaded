namespace RaCMAN.Protocol;

/// <summary>
/// The PC-side savefile library: <c>savefiles/&lt;TITLEID&gt;/&lt;category&gt;/&lt;name&gt;.sav</c> beside
/// the executable, the same layout the old client used. Nothing here knows anything about the
/// game: a saved file is whatever bytes the console's aside buffer held.
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

        status?.Report("Waiting for the game to set the save aside...");
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

        status?.Report("Waiting for the game to take it...");
        await WaitAsync(client, i => !i.LoadPending, timeout, cancellationToken).ConfigureAwait(false);
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
