using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>
/// The order of the rows in a savefile category. A save whose name starts with a planet's name
/// goes above the rest, in the running game's planet order, which is the order of the lines of its
/// planets file (<see cref="AutosplitRoutes"/>, the autosplitter's own <c>&lt;game&gt;-planets.txt</c>).
/// Every other save comes after them, and within one planet, and among the rest, the order is
/// natural and ignores case: "Kerwan 2" before "Kerwan 10".
/// <para>
/// A name is matched against every alias of every planet with spaces, punctuation, underscores
/// and hyphens ignored on both sides, so "veldin_2", "Veldin 2" and "veldin2" are one name. The
/// alias has to be the start of the name and be followed by the end of it, a digit, or something
/// that was ignored, so "Novalis" is never a planet called "Nova". Of the planets that match, the
/// longest alias wins ("Fastoon 2" over "Fastoon"), and between two of the same length the earlier
/// planet does, which is how a bare "Veldin" or "Aranos" is the first visit. A revisit is a line
/// of its own in the planets file with aliases that name it ("veldin 2", "veldin ii").
/// </para>
/// </summary>
public static class SaveFileOrder
{
    /// <summary>
    /// The rows in the order the panel lists them, for <paramref name="game"/>'s planets. A game
    /// with no planets file, or no game at all, keeps the natural order alone.
    /// </summary>
    public static SaveFileEntry[] Sort(IEnumerable<SaveFileEntry> rows, GameId game) =>
        Sort(rows, game == GameId.None ? Array.Empty<string[]>() : AutosplitRoutes.For(game));

    /// <summary>The same against a planet list given directly: one alias array per planet, in game order.</summary>
    public static SaveFileEntry[] Sort(IEnumerable<SaveFileEntry> rows, IReadOnlyList<string[]> planets)
    {
        var aliases = Prepare(planets);
        return rows
            .Select(row => (Row: row, Planet: aliases.Count == 0 ? -1 : PlanetOf(row.DisplayName, aliases)))
            .OrderBy(r => r.Planet < 0 ? 1 : 0)
            .ThenBy(r => r.Planet)
            // The name on screen, without the .sav, so "Kerwan" comes before "Kerwan 2" rather than
            // after it on the strength of a dot sorting after a space.
            .ThenBy(r => r.Row.DisplayName, NaturalComparer.Instance)
            .ThenBy(r => r.Row.Name, StringComparer.Ordinal)
            .Select(r => r.Row)
            .ToArray();
    }

    /// <summary>
    /// The planet a save called <paramref name="name"/> is filed under: its line in the planets
    /// file, or -1 when the name starts with no planet's name.
    /// </summary>
    public static int PlanetOf(string? name, IReadOnlyList<string[]> planets) => PlanetOf(name, Prepare(planets));

    private static int PlanetOf(string? name, IReadOnlyList<(int Planet, string Alias)> aliases)
    {
        var (folded, breaks) = Fold(name);
        if (folded.Length == 0) return -1;

        int best = -1;
        int bestLength = 0;
        foreach (var (planet, alias) in aliases)
        {
            // The list is in planet order, so a tie keeps the earlier planet.
            if (alias.Length <= bestLength) continue;
            if (!folded.StartsWith(alias, StringComparison.Ordinal)) continue;

            int next = alias.Length;
            bool bounded = next == folded.Length || char.IsDigit(folded[next]) || breaks[next];
            if (!bounded) continue;

            best = planet;
            bestLength = alias.Length;
        }

        return best;
    }

    /// <summary>Every planet's aliases folded the way a name is, in planet order.</summary>
    private static List<(int Planet, string Alias)> Prepare(IReadOnlyList<string[]> planets)
    {
        var aliases = new List<(int, string)>();
        for (int planet = 0; planet < planets.Count; planet++)
        {
            foreach (var alias in planets[planet] ?? Array.Empty<string>())
            {
                string folded = Fold(alias).Folded;
                if (folded.Length > 0) aliases.Add((planet, folded));
            }
        }

        return aliases;
    }

    /// <summary>
    /// The letters and digits of <paramref name="text"/> in lower case, and for each of them
    /// whether something that was dropped (a space, punctuation, an underscore or a hyphen) came
    /// just before it. One more entry at the end says the same of the end of the text.
    /// </summary>
    private static (string Folded, bool[] Breaks) Fold(string? text)
    {
        var folded = new System.Text.StringBuilder();
        var breaks = new List<bool>();
        bool dropped = false;

        foreach (char c in text ?? string.Empty)
        {
            if (char.IsLetterOrDigit(c))
            {
                folded.Append(char.ToLowerInvariant(c));
                breaks.Add(dropped);
                dropped = false;
            }
            else
            {
                dropped = true;
            }
        }

        breaks.Add(true);
        return (folded.ToString(), breaks.ToArray());
    }

    /// <summary>
    /// Natural order without regard to case: runs of digits compare as numbers, everything else
    /// as text, so "save 9" comes before "save 10". Two names that only differ in case, or in
    /// leading zeros, still come out in one fixed order.
    /// </summary>
    public static int CompareNatural(string? left, string? right) => NaturalComparer.Instance.Compare(left, right);

    private sealed class NaturalComparer : IComparer<string?>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;

            int i = 0, j = 0;
            while (i < left.Length && j < right.Length)
            {
                if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
                {
                    int startI = i, startJ = j;
                    while (i < left.Length && char.IsDigit(left[i])) i++;
                    while (j < right.Length && char.IsDigit(right[j])) j++;

                    var a = left.AsSpan(startI, i - startI).TrimStart('0');
                    var b = right.AsSpan(startJ, j - startJ).TrimStart('0');
                    if (a.Length != b.Length) return a.Length.CompareTo(b.Length);

                    int digits = a.SequenceCompareTo(b);
                    if (digits != 0) return digits;
                    continue;
                }

                int c = char.ToLowerInvariant(left[i]).CompareTo(char.ToLowerInvariant(right[j]));
                if (c != 0) return c;
                i++;
                j++;
            }

            int rest = (left.Length - i).CompareTo(right.Length - j);
            return rest != 0 ? rest : string.CompareOrdinal(left, right);
        }
    }
}
