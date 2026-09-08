namespace RaCMAN.Protocol;

/// <summary>
/// The PC-side savefile library: <c>savefiles/&lt;TITLEID&gt;/&lt;category&gt;/&lt;name&gt;</c> beside the
/// executable, the same layout the old client used. Nothing here knows anything about the game:
/// a saved file is whatever bytes the console's tempsave held.
/// </summary>
public sealed class SaveFileLibrary
{
    /// <summary>The folder offered when a title has no categories yet.</summary>
    public const string DefaultCategory = "misc";

    public SaveFileLibrary(string rootPath)
    {
        RootPath = rootPath;
    }

    public string RootPath { get; }

    /// <summary>
    /// Where the game writes its set-aside save. Fixed by the console side, section 5.3: the
    /// SAVE_ASIDE action writes it and the LOAD_ASIDE action reads it back.
    /// </summary>
    public static string TempSavePath(string titleId) => $"/dev_hdd0/game/{titleId}/USRDIR/tempsave";

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
/// The two savefile sequences, written against nothing but the generic file ops and the two
/// flagged ACTIONs: no addresses, no game knowledge. Kept out of the panel so it can be tested
/// against the fake server without a window.
/// </summary>
public static class SaveFileTransfer
{
    /// <summary>
    /// How long the game is given to finish writing tempsave after the action fires. The old
    /// client slept two seconds and retried once after two more; this reproduces that.
    /// </summary>
    public static readonly TimeSpan DefaultSettle = TimeSpan.FromSeconds(2);

    /// <summary>
    /// FEATURE_TRIGGER the SAVE_ASIDE action, wait, then read the tempsave file. A NOT_FOUND or
    /// IO_ERROR means the game had not finished writing, so it waits again and retries once.
    /// </summary>
    public static async Task<byte[]> DownloadAsync(
        QwarkClient client,
        byte saveActionId,
        string titleId,
        TimeSpan? settle = null,
        IProgress<string>? status = null,
        IProgress<long>? bytes = null,
        CancellationToken cancellationToken = default)
    {
        var delay = settle ?? DefaultSettle;
        string path = SaveFileLibrary.TempSavePath(titleId);

        status?.Report($"FEATURE_TRIGGER {saveActionId} (set aside)");
        await client.FeatureTriggerAsync(saveActionId, cancellationToken).ConfigureAwait(false);

        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

        status?.Report($"Reading {path}");
        try
        {
            return await client.ReadFileAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (QwarkStatusException ex) when (ex.Status is Status.NotFound or Status.IoError)
        {
            status?.Report($"{path} was not ready ({ex.Status}); waiting and retrying once");
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return await client.ReadFileAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Write the local bytes over the console's tempsave, then FEATURE_TRIGGER the LOAD_ASIDE
    /// action so the game picks the file up.
    /// </summary>
    public static async Task UploadAsync(
        QwarkClient client,
        byte loadActionId,
        string titleId,
        ReadOnlyMemory<byte> data,
        IProgress<string>? status = null,
        IProgress<long>? bytes = null,
        CancellationToken cancellationToken = default)
    {
        string path = SaveFileLibrary.TempSavePath(titleId);

        status?.Report($"Writing {data.Length} bytes to {path}");
        await client.WriteFileAsync(path, data, bytes, cancellationToken).ConfigureAwait(false);

        status?.Report($"FEATURE_TRIGGER {loadActionId} (load set aside)");
        await client.FeatureTriggerAsync(loadActionId, cancellationToken).ConfigureAwait(false);
    }
}
