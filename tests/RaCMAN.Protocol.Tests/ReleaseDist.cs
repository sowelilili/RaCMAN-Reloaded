using System.Buffers.Binary;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The payload a qwark release dist\ carries since build 57, for the packaging scripts' tests:
/// every game module (rac1.sprx to rac4.sprx and tod.sprx, each a signed-looking SELF) and a
/// ui.pak whose header and table of contents are a format 2 pak's (qwark's PROTOCOL.md 9.10): the
/// four renderers, the four savefile helpers and, unless told otherwise, Tools of Destruction's Lua
/// helper and its three chunks (kinds 3 to 6, game 5), which is all check-qwark-build.ps1 reads.
/// </summary>
internal static class ReleaseDist
{
    /// <summary>A module's bytes: the SELF magic and filler.</summary>
    public static byte[] Module(byte seed, int length = 6000)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 13 + seed);
        GameModule.SelfMagic.CopyTo(bytes);
        return bytes;
    }

    /// <summary>A ui.pak's header and table of contents, for <paramref name="build"/>, with or without ToD's entries.</summary>
    public static byte[] Pak(byte build, bool tod = true)
    {
        var entries = new List<(byte Game, byte Kind)>();
        for (byte g = 1; g <= 4; g++) entries.Add((g, 1));
        for (byte g = 1; g <= 4; g++) entries.Add((g, 2));
        if (tod) for (byte k = 3; k <= 6; k++) entries.Add((5, k));

        var pak = new byte[32 + 32 * entries.Count + 64];
        "QPAK"u8.CopyTo(pak);
        BinaryPrimitives.WriteUInt16BigEndian(pak.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16BigEndian(pak.AsSpan(6), 32);
        BinaryPrimitives.WriteUInt32BigEndian(pak.AsSpan(8), build);
        pak[20] = (byte)entries.Count;
        for (int i = 0; i < entries.Count; i++)
        {
            pak[32 + 32 * i] = entries[i].Game;
            pak[32 + 32 * i + 1] = entries[i].Kind;
        }

        return pak;
    }

    /// <summary>
    /// Writes the game modules named in <paramref name="games"/> (all five when null) and a ui.pak
    /// (<paramref name="pak"/> false: none) into <paramref name="dist"/>.
    /// </summary>
    public static void Write(string dist, byte build, IEnumerable<GameId>? games = null, bool pak = true, bool tod = true)
    {
        foreach (var game in games ?? GameModule.Games)
        {
            File.WriteAllBytes(Path.Combine(dist, GameModule.FileName(game)), Module((byte)game));
        }

        if (pak) File.WriteAllBytes(Path.Combine(dist, UiPak.FileName), Pak(build, tod));
    }
}
