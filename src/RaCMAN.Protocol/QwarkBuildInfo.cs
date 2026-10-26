using System.Globalization;

namespace RaCMAN.Protocol;

/// <summary>
/// What a built qwark.sprx is, from the <c>qwark.build</c> file qwark's <c>make</c> writes beside
/// it and a release carries beside it (revision 1.24, PROTOCOL.md 3.1.1): its build number, whether
/// it is a test build, and its build ID, the CRC of its sources. Every test build before a release
/// reports the same build number, so this is how the client knows which of them it ships, and
/// <see cref="QwarkClient.IsStaleModule"/> holds the console's module against it.
/// <para>
/// The file is three lines of text after a comment: <c>build 56</c>, <c>dev 1</c> (or 0) and
/// <c>id 0x1A2B3C4D</c>. Anything else in it is ignored; a file without all three is no file.
/// </para>
/// </summary>
public sealed record QwarkBuildInfo(byte Build, bool Dev, uint Id)
{
    public const string FileName = "qwark.build";

    /// <summary>The client's own copy: beside the qwark.sprx it sends, as ui.pak is.</summary>
    public static string BesideSprx(string sprxPath) =>
        Path.Combine(Path.GetDirectoryName(sprxPath) ?? string.Empty, FileName);

    /// <summary>The file's three lines, or null when one is missing or not a number.</summary>
    public static QwarkBuildInfo? Parse(string? text)
    {
        int? build = null;
        int? dev = null;
        uint? id = null;

        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            int space = line.IndexOf(' ');
            if (space <= 0) continue;
            string key = line[..space];
            string value = line[(space + 1)..].Trim();

            switch (key)
            {
                case "build" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int b)
                                  && b is >= 0 and <= byte.MaxValue:
                    build = b;
                    break;
                case "dev" when value is "0" or "1":
                    dev = value == "1" ? 1 : 0;
                    break;
                case "id" when value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                               && uint.TryParse(value[2..], NumberStyles.AllowHexSpecifier,
                                   CultureInfo.InvariantCulture, out uint i):
                    id = i;
                    break;
            }
        }

        return build is { } bb && dev is { } dd && id is { } ii ? new QwarkBuildInfo((byte)bb, dd == 1, ii) : null;
    }

    /// <summary>
    /// The description beside <paramref name="sprxPath"/>, or null when there is none or it cannot
    /// be read: a module from a source tree before revision 1.24, or a release from before it.
    /// </summary>
    public static QwarkBuildInfo? BesideOrNull(string sprxPath)
    {
        try
        {
            string path = BesideSprx(sprxPath);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>"build 56, test build 0x1A2B3C4D" or "build 56, release": for the debug line and the warning.</summary>
    public string Describe() => Dev ? $"build {Build}, test build 0x{Id:X8}" : $"build {Build}, release";
}
