using System.Globalization;
using System.Text;

namespace RaCMAN.Protocol;

/// <summary>
/// What a mod's folder may be called. MOD_LIST carries a folder name in <c>char dirname[32]</c>, so
/// qwark keeps at most 31 bytes of it: a longer folder is listed under the first 31 bytes of its
/// name, and MOD_LOAD then looks for its files under that shorter name, where they are not. A
/// folder this client makes is therefore never longer than <see cref="MaxBytes"/>, and one that is
/// longer is refused an upload and offered a rename.
/// </summary>
public static class ModFolderName
{
    /// <summary>The longest folder name MOD_LIST can carry whole: 31 bytes of UTF-8 and a NUL.</summary>
    public const int MaxBytes = 31;

    /// <summary>How long a name is on the wire.</summary>
    public static int ByteCount(string name) => Encoding.UTF8.GetByteCount(name);

    /// <summary>Whether qwark can list, load and find the files of a mod in a folder of this name.</summary>
    public static bool Fits(string name) => ByteCount(name) <= MaxBytes;

    /// <summary>
    /// The name as a MOD_LIST row carries it: the first 31 bytes, decoded the way the row is
    /// decoded, so a name cut in the middle of a character compares equal to what the wire gives.
    /// </summary>
    public static string AsListed(string folder)
    {
        var bytes = Encoding.UTF8.GetBytes(folder);
        return bytes.Length <= MaxBytes ? folder : Encoding.UTF8.GetString(bytes, 0, MaxBytes);
    }

    /// <summary>Why a folder of this name cannot be loaded, as a fact about the folder.</summary>
    public static string TooLongFact(string name) =>
        $"its folder name \"{name}\" is {ByteCount(name)} bytes long, and qwark can only use folder names of up to {MaxBytes} bytes.";

    /// <summary>Why a library mod in a folder of this name cannot be uploaded or loaded, and what to do about it.</summary>
    public static string TooLongReason(string name) =>
        TooLongFact(name) + " Rename the folder first (right-click the mod, Rename folder...).";

    /// <summary>
    /// A folder name made out of free text, a ZIP file's name or a mod's <c>#- name</c>: lowercase
    /// letters, digits, <c>-</c> and <c>_</c>, every run of anything else one <c>-</c>, no <c>-</c>
    /// or <c>_</c> at either end, and at most <see cref="MaxBytes"/> long. Accented letters keep
    /// their letter. Empty when nothing usable is left.
    /// </summary>
    public static string Sanitise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var plain = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            plain.Append(c);
        }

        var name = new StringBuilder(plain.Length);
        foreach (char raw in plain.ToString().ToLowerInvariant())
        {
            char c = raw is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' ? raw : '-';
            if (c == '-' && name.Length > 0 && name[^1] == '-') continue;
            name.Append(c);
        }

        return Trim(name.ToString(), MaxBytes);
    }

    /// <summary>
    /// <paramref name="wanted"/> itself when nothing has it, or <c>wanted-2</c>, <c>wanted-3</c> and
    /// so on, each cut short enough that the suffix still fits in <see cref="MaxBytes"/>.
    /// <paramref name="taken"/> says whether a name is already used.
    /// </summary>
    public static string Unique(string wanted, Func<string, bool> taken)
    {
        string stem = Trim(wanted, MaxBytes);
        if (stem.Length == 0) stem = "mod";
        if (!taken(stem)) return stem;

        for (int n = 2; ; n++)
        {
            string suffix = "-" + n.ToString(CultureInfo.InvariantCulture);
            string start = Trim(stem, MaxBytes - suffix.Length);
            string candidate = (start.Length == 0 ? "mod" : start) + suffix;
            if (!taken(candidate)) return candidate;
        }
    }

    /// <summary>At most <paramref name="bytes"/> bytes of an already sanitised name, without a separator left dangling at either end.</summary>
    private static string Trim(string name, int bytes)
    {
        string cut = name;
        while (cut.Length > 0 && ByteCount(cut) > bytes) cut = cut[..^1];
        return cut.Trim('-', '_');
    }
}

/// <summary>An upload refused before anything was written, because qwark could not use the folder name.</summary>
public sealed class ModFolderNameException : Exception
{
    public ModFolderNameException(string message) : base(message) { }
}

/// <summary>
/// One mod folder on the console, or one mod qwark lists: the folder's full name out of DIR_LIST,
/// and the MOD_LIST row qwark made of it. Either can be missing: a folder qwark does not list (it
/// has not rescanned since it appeared), or a row whose folder is gone (a loaded mod keeps its row
/// until it is unloaded and the console rescans).
/// </summary>
public sealed record ConsoleModFolder(string? Folder, ModEntry? Row);

/// <summary>
/// Pairs the folders DIR_LIST finds in <c>/dev_hdd0/qwark/mods/&lt;TITLEID&gt;</c> with the rows
/// MOD_LIST reports. DIR_LIST has each folder's whole name, MOD_LIST only its first 31 bytes, so a
/// row is the folder of exactly its name, or else, when its name is 31 bytes long, a longer folder
/// whose name starts with it: that is a folder qwark cut short.
/// </summary>
public static class ConsoleModMatch
{
    public static IReadOnlyList<ConsoleModFolder> Match(IReadOnlyList<string> folders, IReadOnlyList<ModEntry> rows)
    {
        var unmatched = rows.ToList();
        var paired = new ModEntry?[folders.Count];

        // Exact names first, so a folder that fits is never taken for a longer one's prefix.
        for (int i = 0; i < folders.Count; i++)
        {
            int at = unmatched.FindIndex(row => string.Equals(row.DirName, folders[i], StringComparison.Ordinal));
            if (at < 0) continue;

            paired[i] = unmatched[at];
            unmatched.RemoveAt(at);
        }

        // Then the folders qwark cut short, each to the first row left that carries its first 31 bytes.
        for (int i = 0; i < folders.Count; i++)
        {
            if (paired[i] is not null || ModFolderName.Fits(folders[i])) continue;

            string listed = ModFolderName.AsListed(folders[i]);
            int at = unmatched.FindIndex(row => string.Equals(row.DirName, listed, StringComparison.Ordinal));
            if (at < 0) continue;

            paired[i] = unmatched[at];
            unmatched.RemoveAt(at);
        }

        var result = new List<ConsoleModFolder>(folders.Count + unmatched.Count);
        for (int i = 0; i < folders.Count; i++) result.Add(new ConsoleModFolder(folders[i], paired[i]));
        foreach (var row in unmatched) result.Add(new ConsoleModFolder(null, row));
        return result;
    }

    /// <summary>
    /// The folder names out of a DIR_LIST reply for a title's mod folder: directories only, without
    /// "." and "..", which the console's own listing includes.
    /// </summary>
    public static IReadOnlyList<string> FoldersIn(IEnumerable<DirEntry> entries) =>
        entries.Where(entry => entry.IsDirectory && entry.Name is not ("." or "..") && entry.Name.Length > 0)
            .Select(entry => entry.Name)
            .ToList();
}
