using System.Text;

namespace RaCMAN.Protocol;

/// <summary>Which part of the update a failure belongs to, for the one message the user sees.</summary>
public enum SprxUpdateStep
{
    /// <summary>Reading boot_plugins.txt to find out where the console loads the module from.</summary>
    BootPath,

    /// <summary>Writing the new module beside the boot copy, under a name nothing loads.</summary>
    Upload,

    /// <summary>Reading the upload back and checking it against the bytes that were sent.</summary>
    Verify,

    /// <summary>Moving the old copy aside and the new one into its place. The only step that touches the boot copy.</summary>
    Swap,
}

/// <summary>
/// One step of <see cref="SprxUpdate.RunAsync"/> failed. The message names the step, so a caller
/// has one sentence to show and nothing to work out from an opcode.
/// </summary>
public sealed class SprxUpdateException : Exception
{
    public SprxUpdateException(SprxUpdateStep step, string doing, Exception inner)
        : base($"{doing}: {inner.Message}", inner)
    {
        Step = step;
    }

    public SprxUpdateStep Step { get; }
}

/// <summary>What the update did, for the message and for a test to read.</summary>
public readonly record struct SprxUpdateResult(string BootPath, string BackupPath, bool ReplacedACopy, int Bytes);

/// <summary>
/// Replaces the console's boot copy of qwark.sprx with the one this client shipped, through the
/// module's own file ops. This is the standalone answer to a stale module: nothing here goes near
/// webMAN, because a console that loads the module itself may have no webMAN at all, and the
/// running module is perfectly able to write the file that will replace it at the next boot.
/// <para>
/// The order matters. The new module is written under a name nothing loads, read back and checked
/// before anything else moves, and only then swapped in, so every way this can fail leaves the
/// console booting the module it booted before. A failure does leave the <c>.new</c> file behind:
/// the next attempt truncates it, and rubbish beside the boot copy is worth less than the extra
/// requests a cleanup would make against a console that has just refused one.
/// </para>
/// <para>
/// Nothing here decides <em>whether</em> to update. The caller compares the build HELLO reported
/// against <see cref="QwarkClient.ExpectedQwarkBuild"/>, which is the build the shipped SPRX is,
/// so a console that is ahead of this client is never written over.
/// </para>
/// </summary>
public static class SprxUpdate
{
    /// <summary>The name the upload lands under: beside the boot copy, and not on any boot list.</summary>
    public const string TempSuffix = ".new";

    /// <summary>Where the copy being replaced is kept, so a bad module can be put back by hand.</summary>
    public const string BackupSuffix = ".old";

    /// <summary>
    /// Where the console loads qwark.sprx from, according to its boot plugin list: the first line
    /// naming an absolute path that ends in qwark.sprx. A list that names none — and a console with
    /// no list at all, which reads as empty text — falls back to where the boot install puts it.
    /// </summary>
    public static string BootPathFrom(string? bootPluginsText)
    {
        foreach (var raw in (bootPluginsText ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '/') continue;
            if (line.EndsWith(WebManLoader.SprxName, StringComparison.OrdinalIgnoreCase)) return line;
        }

        return WebManLoader.BootPath;
    }

    /// <summary>
    /// Puts <paramref name="sprx"/> where the console boots its module from. The console has to be
    /// restarted afterwards: a VSH plugin is read once, at boot, and this client cannot restart it.
    /// </summary>
    public static async Task<SprxUpdateResult> RunAsync(
        QwarkClient client,
        ReadOnlyMemory<byte> sprx,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string bootPath;
        try
        {
            bootPath = BootPathFrom(await ReadBootPluginsAsync(client, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new SprxUpdateException(SprxUpdateStep.BootPath,
                $"reading {WebManLoader.BootPluginsPath}", error);
        }

        string temporary = bootPath + TempSuffix;
        string backup = bootPath + BackupSuffix;

        progress?.Report($"Uploading {WebManLoader.SprxName} to {temporary}");
        try
        {
            await client.WriteFileAsync(temporary, sprx, progress: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new SprxUpdateException(SprxUpdateStep.Upload, $"uploading it to {temporary}", error);
        }

        try
        {
            var written = await client.ReadFileAsync(temporary, progress: null, cancellationToken).ConfigureAwait(false);
            if (written.Length != sprx.Length)
            {
                throw new IOException(
                    $"the console holds {written.Length} bytes of it, not {sprx.Length}");
            }

            if (Crc32.Compute(written) != Crc32.Compute(sprx.Span))
            {
                throw new IOException("the copy on the console does not match the one that was sent");
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new SprxUpdateException(SprxUpdateStep.Verify, $"checking {temporary}", error);
        }

        progress?.Report($"Putting {temporary} in place of {bootPath}");
        bool replaced;
        try
        {
            // FILE_RENAME refuses a destination that exists, so the previous .old goes first and the
            // boot copy moves onto the name it frees. A boot list that names a file which is not
            // there is nothing to put right: the upload still becomes the boot copy.
            await IgnoreMissingAsync(client.FileDeleteAsync(backup, cancellationToken)).ConfigureAwait(false);
            replaced = await MovedAsync(client.FileRenameAsync(bootPath, backup, cancellationToken)).ConfigureAwait(false);

            try
            {
                await client.FileRenameAsync(temporary, bootPath, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The one moment the console has no module at the boot path. Put the old one back,
                // so a swap that failed half way leaves the console booting what it booted before.
                if (replaced)
                {
                    await IgnoreMissingAsync(client.FileRenameAsync(backup, bootPath, cancellationToken))
                        .ConfigureAwait(false);
                }

                throw;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new SprxUpdateException(SprxUpdateStep.Swap, $"putting {temporary} in place of {bootPath}", error);
        }

        progress?.Report($"{bootPath} is the new {WebManLoader.SprxName}");
        return new SprxUpdateResult(bootPath, backup, replaced, sprx.Length);
    }

    /// <summary>
    /// The boot plugin list as text. A console that has none answers NOT_FOUND, which says the same
    /// thing an empty list does: nothing names the module, so the caller's default stands.
    /// </summary>
    private static async Task<string> ReadBootPluginsAsync(QwarkClient client, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await client.ReadFileAsync(WebManLoader.BootPluginsPath, progress: null, cancellationToken)
                .ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (QwarkStatusException error) when (error.Status is Status.NotFound)
        {
            return string.Empty;
        }
    }

    /// <summary>A file that is not there is already where this wanted to put it; every other status still is a failure.</summary>
    private static async Task IgnoreMissingAsync(Task work)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (QwarkStatusException error) when (error.Status is Status.NotFound)
        {
            // Nothing of that name on the console.
        }
    }

    /// <summary>The same, and says whether the move happened, which decides what a failed swap undoes.</summary>
    private static async Task<bool> MovedAsync(Task work)
    {
        try
        {
            await work.ConfigureAwait(false);
            return true;
        }
        catch (QwarkStatusException error) when (error.Status is Status.NotFound)
        {
            return false;
        }
    }
}
