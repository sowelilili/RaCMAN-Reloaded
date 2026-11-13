using System.Text;

namespace RaCMAN.Protocol;

/// <summary>Which part of the update a failure belongs to, for the one message the user sees.</summary>
public enum SprxUpdateStep
{
    /// <summary>
    /// Writing the new build's ui.pak beside the console's own as <see cref="UiPak.StagedPath"/>
    /// and reading it back. The first step, so a failure here leaves the module alone too.
    /// </summary>
    UiPak,

    /// <summary>
    /// Writing the new build's game modules (revision 1.26) beside the console's
    /// own as <see cref="GameModule.StagedPath"/> and reading each back. Before the module is
    /// touched, so a failure here leaves it alone too.
    /// </summary>
    GameModule,

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

/// <summary>
/// What the update did, for the message and for a test to read. <see cref="UiPakStaged"/> is false
/// when the client had no ui.pak to send, and the console then keeps whatever pak it has.
/// <see cref="GameModulesStaged"/> counts the game modules staged with it: none for a client
/// with none beside its module.
/// </summary>
public readonly record struct SprxUpdateResult(
    string BootPath, string BackupPath, bool ReplacedACopy, int Bytes, bool UiPakStaged = false, int GameModulesStaged = 0);

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
/// The console menu's ui.pak goes first, and only ever as <see cref="UiPak.StagedPath"/>: the
/// module that is running reads its own pak until the restart, and the next build's would be a bad
/// one to it. The new module moves the staged pak into place when it starts, before it checks it.
/// A failure there stops the update before the module is touched.
/// </para>
/// <para>
/// The game modules a release ships (revision 1.26; since qwark build 57, as a test build always did) go the same way, each as
/// <see cref="GameModule.StagedPath"/>, after the pak: the running module keeps the modules it has,
/// and the new one puts each staged module in place the next time its game boots. A module that
/// links its games in (build 56's release) never looks at them.
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
    public static string BootPathFrom(string? bootPluginsText) =>
        ListedBootPath(bootPluginsText) ?? WebManLoader.BootPath;

    /// <summary>
    /// The path <see cref="BootPathFrom"/> finds on the list, or null when the list names none:
    /// a console that does not boot qwark at all, which a webMAN load leaves that way.
    /// </summary>
    public static string? ListedBootPath(string? bootPluginsText)
    {
        foreach (var raw in (bootPluginsText ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '/') continue;
            if (line.EndsWith(WebManLoader.SprxName, StringComparison.OrdinalIgnoreCase)) return line;
        }

        return null;
    }

