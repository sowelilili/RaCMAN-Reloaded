using System.Globalization;
using System.Text.RegularExpressions;
using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>
/// The old RaCMAN's <c>config.txt</c>, the flat "key = value" file it kept beside racman.exe.
/// This client reads it once, on the user's request, to carry over what still means something
/// here: the console's IP, the five controller combos, the per-title auto-apply mod lists, and the
/// chargeboot colour picker's saved slots.
/// <para>
/// What is not carried over, and why: the saved positions (only Deadlocked and ToD ever wrote
/// them, as opaque snapshots, and positions now live on the console), the Lua "run script" combo
/// (no Lua), and the ToS flag.
/// </para>
/// </summary>
public sealed class LegacyConfig
{
    /// <summary>One combo as the old client actually used it: a missing or zero entry meant its built-in default.</summary>
    public sealed record ComboImport(ComboAction Action, uint Mask, bool WasDefault);

    /// <summary>The old keys, in the old client's own order, with the defaults ConfigureCombos.GetCombos fell back to.</summary>
    private static readonly (ComboAction Action, string Key, uint Default)[] ComboKeys =
    {
        (ComboAction.SavePosition, "savePosCombo", 0xB),
        (ComboAction.LoadPosition, "loadPosCombo", 0x7),
        (ComboAction.Die, "dieCombo", 0x5),
        (ComboAction.LoadPlanet, "loadPlanetCombo", 0x600),
        (ComboAction.LoadSetAsideFile, "loadSetAsideCombo", 0x100),
    };

    private const string ModAutoPrefix = "autoApplyMods_";

    private static readonly Regex Line = new(@"^([\w\-]+)\s*=\s*(.*)$", RegexOptions.Compiled);

    private LegacyConfig(Dictionary<string, string> values, string? path)
    {
        Values = values;
        Path = path;
    }

    /// <summary>Every "key = value" line, last one wins, keys case-sensitive as the old client matched them.</summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    public string? Path { get; }

    public static LegacyConfig Parse(string text, string? path = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var match = Line.Match(raw.TrimEnd('\r'));
            if (!match.Success) continue;
            values[match.Groups[1].Value] = match.Groups[2].Value.Trim();
        }

        return new LegacyConfig(values, path);
    }

    public static LegacyConfig Load(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>The console address, or null when the file never had one.</summary>
    public string? Ip =>
        Values.TryGetValue("ip", out var ip) && !string.IsNullOrWhiteSpace(ip) ? ip.Trim() : null;

    /// <summary>True when at least one combo key is in the file; the old client wrote them only after the combo dialog was used.</summary>
    public bool HasComboKeys => ComboKeys.Any(c => Values.ContainsKey(c.Key));

    /// <summary>
    /// The five combos the console knows, at the value the old client used. The old defaults are
    /// filled in for a missing or zero entry, because that is what the old client did on every
    /// start, so importing them reproduces what the user's pad actually did.
    /// </summary>
    public IReadOnlyList<ComboImport> Combos
    {
        get
        {
            var list = new List<ComboImport>(ComboKeys.Length);
            foreach (var (action, key, fallback) in ComboKeys)
            {
                uint mask = 0;
                if (Values.TryGetValue(key, out var text)) TryParseMask(text, out mask);
                bool wasDefault = mask == 0;
                list.Add(new ComboImport(action, wasDefault ? fallback : mask, wasDefault));
            }

            return list;
        }
    }

    /// <summary><c>autoApplyMods_&lt;TITLEID&gt; = folder-one,folder-two</c>, keyed by title id.</summary>
    public IReadOnlyDictionary<string, string[]> ModAutoByTitle
    {
        get
        {
            var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in Values)
            {
                if (!key.StartsWith(ModAutoPrefix, StringComparison.Ordinal)) continue;
                string title = key[ModAutoPrefix.Length..];
                if (title.Length == 0) continue;

                var folders = value.Split(',')
                    .Select(f => f.Trim())
                    .Where(f => f.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (folders.Length > 0) result[title] = folders;
            }

            return result;
        }
    }

    /// <summary><c>SavedColor1No&lt;slot&gt;</c> and its two siblings, as ChargebootColorPicker wrote them.</summary>
    private static readonly Regex ColourKey = new(@"^SavedColor([123])No(\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// The chargeboot colour picker's saved slots, lowest slot first, as 0xRRGGBB triples in the
    /// three colours this client exposes. A slot needs all three keys present and parseable: two
    /// thirds of a colour scheme is not one.
    /// <para>
    /// The old dialog's three swatches were FRONT, MID and BACK: swatch 1 was written to both the
    /// primary-front and the tint word, swatch 2 over the tint word again (so the mid colour is the
    /// one the tint word keeps), and swatch 3 to the primary-back word. Front, back and tint here
    /// are therefore SavedColor1, SavedColor3 and SavedColor2 in that order.
    /// </para>
    /// </summary>
    public IReadOnlyList<(int Slot, uint Front, uint Back, uint Tint)> ColourSlots
    {
        get
        {
            var found = new SortedDictionary<int, uint?[]>();
            foreach (var (key, value) in Values)
            {
                var match = ColourKey.Match(key);
                if (!match.Success) continue;
                if (!int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
                {
                    continue;
                }

                if (!TryParseArgb(value, out uint rgb)) continue;

                if (!found.TryGetValue(slot, out var trio)) found[slot] = trio = new uint?[3];
                trio[match.Groups[1].Value[0] - '1'] = rgb;
            }

            var result = new List<(int Slot, uint Front, uint Back, uint Tint)>();
            foreach (var (slot, trio) in found)
            {
                if (trio[0] is { } front && trio[1] is { } mid && trio[2] is { } back)
                {
                    result.Add((slot, front, back, mid));
                }
            }

            return result;
        }
    }

    /// <summary>Anything worth importing at all: an IP, a combo the user set, a mod list or a colour slot.</summary>
    public bool HasAnything => Ip is not null || HasComboKeys || ModAutoByTitle.Count > 0 || ColourSlots.Count > 0;

    /// <summary>The old client stored the mask as a decimal int; a hand-edited file may say 0x.</summary>
    public static bool TryParseMask(string text, out uint mask)
    {
        text = (text ?? string.Empty).Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask);
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int signed) && signed >= 0)
        {
            mask = (uint)signed;
            return true;
        }

        mask = 0;
        return false;
    }

    /// <summary>
    /// One saved colour as the old client wrote it: <c>Color.ToArgb()</c>, a signed decimal int
    /// holding 0xAARRGGBB, so an opaque colour comes out negative. The alpha is dropped, because
    /// the console decides that byte itself. A hand-edited 0x value is accepted too.
    /// </summary>
    public static bool TryParseArgb(string text, out uint rgb)
    {
        text = (text ?? string.Empty).Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
            {
                rgb = hex & 0x00FFFFFFu;
                return true;
            }

            rgb = 0;
            return false;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int argb))
        {
            rgb = (uint)argb & 0x00FFFFFFu;
            return true;
        }

        rgb = 0;
        return false;
    }
}
