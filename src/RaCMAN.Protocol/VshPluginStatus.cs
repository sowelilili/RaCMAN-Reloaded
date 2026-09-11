using System.Text.RegularExpressions;

namespace RaCMAN.Protocol;

/// <summary>What webMAN's VSH plugin page was able to say about a plugin file.</summary>
public enum VshPluginState
{
    /// <summary>The page could not be read, or is not one this client understands.</summary>
    Unknown,

    /// <summary>The page was read and no slot holds the file.</summary>
    NotLoaded,

    /// <summary>A slot holds it.</summary>
    Loaded,
}

/// <summary>
/// One answer from webMAN's <c>vshplugin.ps3mapi</c> page: whether a plugin file is in a VSH slot,
/// which slot, and the path the console loaded it from.
/// <para>
/// The page is parsed rather than searched for the file name. webMAN prints, beside the table of
/// slots, a hidden <c>&lt;datalist&gt;</c> of every <c>.sprx</c> it can find on the console, so a
/// qwark.sprx merely sitting in <c>/dev_hdd0/plugins</c> appears in the page whether or not
/// anything has loaded it. A client that searched the whole page for the name therefore reported
/// a module that was never loaded (and, worse, one that was loaded and had died) as running.
/// </para>
/// <para>
/// What is load-bearing in that page is the form on each row. A slot with a plugin in it carries
/// an <c>unload_slot</c> hidden input and prints the plugin's name and path; an empty slot carries
/// a <c>load_slot</c> input and a box to type a path into. So a file is loaded when it is named on
/// a row that offers to unload it, and the page is only understood at all when at least one row
/// offers one or the other.
/// </para>
/// </summary>
public readonly record struct VshPluginStatus(VshPluginState State, int Slot, string Path)
{
    /// <summary>The page said nothing useful: not read, not answered, not understood.</summary>
    public static readonly VshPluginStatus Unknown = new(VshPluginState.Unknown, -1, string.Empty);

    /// <summary>The page was understood and no slot holds the file.</summary>
    public static readonly VshPluginStatus NotLoaded = new(VshPluginState.NotLoaded, -1, string.Empty);

    public bool IsLoaded => State == VshPluginState.Loaded;

    /// <summary>True when the page was understood, whichever way it answered.</summary>
    public bool IsKnown => State != VshPluginState.Unknown;

    /// <summary>The slot's number where there is one, for a message that names it.</summary>
    public string SlotText => Slot >= 0 ? Slot.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";

    /// <summary>Pulls the slot number out of the row's hidden input, whatever order the attributes come in.</summary>
    private static readonly Regex UnloadSlot = new(
        "name=\"unload_slot\"[^>]*?value=\"(?<slot>\\d+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads one plugin file's state out of the page. <paramref name="sprxName"/> is matched
    /// against the whole row, so the path column finds it wherever the console loaded it from.
    /// </summary>
    public static VshPluginStatus Parse(string? page, string sprxName)
    {
        if (string.IsNullOrEmpty(page) || string.IsNullOrEmpty(sprxName)) return Unknown;

        var table = WithoutFileLists(page);

        bool understood = false;

        foreach (var row in Rows(table))
        {
            // Either form proves this is the plugin page: an empty slot offers to load into it,
            // a full one offers to unload it.
            if (row.Contains("load_slot", StringComparison.OrdinalIgnoreCase)
                || row.Contains("unload_slot", StringComparison.OrdinalIgnoreCase))
            {
                understood = true;
            }

            var match = UnloadSlot.Match(row);
            if (!match.Success) continue;
            if (!row.Contains(sprxName, StringComparison.OrdinalIgnoreCase)) continue;

            int slot = int.TryParse(match.Groups["slot"].Value, out int parsed) ? parsed : -1;
            return new VshPluginStatus(VshPluginState.Loaded, slot, PathAround(row, sprxName));
        }

        // A page with no slot form on it at all is not the plugin page: an error page, a login
        // page, or a webMAN whose shape this client does not know. Saying "not loaded" there
        // would have the caller upload and load a module that may well be running already.
        return understood ? NotLoaded : Unknown;
    }

    /// <summary>
    /// The page without the parts that name files the console merely *has*: the hidden datalist of
    /// every .sprx on the drives, and any select of the same. Everything left names a slot.
    /// </summary>
    private static string WithoutFileLists(string page)
    {
        var text = Cut(page, "<datalist", "</datalist>");
        return Cut(text, "<select", "</select>");
    }

    private static string Cut(string text, string open, string close)
    {
        while (true)
        {
            int start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return text;

            int end = text.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return text[..start];

            text = text.Remove(start, end - start + close.Length);
        }
    }

    /// <summary>The page's table rows. Split rather than matched, so odd attributes cannot throw it.</summary>
    private static IEnumerable<string> Rows(string table)
    {
        var parts = table.Split("<tr", StringSplitOptions.None);
        for (int i = 1; i < parts.Length; i++) yield return parts[i];
    }

    /// <summary>
    /// The path cell the file name sits in: back to the tag that opens the cell, forward to the one
    /// that closes it. Empty when the name turns up somewhere without a cell around it.
    /// </summary>
    private static string PathAround(string row, string sprxName)
    {
        int at = row.IndexOf(sprxName, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return string.Empty;

        int open = row.LastIndexOf('>', at);
        int close = row.IndexOf('<', at);
        if (open < 0 || close < 0 || close <= open) return string.Empty;

        return row[(open + 1)..close].Trim();
    }
}