    /// <summary>
    /// Puts <paramref name="sprx"/> where the console boots its module from, and
    /// <paramref name="uiPak"/>, when there is one, where the new module will look for it at its
    /// start, and <paramref name="gameModules"/> where it puts them in place as each game boots. The
    /// console has to be restarted afterwards: a VSH plugin is read once, at boot, and this client
    /// cannot restart it.
    /// </summary>
    public static async Task<SprxUpdateResult> RunAsync(
        QwarkClient client,
        ReadOnlyMemory<byte> sprx,
        byte[]? uiPak = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<GameModuleFile>? gameModules = null)
    {
        if (uiPak is not null)
        {
            progress?.Report($"Uploading {UiPak.FileName}");
            try
            {
                await client.WriteFileAsync(UiPak.StagedPath, uiPak, progress: null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new SprxUpdateException(SprxUpdateStep.UiPak,
                    $"uploading {UiPak.FileName}", error);
            }

            try
            {
                await VerifyAsync(client, UiPak.StagedPath, uiPak, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new SprxUpdateException(SprxUpdateStep.UiPak, $"checking {UiPak.FileName}", error);
            }
        }

        int modules = 0;
        foreach (var module in gameModules ?? Array.Empty<GameModuleFile>())
        {
            string staged = GameModule.StagedPath(module.Game);
            progress?.Report($"Uploading {module.FileName}");
            try
            {
                await client.WriteFileAsync(staged, module.Bytes, progress: null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new SprxUpdateException(SprxUpdateStep.GameModule, $"uploading {module.FileName}", error);
            }

            try
            {
                await VerifyAsync(client, staged, module.Bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new SprxUpdateException(SprxUpdateStep.GameModule, $"checking {module.FileName}", error);
            }

            modules++;
        }

        string bootPath;
        try
        {
            bootPath = BootPathFrom(await ReadBootPluginsAsync(client, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new SprxUpdateException(SprxUpdateStep.BootPath,
                "reading boot_plugins.txt", error);
        }

        bool replaced = await ReplaceBootCopyAsync(new QwarkFiles(client), bootPath, sprx, progress, cancellationToken)
            .ConfigureAwait(false);
        return new SprxUpdateResult(bootPath, bootPath + BackupSuffix, replaced, sprx.Length,
            UiPakStaged: uiPak is not null, GameModulesStaged: modules);
    }

    /// <summary>
    /// The boot copy's own replacement, over whichever link reaches the console: qwark's file ops
    /// here, webMAN's FTP server for a webMAN load (<see cref="WebManLoader.LoadAsync"/>). The new
    /// module is written as <c>&lt;bootPath&gt;.new</c>, read back and checked, and only then
    /// swapped in, the old copy kept as <c>.old</c>; a swap that fails half way puts the old copy
    /// back. So every failure leaves the console booting the module it booted before, and is one
    /// <see cref="SprxUpdateException"/> naming its step. Answers whether there was a copy to keep.
    /// </summary>
    internal static async Task<bool> ReplaceBootCopyAsync(
        IConsoleFiles files,
        string bootPath,
        ReadOnlyMemory<byte> sprx,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string temporary = bootPath + TempSuffix;
        string backup = bootPath + BackupSuffix;

        progress?.Report($"Uploading {WebManLoader.SprxName}");
        try
        {
            await files.WriteAsync(temporary, sprx, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new SprxUpdateException(SprxUpdateStep.Upload, "uploading it", error);
        }

        try
        {
            await VerifyAsync(files, temporary, sprx, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new SprxUpdateException(SprxUpdateStep.Verify, "checking it", error);
        }

        progress?.Report($"Putting {WebManLoader.SprxName} in place");
        bool replaced;
        try
        {
            // A rename refuses a destination that exists (cellFs does), so the previous .old goes
            // first and the boot copy moves onto the name it frees. A boot list that names a file
            // which is not there is nothing to put right: the upload still becomes the boot copy.
            await files.DeleteAsync(backup, cancellationToken).ConfigureAwait(false);
            replaced = await files.RenameAsync(bootPath, backup, cancellationToken).ConfigureAwait(false);

            try
            {
                if (!await files.RenameAsync(temporary, bootPath, cancellationToken).ConfigureAwait(false))
                {
                    throw new IOException($"{temporary} is not there any more");
                }
            }
            catch
            {
                // The one moment the console has no module at the boot path. Put the old one back,
                // so a swap that failed half way leaves the console booting what it booted before.
                if (replaced) await files.RenameAsync(backup, bootPath, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new SprxUpdateException(SprxUpdateStep.Swap, "putting it in place", error);
        }

        progress?.Report($"{WebManLoader.SprxName} is in place");
        return replaced;
    }

    /// <summary>
    /// Reads <paramref name="path"/> back and checks it against what was sent: the length, then the
    /// CRC32. The module's upload and every upload of ui.pak are checked this one way.
    /// </summary>
    internal static Task VerifyAsync(
        QwarkClient client, string path, ReadOnlyMemory<byte> sent, CancellationToken cancellationToken) =>
        VerifyAsync(new QwarkFiles(client), path, sent, cancellationToken);

    /// <summary><see cref="VerifyAsync(QwarkClient, string, ReadOnlyMemory{byte}, CancellationToken)"/> over either link.</summary>
    internal static async Task VerifyAsync(
        IConsoleFiles files, string path, ReadOnlyMemory<byte> sent, CancellationToken cancellationToken)
    {
        var written = await files.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (written.Length != sent.Length)
        {
            throw new IOException($"the console holds {written.Length} bytes of it, not {sent.Length}");
        }

        if (Crc32.Compute(written) != Crc32.Compute(sent.Span))
        {
            throw new IOException("the copy on the console does not match the one that was sent");
        }
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

    /// <summary>
    /// Whether a delete or a move happened: a file that is not there is already where this wanted
    /// to put it, which decides what a failed swap undoes; every other status still is a failure.
    /// </summary>
    internal static async Task<bool> MovedAsync(Task work)
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

    /// <summary>The boot copy's swap over qwark's own file ops.</summary>
    private sealed class QwarkFiles : IConsoleFiles
    {
        private readonly QwarkClient _client;

        public QwarkFiles(QwarkClient client) => _client = client;

        public Task WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
            _client.WriteFileAsync(path, bytes, progress: null, cancellationToken);

        public Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken) =>
            _client.ReadFileAsync(path, progress: null, cancellationToken);

        public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken) =>
            MovedAsync(_client.FileDeleteAsync(path, cancellationToken));

        public Task<bool> RenameAsync(string from, string to, CancellationToken cancellationToken) =>
            MovedAsync(_client.FileRenameAsync(from, to, cancellationToken));
    }
}

/// <summary>
/// The four file operations the boot copy's swap needs (<see cref="SprxUpdate.ReplaceBootCopyAsync"/>),
/// over qwark's file ops or webMAN's FTP server. A delete or a rename answers false when there is
/// no file of the name it was given, and throws for any other refusal.
/// </summary>
internal interface IConsoleFiles
{
    Task WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string path, CancellationToken cancellationToken);

    Task<bool> RenameAsync(string from, string to, CancellationToken cancellationToken);
}
