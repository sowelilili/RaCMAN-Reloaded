using System.Buffers.Binary;

namespace RaCMAN.Protocol;

/// <summary>
/// The console menu's file. Since build 55 the four renderers qwark writes into a game are not
/// inside qwark.sprx any more but in <c>/dev_hdd0/qwark/ui.pak</c>, which frees a page of the
/// VSH's memory. A release ships it beside qwark.sprx, and every way this client puts the module on
/// a console puts the pak there too. Without a pak built with the running module the module works
/// as before, and only the Trainer menu stays out of the games; the console says so in
/// <see cref="SessionFlags.UiPakBad"/>.
/// <para>
/// The client reads the 32-byte header and nothing else: the magic and the build it was made
/// with, so that a pak which could not fix anything is not sent. Whether a pak is good is the
/// module's question, and it answers it with that flag.
/// </para>
/// </summary>
public static class UiPak
{
    public const string FileName = "ui.pak";

    /// <summary>qwark's own folder on the console. The module creates it at start; FTP before then may not find it.</summary>
    public const string ConsoleDirectory = "/dev_hdd0/qwark";

    /// <summary>Where the module reads the pak from.</summary>
    public const string ConsolePath = ConsoleDirectory + "/" + FileName;

    /// <summary>
    /// Where the standalone update leaves the next build's pak. The module that is running keeps
    /// its own pak until the restart, and the new one moves this into place when it starts.
    /// </summary>
    public const string StagedPath = ConsolePath + ".new";

    /// <summary>The header's length: the magic, the format, the header size and the build, big-endian.</summary>
    public const int HeaderSize = 32;

    /// <summary>"QPAK" in ASCII, the first four bytes of every pak.</summary>
    public static ReadOnlySpan<byte> Magic => "QPAK"u8;

    /// <summary>The one sentence for a client that has no pak to send beside its qwark.sprx.</summary>
    public const string MissingNote = "No ui.pak beside qwark.sprx: the Trainer menu will not load in games";

    /// <summary>The one sentence for a pak the console's FTP server refused; the module is sent all the same.</summary>
    public const string SendFailedNote = "Could not send ui.pak to the console: the Trainer menu will not load in games";

    /// <summary>
    /// The client's own copy: beside the qwark.sprx it sends, whichever path that resolved to.
    /// The release puts the two side by side, so the settings' one path finds both.
    /// </summary>
    public static string BesideSprx(string sprxPath) =>
        Path.Combine(Path.GetDirectoryName(sprxPath) ?? string.Empty, FileName);

    /// <summary>
    /// The qwark build a pak's header names, or false for bytes that are not a pak at all: shorter
    /// than the header, or without the magic. Nothing past the header is looked at.
    /// </summary>
    public static bool TryReadBuild(ReadOnlySpan<byte> pak, out int build)
    {
        build = 0;
        if (pak.Length < HeaderSize || !pak[..Magic.Length].SequenceEqual(Magic)) return false;

        uint value = BinaryPrimitives.ReadUInt32BigEndian(pak[8..12]);
        if (value > int.MaxValue) return false;

        build = (int)value;
        return true;
    }
}
