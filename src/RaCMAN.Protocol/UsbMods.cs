namespace RaCMAN.Protocol;

/// <summary>
/// How a mod on the console's USB drive stands against the console's own copy: an UsbMod row's
/// <c>state</c>, PROTOCOL.md section 5.7.2 (revision 1.24).
/// </summary>
public enum UsbModState : byte
{
    /// <summary>Only the USB drive has the mod; a load copies it in.</summary>
    Available = 0,

    /// <summary>The console has this very copy.</summary>
    Same = 1,

    /// <summary>The USB copy's version is newer: the mod's next load takes it.</summary>
    Newer = 2,

    /// <summary>The console's copy is newer, and stays.</summary>
    Older = 3,

    /// <summary>The same version, or none to compare, and other contents: the console's copy stays.</summary>
    Conflict = 4,

    /// <summary>The USB copy cannot be taken; <see cref="UsbModEntry.Problem"/> says why.</summary>
    Broken = 5,
}

[Flags]
public enum UsbModFlags : byte
{
    None = 0,

    /// <summary>NEWER while the console's copy is loaded in the game: the update waits for the next load.</summary>
    Waiting = 1 << 0,

    /// <summary>The copy is under <c>/dev_usb00N/mods</c> rather than <c>/dev_usb00N/qwark/mods</c>.</summary>
    RootMods = 1 << 1,
}

/// <summary>Why a BROKEN USB copy cannot be taken.</summary>
public enum UsbModProblem : byte
{
    None = 0,
    PatchFile = 1,
    MissingBin = 2,
    BadName = 3,
    TooBig = 4,
    TooManyFiles = 5,
    ReadFailed = 6,
}

/// <summary>One MOD_USB_LIST row (revision 1.24): 120 bytes, laid out as a MOD_LIST row.</summary>
public sealed record UsbModEntry(
    byte Port,
    UsbModState State,
    UsbModFlags Flags,
    UsbModProblem Problem,
    uint Hash,
    string DirName,
    string Name,
    string Version,
    string Author)
{
    public const int Size = 120;

    public bool Waiting => (Flags & UsbModFlags.Waiting) != 0;

    /// <summary>Where the copy is on the console, as a path: <c>/dev_usb000/qwark/mods/&lt;title&gt;/&lt;dir&gt;</c>.</summary>
    public string PathFor(string title) =>
        $"/dev_usb00{Port}{((Flags & UsbModFlags.RootMods) != 0 ? "/mods" : "/qwark/mods")}/{title}/{DirName}";

    /// <summary>The drive as a user sees it in the XMB: USB000 and so on.</summary>
    public string DriveName => $"USB00{Port}";

    /// <summary>What a BROKEN row's problem means, in a few words.</summary>
    public string ProblemText => Problem switch
    {
        UsbModProblem.PatchFile => "its patch.txt cannot be read or is too big",
        UsbModProblem.MissingBin => "a .bin file its patch.txt names is missing",
        UsbModProblem.BadName => "a .bin file name is one the console will not copy",
        UsbModProblem.TooBig => "a file is over 256 KB, or the mod over 1 MB",
        UsbModProblem.TooManyFiles => "it names more than 32 .bin files",
        UsbModProblem.ReadFailed => "the drive could not be read",
        _ => "it cannot be copied",
    };

    public static UsbModEntry Parse(ReadOnlySpan<byte> entry)
    {
        var r = new SpanReader(entry);
        byte port = r.ReadU8();
        var state = (UsbModState)r.ReadU8();
        var flags = (UsbModFlags)r.ReadU8();
        var problem = (UsbModProblem)r.ReadU8();
        uint hash = r.ReadU32();
        string dirName = r.ReadFixedString(32);
        string name = r.ReadFixedString(32);
        string version = r.ReadFixedString(16);
        string author = r.ReadFixedString(32);
        return new UsbModEntry(port, state, flags, problem, hash, dirName, name, version, author);
    }

    public byte[] ToBytes()
    {
        var buffer = new byte[Size];
        var w = new SpanWriter(buffer);
        w.WriteU8(Port);
        w.WriteU8((byte)State);
        w.WriteU8((byte)Flags);
        w.WriteU8((byte)Problem);
        w.WriteU32(Hash);
        w.WriteFixedString(DirName, 32);
        w.WriteFixedString(Name, 32);
        w.WriteFixedString(Version, 16);
        w.WriteFixedString(Author, 32);
        return buffer;
    }

    public static UsbModEntry[] ParseList(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return Array.Empty<UsbModEntry>();
        var r = new SpanReader(payload);
        int n = r.ReadU8();
        var result = new UsbModEntry[n];
        for (int i = 0; i < n; i++) result[i] = Parse(r.ReadSpan(Size));
        return result;
    }
}

/// <summary>
/// qwark's version rule for mods (PROTOCOL.md 5.7.2): up to four numbers separated by dots, an
/// optional leading v, spaces around; anything else is no version to compare. The client decides
/// with it whether its own copy of a mod is older than the console's or a USB drive's, so it
/// never uploads an older copy over a newer one.
/// </summary>
public static class ModVersion
{
    /// <summary>The order of <paramref name="a"/> and <paramref name="b"/> (-1, 0, 1), or null when either is no version.</summary>
    public static int? Compare(string? a, string? b)
    {
        if (!TryParse(a, out var pa) || !TryParse(b, out var pb)) return null;
        for (int i = 0; i < 4; i++)
        {
            if (pa[i] != pb[i]) return pa[i] > pb[i] ? 1 : -1;
        }

        return 0;
    }

    /// <summary>True when <paramref name="candidate"/> is a version newer than <paramref name="than"/>.</summary>
    public static bool IsNewer(string? candidate, string? than) => Compare(candidate, than) > 0;

    private static bool TryParse(string? text, out uint[] parts)
    {
        parts = new uint[4];
        if (text is null) return false;

        int i = 0;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        if (i < text.Length && (text[i] == 'v' || text[i] == 'V')) i++;

        int n = 0;
        while (true)
        {
            uint value = 0;
            int digits = 0;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9')
            {
                if (value < 100000000u) value = (value * 10u) + (uint)(text[i] - '0');
                i++;
                digits++;
            }

            if (digits == 0 || n >= 4) return false;
            parts[n++] = value;
            if (i >= text.Length || text[i] != '.') break;
            i++;
        }

        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        return i == text.Length;
    }
}
