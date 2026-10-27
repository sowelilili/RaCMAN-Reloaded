namespace RaCMAN.Protocol;

/// <summary>
/// A game's own module (revision 1.26, qwark's PROTOCOL.md 9.11). A qwark test build carries no
/// game: each one is an SPRX of its own, <c>/dev_hdd0/qwark/game-&lt;n&gt;.sprx</c>, <c>n</c>
/// being SessionInfo's game id (1 RaC1 .. 4 Deadlocked), which the module loads while that game
/// runs once the VSH has the memory for it. A release links the games in, ships no module files
/// and never asks for one. The files are handled exactly as <see cref="UiPak"/> is: they sit beside
/// qwark.sprx in qwark's dist\ and in the client's folder, every way this client puts the module on
/// a console puts them beside the pak, and the standalone update stages each as <c>.new</c>, which
/// the module puts in place the next time that game boots.
/// <para>
/// The client cannot tell which build a module is: the SELF's body is encrypted. It checks only
/// that a file is a SELF at all (<see cref="LooksLikeSprx"/>), and sends the ones beside the
/// qwark.sprx it ships, which come out of the same dist\ as it does. Whether a module is good is
/// the console's question, and it answers it with <see cref="SessionFlags2.GameModuleBad"/>.
/// </para>
/// </summary>
public static class GameModule
{
    /// <summary>The games that can have a module, in game-id order.</summary>
    public static IReadOnlyList<GameId> Games { get; } = new[] { GameId.Rac1, GameId.Rac2, GameId.Rac3, GameId.Rac4 };

    /// <summary>The name that matches every module, for a listing: <c>game-*.sprx</c>.</summary>
    public const string Pattern = "game-*.sprx";

    /// <summary>Whether <paramref name="game"/> is one of the four that have a module.</summary>
    public static bool HasModule(GameId game) => game is GameId.Rac1 or GameId.Rac2 or GameId.Rac3 or GameId.Rac4;

    /// <summary><c>game-&lt;n&gt;.sprx</c> for a game id from 1 to 4.</summary>
    public static string FileName(int game)
    {
        if (game is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(game), "game modules are numbered 1 to 4");
        return $"game-{game}.sprx";
    }

    /// <inheritdoc cref="FileName(int)"/>
    public static string FileName(GameId game) => FileName((int)game);

    /// <summary>Where the module reads a game's module from: beside ui.pak, in qwark's folder.</summary>
    public static string ConsolePath(GameId game) => UiPak.ConsoleDirectory + "/" + FileName(game);

    /// <summary>
    /// Where a send and the standalone update stage a game's module. The module puts it in place of
    /// <see cref="ConsolePath"/> the next time that game boots; a send renames it there at once.
    /// </summary>
    public static string StagedPath(GameId game) => ConsolePath(game) + ".new";

    /// <summary>The client's own copy: beside the qwark.sprx it sends, as ui.pak is.</summary>
    public static string BesideSprx(string sprxPath, GameId game) =>
        Path.Combine(Path.GetDirectoryName(sprxPath) ?? string.Empty, FileName(game));

    /// <summary>"SCE\0", the first four bytes of every signed SELF, qwark.sprx included.</summary>
    public static ReadOnlySpan<byte> SelfMagic => "SCE\0"u8;

    /// <summary>
    /// Whether <paramref name="bytes"/> could be a module at all: a signed SELF's magic and more
    /// than its header. Nothing else can be read without the console's keys.
    /// </summary>
    public static bool LooksLikeSprx(ReadOnlySpan<byte> bytes) =>
        bytes.Length > 64 && bytes[..SelfMagic.Length].SequenceEqual(SelfMagic);

    /// <summary>
    /// The modules beside <paramref name="sprxPath"/>, read, in game order: the ones a release of
    /// this client sends with the module. A release has none, and none is an empty list, never an
    /// error; a file that cannot be read is left out as if it were not there.
    /// </summary>
    public static async Task<IReadOnlyList<GameModuleFile>> ReadBesideAsync(
        string sprxPath, CancellationToken cancellationToken = default)
    {
        var found = new List<GameModuleFile>();
        foreach (var game in Games)
        {
            string path = BesideSprx(sprxPath, game);
            if (!File.Exists(path)) continue;

            try
            {
                found.Add(new GameModuleFile(game, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Gone or locked between the look and the read: sent next time.
            }
        }

        return found;
    }

    /// <summary>Which games have a module beside <paramref name="sprxPath"/>, without reading them.</summary>
    public static IReadOnlyList<GameId> Beside(string sprxPath) =>
        Games.Where(game => File.Exists(BesideSprx(sprxPath, game))).ToArray();

    /// <summary>"RaC1, RaC2 and Deadlocked": the games a send or an install carried, for its one message.</summary>
    public static string Names(IEnumerable<GameId> games)
    {
        var names = games.Select(g => g.DisplayName()).ToArray();
        return names.Length switch
        {
            0 => string.Empty,
            1 => names[0],
            _ => string.Join(", ", names[..^1]) + " and " + names[^1],
        };
    }
}

/// <summary>One game's module, read from beside the client's qwark.sprx.</summary>
public sealed record GameModuleFile(GameId Game, byte[] Bytes)
{
    public string FileName => GameModule.FileName(Game);
}
