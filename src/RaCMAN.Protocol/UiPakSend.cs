namespace RaCMAN.Protocol;

/// <summary>Which part of <see cref="UiPakSend"/> a failure belongs to.</summary>
public enum UiPakSendStep
{
    /// <summary>The copy beside the client: not there, not readable, not a pak, or another build's. Nothing was sent.</summary>
    Local,

    /// <summary>Writing the pak beside the console's own, under the name the module only reads at its start.</summary>
    Upload,

    /// <summary>Reading the upload back and checking it against the bytes that were sent.</summary>
    Verify,

    /// <summary>Deleting the console's pak and renaming the upload to its name. The only step that touches it.</summary>
    Replace,
}

/// <summary>
/// One step of <see cref="UiPakSend"/> failed. The message is the whole sentence the user sees: it
/// names the step and carries what the console answered, so the caller shows it as it is.
/// </summary>
public sealed class UiPakSendException : Exception
{
    public UiPakSendException(UiPakSendStep step, string message, Exception? inner = null)
        : base(message, inner)
    {
        Step = step;
    }

    public UiPakSendStep Step { get; }
}

/// <summary>What a send did, for a test to read.</summary>
public readonly record struct UiPakSendResult(int Bytes, bool ReplacedACopy);

/// <summary>
/// Puts this client's ui.pak on a console whose module is current and whose pak is missing or
/// another build's (<see cref="SessionInfo.UiPakBad"/>), through the module's own file ops. The
/// module notices the change by itself: it checks the pak again just after a file op on its path,
/// so the next session block says whether this worked.
/// <para>
/// The order is the standalone update's: the pak is written under <see cref="UiPak.StagedPath"/>,
/// read back and checked before anything else moves, and only then put in place, so a failure
/// before the last step leaves the console's pak as it was. FILE_RENAME refuses a destination that
/// exists, so the old pak is deleted first; a console that has none answers NOT_FOUND, which is
/// what was wanted. A rename that fails after that leaves the checked upload under
/// <see cref="UiPak.StagedPath"/>, which the module moves into place itself at its next start.
/// </para>
/// <para>
/// A copy that could not fix anything is not sent at all: one that is not a pak, or one whose
/// header names a build other than <see cref="QwarkClient.ExpectedQwarkBuild"/>. Whether to send
/// is the caller's question; this only knows how.
/// </para>
/// </summary>
public static class UiPakSend
{
    /// <summary>The send itself, from the copy at <paramref name="localPath"/>.</summary>
    public static async Task<UiPakSendResult> RunAsync(
        QwarkClient client,
        string localPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pak = await ReadLocalAsync(localPath, cancellationToken).ConfigureAwait(false);
        return await SendAsync(client, pak, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The copy beside the client, if it is worth sending. Every reason it is not is a
    /// <see cref="UiPakSendStep.Local"/> failure with a sentence that says why.
    /// </summary>
    public static async Task<byte[]> ReadLocalAsync(string localPath, CancellationToken cancellationToken = default)
    {
        byte[] pak;
        try
        {
            pak = await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new UiPakSendException(UiPakSendStep.Local,
                $"There is no {UiPak.FileName} beside this client to send ({localPath}).", error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new UiPakSendException(UiPakSendStep.Local,
                $"Could not read {localPath}: {error.Message}", error);
        }

        if (Problem(pak, localPath) is { } problem) throw new UiPakSendException(UiPakSendStep.Local, problem);
        return pak;
    }

    /// <summary>
    /// Why <paramref name="pak"/> would not fix the console's menu, or null when it is a pak made
    /// with the build this client expects the console to run.
    /// </summary>
    public static string? Problem(ReadOnlySpan<byte> pak, string localPath)
    {
        if (!UiPak.TryReadBuild(pak, out int build))
        {
            return $"{localPath} is not a {UiPak.FileName} file, so it was not sent.";
        }

        if (build != QwarkClient.ExpectedQwarkBuild)
        {
            return $"The {UiPak.FileName} beside this client is for qwark build {build}, not build "
                   + $"{QwarkClient.ExpectedQwarkBuild}, so sending it would not fix the menu ({localPath}).";
        }

        return null;
    }

    /// <summary>The four steps against the console: upload, read back, delete the old pak, rename.</summary>
    public static async Task<UiPakSendResult> SendAsync(
        QwarkClient client,
        ReadOnlyMemory<byte> pak,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The caller has checked already; this is the one place that cannot be talked past.
        if (Problem(pak.Span, UiPak.FileName) is { } problem) throw new UiPakSendException(UiPakSendStep.Local, problem);

        const string Failed = $"Sending {UiPak.FileName} failed while";

        progress?.Report($"Uploading {UiPak.FileName} to {UiPak.StagedPath}");
        try
        {
            await client.WriteFileAsync(UiPak.StagedPath, pak, progress: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new UiPakSendException(UiPakSendStep.Upload,
                $"{Failed} uploading it to {UiPak.StagedPath}: {error.Message}", error);
        }

        try
        {
            await SprxUpdate.VerifyAsync(client, UiPak.StagedPath, pak, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new UiPakSendException(UiPakSendStep.Verify,
                $"{Failed} checking {UiPak.StagedPath}: {error.Message}", error);
        }

        progress?.Report($"Putting {UiPak.StagedPath} in place of {UiPak.ConsolePath}");
        bool replaced;
        try
        {
            replaced = await SprxUpdate.MovedAsync(client.FileDeleteAsync(UiPak.ConsolePath, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new UiPakSendException(UiPakSendStep.Replace,
                $"{Failed} deleting the old {UiPak.ConsolePath}: {error.Message}", error);
        }

        try
        {
            await client.FileRenameAsync(UiPak.StagedPath, UiPak.ConsolePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new UiPakSendException(UiPakSendStep.Replace,
                $"{Failed} renaming {UiPak.StagedPath} to {UiPak.ConsolePath}: {error.Message}", error);
        }

        return new UiPakSendResult(pak.Length, replaced);
    }
}
