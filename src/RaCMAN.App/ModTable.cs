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

    /// <summary>Something to act on that is not wrong: an update waiting on the console's USB drive.</summary>
    Update,
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
    /// <summary>
    /// The copy on the console's USB drive (revision 1.24), MOD_USB_LIST's row of the same folder
    /// name, or null when no drive has the mod or the console does not read its drives.
    /// </summary>
    public UsbModEntry? Usb { get; init; }

    /// <summary>True for a mod only the console's USB drive has: not installed on the console, not in a library here.</summary>
    public bool UsbOnly => Local is null && Console is null && ConsoleFolder is null && Usb is not null;

    /// <summary>True for a mod the console has and no library on this PC does.</summary>
    public bool ConsoleOnly => Local is null && !UsbOnly;

    /// <summary>What the panel selects the row by: a library mod by its folder, a console-only one by the console's.</summary>
    public string Key => Local is { } local ? local.DirName : UsbOnly ? "usb:" + FolderName : "console:" + FolderName;

    /// <summary>
    /// The folder the mod is in, or is uploaded to: the library's folder name, or for a console-only
    /// mod the console's, whole, or when DIR_LIST has not got it the name MOD_LIST carries; for a
    /// mod only the USB drive has, its folder there.
    /// </summary>
    public string FolderName => Local?.DirName ?? ConsoleFolder ?? Console?.DirName ?? Usb?.DirName ?? string.Empty;

    /// <summary>
    /// The name MOD_SET_AUTO takes for the row: the console's, or for a mod only the USB drive has,
    /// its folder there, which the console copies in as it sets the flag. Null when neither.
    /// </summary>
    public string? AutoName => Console?.DirName ?? (UsbOnly ? Usb!.DirName : null);

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
            if (UsbOnly && Usb!.Name.Length > 0) return Usb.Name;
            return FolderName;
        }
    }

    public string Version => Local?.Version ?? (Console is { ParseError: false } row ? row.Version
        : UsbOnly ? Usb!.Version : string.Empty);

    public string Author => Local?.Author ?? (Console is { ParseError: false } row ? row.Author
        : UsbOnly ? Usb!.Author : string.Empty);

    public bool Loaded => Console?.Loaded ?? false;

    /// <summary>
    /// Qwark revision 1.30: what the mod's <c>#- legal:</c> line says. The library's copy when this
    /// PC has one (it is what Load uploads), else what MOD_LIST says of the console's; null for a mod
    /// only a USB drive has, whose patch.txt nobody here has read.
    /// </summary>
    public ModLegality? Legality => Local?.Legality ?? Console?.Legality;
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
    public static IReadOnlyList<ModRow> Build(IReadOnlyList<LocalMod> locals, IReadOnlyList<ModEntry> rows, IReadOnlyList<string>? folders) =>
        Build(locals, rows, folders, null);

    /// <summary>
    /// The same, with the mods on the console's USB drives (revision 1.24): each goes onto the row
    /// of its folder name, library or console, and one no row has becomes a row of its own after the
    /// console-only ones, ordered the same way.
    /// </summary>
    public static IReadOnlyList<ModRow> Build(IReadOnlyList<LocalMod> locals, IReadOnlyList<ModEntry> rows,
        IReadOnlyList<string>? folders, IReadOnlyList<UsbModEntry>? usb)
    {
        var table = BuildConsole(locals, rows, folders);
        if (usb is null || usb.Count == 0) return table;

        var result = table.ToList();
        var usbOnly = new List<ModRow>();
        foreach (var entry in usb)
        {
            int at = result.FindIndex(row => string.Equals(row.FolderName, entry.DirName, StringComparison.Ordinal) && row.Usb is null);
            if (at >= 0) result[at] = result[at] with { Usb = entry };
            else usbOnly.Add(new ModRow(null, null, null, folders is not null) { Usb = entry });
        }

        result.AddRange(usbOnly
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.FolderName, StringComparer.Ordinal));
        return result;
    }

    private static IReadOnlyList<ModRow> BuildConsole(IReadOnlyList<LocalMod> locals, IReadOnlyList<ModEntry> rows, IReadOnlyList<string>? folders)
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
        if (row.UsbOnly) return UsbOnlyStatus(row);
        if (row.Local is not { } local) return ConsoleOnlyStatus(row, withAuto);
        if (local.FolderNameTooLong) return ("folder name too long", ModTone.Bad);
        if (row.Console is not { } remote) return WithUsb(row, new List<string> { "not uploaded" }, ModTone.Quiet);

        var parts = FlagWords(remote, withAuto);

        // Revision 1.24: a console copy newer than the library's came off a USB drive (or another
        // PC), and Load keeps it rather than uploading the older one over it.
        // When the drive's copy is the console's (USB state SAME) that is the whole story, so the
        // row is just ready: the library is behind both and Load keeps what the console has.
        if (remote.Hash != local.Hash)
        {
            bool newer = !remote.ParseError && ModVersion.IsNewer(remote.Version, local.Version);
            if (!newer) parts.Add("stale");
            else if (row.Usb is not { State: UsbModState.Same }) parts.Add("newer on console");
        }

        var tone = remote.Loaded ? ModTone.Good : remote.ParseError ? ModTone.Bad : ModTone.Quiet;
        if (parts.Count == 0) parts.Add("ready");
        return WithUsb(row, parts, tone);
    }

    /// <summary>
    /// What a row's USB copy adds to its status (revision 1.24). The cell is narrow, so it
    /// says as little as it can and ModsPanel.UsbTooltip says the rest (versions, port, path, what
    /// the console does): an update is "update ready" (just "update" after another word, and while
    /// the mod is loaded it waits for the next load, which the tooltip says); a copy only the drive
    /// has, for a library mod not on the console yet, is "on USB"; one the console cannot copy is
    /// "USB bad". A copy that is the console's, an older one, or one with the same version and other
    /// contents (a warning on the tooltip, the console keeps its own) adds nothing. An update
    /// colours a row that is not wrong otherwise.
    /// </summary>
    private static (string Text, ModTone Tone) WithUsb(ModRow row, List<string> parts, ModTone tone)
    {
        switch (row.Usb?.State)
        {
            case UsbModState.Newer:
                if (parts.Count == 1 && parts[0] == "ready") parts[0] = "update ready";
                else parts.Add(parts.Count == 1 && parts[0] != "loaded" ? "update ready" : "update");
                if (tone == ModTone.Quiet) tone = ModTone.Update;
                break;
            case UsbModState.Available:
                if (parts.Count == 1 && parts[0] == "not uploaded") parts[0] = "on USB";
                else parts.Add("on USB");
                break;
            case UsbModState.Broken:
                parts.Add("USB bad");
                break;
        }

        return (string.Join(", ", parts), tone);
    }

    /// <summary>What a mod only the console's USB drive has says: where it is, and whether the console can copy it.</summary>
    public static (string Text, ModTone Tone) UsbOnlyStatus(ModRow row)
    {
        var usb = row.Usb!;
        return usb.State == UsbModState.Broken
            ? ($"On {usb.DriveName}, unusable", ModTone.Bad)
            : ($"On {usb.DriveName}", ModTone.Quiet);
    }

    /// <summary>
    /// Whether Load leaves the library's copy of a mod on this PC and loads the console's instead
    /// (revision 1.24): when the console's USB drive or the console itself has a newer version of it
    /// by qwark's rule, which the console then takes or keeps. Uploading the library's older copy
    /// would only be replaced again, or overwrite the newer one.
    /// </summary>
    public static bool LoadKeepsNewerConsoleCopy(ModRow row)
    {
        if (row.Local is not { } local) return false;
        if (row.Usb is { State: not UsbModState.Broken } usb && ModVersion.IsNewer(usb.Version, local.Version)) return true;
        return row.Console is { ParseError: false } remote && ModVersion.IsNewer(remote.Version, local.Version);
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
        return WithUsb(row, parts, tone);
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
    /// The Legal column (qwark revision 1.30): "yes", "no" or "?" for a mod whose patch.txt says
    /// <c>#- legal: true</c>, <c>false</c> or neither, and "-" for one nobody here has read. The
    /// tooltip says what the console does about it as a run starts.
    /// </summary>
    public static (string Text, ModTone Tone, string Tooltip) LegalityCell(ModRow row) => row.Legality switch
    {
        ModLegality.Legal => ("yes", ModTone.Good,
            "Run-legal: its author says it is allowed in speedruns."),
        ModLegality.Illegal => ("no", ModTone.Bad,
            "Not run-legal: its author says it is not allowed in speedruns. Your console warns about it when a run starts with it loaded."),
        ModLegality.Unknown => ("?", ModTone.Quiet,
            "Legality unknown: its author hasn't said whether it is allowed in speedruns. Your console mentions it once when a run starts with it loaded."),
        _ => ("-", ModTone.Quiet, "Only on your console's USB drive, so its legality isn't known here."),
    };

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

        string loadReason = !connected ? "Connect to your console first."
            : row.FolderNameTooLong ? "It cannot be loaded: " + (row.ConsoleOnly
                ? ModFolderName.TooLongFact(row.FolderName)
                : ModFolderName.TooLongReason(row.FolderName))
            : row.UsbOnly && row.Usb!.State == UsbModState.Broken ? $"Your console cannot copy it from {row.Usb.DriveName}: {row.Usb.ProblemText}."
            : row.ConsoleOnly && row.Console is null ? "qwark does not list this folder. Press Rescan console folder first."
            : row.ConsoleOnly && row.Console!.ParseError ? "qwark could not parse this mod's patch.txt, or a .bin it names is missing."
            : string.Empty;

        string uninstallReason = row.UsbOnly ? $"It is only on your console's USB drive ({row.Usb!.DriveName}); qwark never deletes anything there."
            : !connected ? (rpcs3 ? "Connect to qwark-rpcs3 first." : "Connect to your console first.")
            : busy ? "Wait for the game to finish starting or stopping, and for any change under way to finish."
            : !row.FoldersListed ? "Reading your console's mod folder..."
            : string.Empty;

        return new ModRowActions(
            Load: !rpcs3 && !loaded,
            LoadEnabled: !rpcs3 && !loaded && loadReason.Length == 0,
            LoadReason: loadReason,
            Unload: !rpcs3 && loaded,
            AutoEnabled: !rpcs3 && connected
                         && (row.Console is not null || (row.UsbOnly && row.Usb!.State != UsbModState.Broken)),
            UninstallEnabled: uninstallReason.Length == 0,
            UninstallReason: uninstallReason,
            Rename: row.Local is { Shipped: false, FolderNameTooLong: true });
    }
}
