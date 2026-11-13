namespace RaCMAN.Protocol;

/// <summary>Which part of <see cref="GameModuleSend"/> a failure belongs to.</summary>
public enum GameModuleSendStep
{
    /// <summary>The copy beside the client: not there, not readable, or not a SELF. Nothing was sent.</summary>
    Local,

    /// <summary>Writing the module beside the console's own, under its staged name.</summary>
    Upload,

    /// <summary>Reading the upload back and checking it against the bytes that were sent.</summary>
    Verify,

    /// <summary>Deleting the console's copy and renaming the upload to its name. The only step that touches it.</summary>
    Replace,
}

/// <summary>
/// One step of <see cref="GameModuleSend"/> failed. The message is the whole sentence the user
/// sees: it names the step and carries what the console answered.
/// </summary>
public sealed class GameModuleSendException : Exception
{
    public GameModuleSendException(GameModuleSendStep step, string message, Exception? inner = null)
        : base(message, inner)
    {
        Step = step;
    }

    public GameModuleSendStep Step { get; }
}

/// <summary>What a send did, for a test to read.</summary>
public readonly record struct GameModuleSendResult(GameId Game, int Bytes, bool ReplacedACopy);

/// <summary>
/// Puts this client's module for one game on a console whose qwark is current and whose module for
/// the running game is missing, broken or another build's (<see cref="SessionInfo.GameModuleBad"/>),
/// through qwark's own file ops. The module looks at the file again when the rename lands, and
/// loads it without a reboot once the memory gate lets it (PROTOCOL.md 9.11).
/// <para>
/// The order is <see cref="UiPakSend"/>'s: written as <see cref="GameModule.StagedPath"/>, read
/// back and checked, the old copy deleted (FILE_RENAME refuses a destination that exists; NOT_FOUND
/// is what a console with none answers), and the upload renamed into place. A rename that fails
/// after the delete leaves the checked upload under the staged name, which the module puts in
/// place itself the next time that game boots.
/// </para>
/// </summary>
public static class GameModuleSend
{
    /// <summary>The send itself, from the copy at <paramref name="localPath"/>.</summary>
    public static async Task<GameModuleSendResult> RunAsync(
        QwarkClient client,
        GameId game,
        string localPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var module = await ReadLocalAsync(game, localPath, cancellationToken).ConfigureAwait(false);
        return await SendAsync(client, game, module, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The copy beside the client, if it is worth sending. Every reason it is not is a
    /// <see cref="GameModuleSendStep.Local"/> failure with a sentence that says why.
    /// </summary>
    public static async Task<byte[]> ReadLocalAsync(GameId game, string localPath, CancellationToken cancellationToken = default)
    {
        if (!GameModule.HasModule(game))
        {
            throw new GameModuleSendException(GameModuleSendStep.Local, "This game has no game module");
        }

        byte[] module;
        try
        {
            module = await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new GameModuleSendException(GameModuleSendStep.Local,
                $"No {GameModule.FileName(game)} beside this client to send", error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new GameModuleSendException(GameModuleSendStep.Local, $"Couldn't read {GameModule.FileName(game)}: {error.Message}", error);
        }

        if (Problem(game, module, localPath) is { } problem) throw new GameModuleSendException(GameModuleSendStep.Local, problem);
        return module;
    }

    /// <summary>Why <paramref name="module"/> could not be a module, or null when it could.</summary>
    public static string? Problem(GameId game, ReadOnlySpan<byte> module, string localPath) =>
        GameModule.LooksLikeSprx(module)
            ? null
            : $"{Path.GetFileName(localPath)} isn't a signed SPRX; not sent";

    /// <summary>The four steps against the console: upload, read back, delete the old copy, rename.</summary>
    public static async Task<GameModuleSendResult> SendAsync(
        QwarkClient client,
        GameId game,
        ReadOnlyMemory<byte> module,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!GameModule.HasModule(game))
        {
            throw new GameModuleSendException(GameModuleSendStep.Local, "This game has no game module");
        }

        string name = GameModule.FileName(game);
        if (Problem(game, module.Span, name) is { } problem) throw new GameModuleSendException(GameModuleSendStep.Local, problem);

        string staged = GameModule.StagedPath(game);
        string target = GameModule.ConsolePath(game);
        string failed = $"Sending {name} failed while";

        progress?.Report($"Uploading {name}");
        try
        {
            await client.WriteFileAsync(staged, module, progress: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new GameModuleSendException(GameModuleSendStep.Upload, $"{failed} uploading it: {error.Message}", error);
        }

        try
        {
            await SprxUpdate.VerifyAsync(client, staged, module, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new GameModuleSendException(GameModuleSendStep.Verify, $"{failed} checking it: {error.Message}", error);
        }

        progress?.Report($"Putting {name} in place");
        bool replaced;
        try
        {
            replaced = await SprxUpdate.MovedAsync(client.FileDeleteAsync(target, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new GameModuleSendException(GameModuleSendStep.Replace, $"{failed} deleting the old one: {error.Message}", error);
        }

        try
        {
            await client.FileRenameAsync(staged, target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new GameModuleSendException(GameModuleSendStep.Replace,
                $"{failed} renaming it: {error.Message}", error);
        }

        return new GameModuleSendResult(game, module.Length, replaced);
    }
}
