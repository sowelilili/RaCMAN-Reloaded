using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>What DIR_LIST found in the console's mod folder for one title: the folders' whole names.</summary>
public sealed record ConsoleModFolders(string Title, IReadOnlyList<string> Folders);

/// <summary>How a status cell is coloured.</summary>
public enum ModTone
{
    Quiet,
    Good,
    Bad,
}

/// <summary>
/// One row of the Mods panel: a mod of the library with what the console has of it, or a mod only
/// the console has. <see cref="Console"/> is its MOD_LIST row and <see cref="ConsoleFolder"/> the
/// whole name of its folder on the console, as DIR_LIST gives it; either can be missing.
/// <see cref="FoldersListed"/> says whether DIR_LIST has been read at all, and so whether a missing
/// folder is known to be missing.
/// </summary>
public sealed record ModRow(LocalMod? Local, ModEntry? Console, string? ConsoleFolder, bool FoldersListed)
{
    /// <summary>True for a mod the console has and no library on this PC does.</summary>
    public bool ConsoleOnly => Local is null;

    /// <summary>What the panel selects the row by: a library mod by its folder, a console-only one by the console's.</summary>
    public string Key => Local is { } local ? local.DirName : "console:" + FolderName;

    /// <summary>
    /// The folder the mod is in, or is uploaded to: the library's folder name, or for a console-only
    /// mod the console's, whole, or when DIR_LIST has not got it the name MOD_LIST carries.
    /// </summary>
    public string FolderName => Local?.DirName ?? ConsoleFolder ?? Console?.DirName ?? string.Empty;

    /// <summary>The name MOD_LIST knows the mod by, which MOD_UNLOAD and MOD_SET_AUTO take, or null when qwark does not list it.</summary>
    public string? ListedName => Console?.DirName;

    /// <summary>True when the folder name is longer than qwark can use.</summary>
    public bool FolderNameTooLong => !ModFolderName.Fits(FolderName);

    /// <summary>
    /// The name shown: the library's, or what MOD_LIST says, or the folder's whole name when qwark
    /// could not parse the mod or found no <c>#- name</c> line (it names such a mod after the first
    /// 31 bytes of its folder).
    /// </summary>
    public string Name
    {
        get
        {
            if (Local is { } local) return local.Name;
            if (Console is { ParseError: false } row && row.Name.Length > 0 && row.Name != row.DirName) return row.Name;
            return FolderName;
        }
    }

    public string Version => Local?.Version ?? (Console is { ParseError: false } row ? row.Version : string.Empty);

    public string Author => Local?.Author ?? (Console is { ParseError: false } row ? row.Author : string.Empty);

    public bool Loaded => Console?.Loaded ?? false;
}

/// <summary>What a row offers, and why an offer is greyed out.</summary>
public sealed record ModRowActions(
    bool Load,
    bool LoadEnabled,
    string LoadReason,
    bool Unload,
    bool AutoEnabled,
    bool UninstallEnabled,
    string UninstallReason,
    bool Rename);

