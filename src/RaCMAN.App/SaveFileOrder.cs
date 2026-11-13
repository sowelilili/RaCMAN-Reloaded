using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>
/// The order of the rows in a savefile category, by the Settings choice (<see cref="SaveFileSort"/>).
/// <para>
/// By planet, the default: each save goes under the planet it was made on, which the save itself
/// says (<see cref="SaveFilePlanet"/>), in the running game's level order, the line order of its
/// planets file (<see cref="AutosplitRoutes"/>). A level the file has no planet on is no planet.
/// Saves on one planet are in natural order, and the saves whose planet is not known (not read
/// yet, too short, not a save of this game, only on the console where it cannot be read) come
/// after all of them, in natural order too.
/// </para>
/// <para>
/// Alphabetical: natural order alone, which ignores case and compares numbers as numbers,
/// "Kerwan 2" before "Kerwan 10".
/// </para>
/// </summary>
public static class SaveFileOrder
{
    /// <summary>
    /// The rows in the order the panel lists them. <paramref name="planetOf"/> is each row's level
    /// as its save says it, or null; without it, or without a game, the order is natural.
    /// </summary>
    public static SaveFileEntry[] Sort(IEnumerable<SaveFileEntry> rows, GameId game, SaveFileSort sort,
        Func<SaveFileEntry, int?>? planetOf)
    {
        bool byPlanet = sort == SaveFileSort.ByPlanet && game != GameId.None && planetOf is not null;
        return rows
            .Select(row => (Row: row, Planet: byPlanet ? PlanetIndex(game, planetOf!(row)) : -1))
            .OrderBy(r => r.Planet < 0 ? 1 : 0)
            .ThenBy(r => r.Planet)
            // The name on screen, without the .sav, so "Kerwan" comes before "Kerwan 2" rather than
            // after it on the strength of a dot sorting after a space.
            .ThenBy(r => r.Row.DisplayName, NaturalComparer.Instance)
            .ThenBy(r => r.Row.Name, StringComparer.Ordinal)
            .Select(r => r.Row)
            .ToArray();
    }

    /// <summary>The natural order alone.</summary>
    public static SaveFileEntry[] Sort(IEnumerable<SaveFileEntry> rows) =>
        Sort(rows, GameId.None, SaveFileSort.Alphabetical, null);

    /// <summary>
    /// Where a save on <paramref name="level"/> goes: the level itself when the game's planets file
    /// has a planet on that line, -1 (below every planet) when not.
    /// </summary>
    public static int PlanetIndex(GameId game, int? level) =>
        level is int index && AutosplitRoutes.Knows(game, index) ? index : -1;

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