/// <summary>
/// The Mods panel's rows, worked out from the library, MOD_LIST and DIR_LIST with no ImGui, so the
/// matching and what each row says and offers can be tested on their own.
/// </summary>
public static class ModTable
{
    /// <summary>
    /// The library's mods in the library's order, each paired with the console's copy of it, then
    /// every mod the console has that no library mod claimed, by name. A library mod's console copy
    /// is the folder of its own name (DIR_LIST, case-exact first); without a folder to go by, the
    /// MOD_LIST row of its name, or, for a folder name too long for MOD_LIST, the row carrying its
    /// first 31 bytes. <paramref name="folders"/> is null until DIR_LIST has been read, and then
    /// the console's mods are MOD_LIST's rows alone.
    /// </summary>
    public static IReadOnlyList<ModRow> Build(IReadOnlyList<LocalMod> locals, IReadOnlyList<ModEntry> rows, IReadOnlyList<string>? folders)
    {
        bool listed = folders is not null;
        var pairs = listed
            ? ConsoleModMatch.Match(folders!, rows)
            : rows.Select(row => new ConsoleModFolder(null, row)).ToList();

        var claimed = new bool[pairs.Count];
        var owner = new int[locals.Count];
        Array.Fill(owner, -1);

        // Whole names first, case-exact and then not (the library may be on a file system that
        // ignores case), so a mod whose name fits is never paired with a longer one's copy; then
        // the names MOD_LIST cut short, for the library mods whose folder names are too long.
        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
        {
            for (int i = 0; i < locals.Count; i++)
            {
                if (owner[i] >= 0) continue;
                string dirName = locals[i].DirName;
                owner[i] = Claim(pairs, claimed, pair => string.Equals(pair.Folder ?? pair.Row?.DirName, dirName, comparison));
            }
        }

        for (int i = 0; i < locals.Count; i++)
        {
            if (owner[i] >= 0 || !locals[i].FolderNameTooLong) continue;

            string listedName = ModFolderName.AsListed(locals[i].DirName);
            owner[i] = Claim(pairs, claimed, pair => pair.Folder is null && pair.Row?.DirName == listedName);
        }

        var result = new List<ModRow>(locals.Count + pairs.Count);
        for (int i = 0; i < locals.Count; i++)
        {
            var pair = owner[i] >= 0 ? pairs[owner[i]] : null;
            result.Add(new ModRow(locals[i], pair?.Row, pair?.Folder, listed));
        }

        var consoleOnly = new List<ModRow>();
        for (int i = 0; i < pairs.Count; i++)
        {
            if (!claimed[i]) consoleOnly.Add(new ModRow(null, pairs[i].Row, pairs[i].Folder, listed));
        }

        result.AddRange(consoleOnly
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.FolderName, StringComparer.Ordinal));
        return result;
    }

    /// <summary>The first unclaimed pair that <paramref name="matches"/>, claimed; -1 when none does.</summary>
    private static int Claim(IReadOnlyList<ConsoleModFolder> pairs, bool[] claimed, Func<ConsoleModFolder, bool> matches)
    {
        for (int i = 0; i < pairs.Count; i++)
        {
            if (claimed[i] || !matches(pairs[i])) continue;

            claimed[i] = true;
            return i;
        }

        return -1;
    }

    /// <summary>
    /// The PS3 table's Console cell. A library mod: "folder name too long" when qwark cannot use its
    /// folder, "not uploaded" with no console copy, else what MOD_LIST says of the copy, "stale"
    /// when the copy is not the library's. A console-only mod: <see cref="ConsoleOnlyStatus"/>.
    /// </summary>
    public static (string Text, ModTone Tone) ConsoleStatus(ModRow row, bool withAuto = false)
    {
        if (row.Local is not { } local) return ConsoleOnlyStatus(row, withAuto);
        if (local.FolderNameTooLong) return ("folder name too long", ModTone.Bad);
        if (row.Console is not { } remote) return ("not uploaded", ModTone.Quiet);

        var parts = FlagWords(remote, withAuto);
        if (remote.Hash != local.Hash) parts.Add("stale");

        var tone = remote.Loaded ? ModTone.Good : remote.ParseError ? ModTone.Bad : ModTone.Quiet;
        return (parts.Count == 0 ? "ready" : string.Join(", ", parts), tone);
    }

    /// <summary>
    /// What a console-only row says: "Console only", then why qwark cannot use it when it cannot
    /// (a folder name too long, a patch.txt it could not parse), what MOD_LIST says of it, and
    /// whether qwark does not list the folder yet or lists a mod whose folder is gone.
    /// <paramref name="withAuto"/> adds the auto flag, for the RPCS3 table, which has no Auto column.
    /// </summary>
    public static (string Text, ModTone Tone) ConsoleOnlyStatus(ModRow row, bool withAuto = false)
    {
        var parts = new List<string> { "Console only" };
        if (row.FolderNameTooLong) parts.Add("folder name too long");

        if (row.Console is { } remote) parts.AddRange(FlagWords(remote, withAuto));
        else parts.Add("not listed by qwark");

        if (row.Console is not null && row.FoldersListed && row.ConsoleFolder is null) parts.Add("folder gone");

        var tone = row.FolderNameTooLong || row.Console is { ParseError: true } ? ModTone.Bad
            : row.Loaded ? ModTone.Good
            : ModTone.Quiet;
        return (string.Join(", ", parts), tone);
    }

    private static List<string> FlagWords(ModEntry remote, bool withAuto)
    {
        var parts = new List<string>();
        if (remote.Loaded) parts.Add("loaded");
        if (withAuto && remote.Auto) parts.Add("auto");
        if (remote.Previous) parts.Add("previous");
        if (remote.NeedsLua) parts.Add("needs Lua");
        if (remote.ParseError) parts.Add("parse error");
        return parts;
    }

    /// <summary>
    /// What a row offers. On a console: Load while it is not loaded, and only for a folder name qwark
    /// can use and, for a console-only mod, one qwark lists and could parse; Unload while it is
    /// loaded; the Auto box for a mod qwark lists. Under RPCS3 there is no Load, Unload or Auto:
    /// RPCS3 loads the library's mods through their Enabled box, and a console-only one can only be
    /// uninstalled. Uninstall wants the console answering and its mod folder read, since that is
    /// what says which folder the uninstall deletes. Rename is for a user mod whose folder name is
    /// too long.
    /// </summary>
    public static ModRowActions ActionsFor(ModRow row, bool rpcs3, bool connected, bool busy)
    {
        bool loaded = row.Loaded;

        string loadReason = !connected ? "Connect to the console first."
            : row.FolderNameTooLong ? "It cannot be loaded: " + (row.ConsoleOnly
                ? ModFolderName.TooLongFact(row.FolderName)
                : ModFolderName.TooLongReason(row.FolderName))
            : row.ConsoleOnly && row.Console is null ? "qwark does not list this folder. Press Rescan console folder first."
            : row.ConsoleOnly && row.Console!.ParseError ? "qwark could not parse this mod's patch.txt, or a .bin it names is missing."
            : string.Empty;

        string uninstallReason = !connected ? (rpcs3 ? "Connect to qwark-rpcs3 first." : "Connect to the console first.")
            : busy ? "Wait for the game to finish starting or stopping, and for any change under way to finish."
            : !row.FoldersListed ? "Reading the console's mod folder..."
            : string.Empty;

        return new ModRowActions(
            Load: !rpcs3 && !loaded,
            LoadEnabled: !rpcs3 && !loaded && loadReason.Length == 0,
            LoadReason: loadReason,
            Unload: !rpcs3 && loaded,
            AutoEnabled: !rpcs3 && connected && row.Console is not null,
            UninstallEnabled: uninstallReason.Length == 0,
            UninstallReason: uninstallReason,
            Rename: row.Local is { Shipped: false, FolderNameTooLong: true });
    }
}
