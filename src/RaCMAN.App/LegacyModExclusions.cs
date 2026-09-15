namespace RaCMAN.App;

/// <summary>
/// The old RaCMAN's default mods that this client decided not to ship, so that an import from an
/// old folder never puts them back. It is a shipped data file rather than a list in the code
/// because it is a list of names and reasons, and because a user who wants one of them anyway can
/// take the line out.
/// <para>
/// One entry per line: <c>TITLEID/modfolder</c>, then whitespace, then a short reason the import
/// prints. Blank lines and lines starting with <c>#</c> are comments. A missing file is not an
/// error — it means nothing is excluded by name, and <see cref="Problem"/> says so; the rule about
/// Lua in <see cref="LegacyLibraryImport"/> holds whatever this file says.
/// </para>
/// </summary>
public sealed class LegacyModExclusions
{
    public const string FileName = "legacy-mod-exclusions.txt";

    /// <summary>What an entry with no reason of its own is reported as.</summary>
    public const string DefaultReason = "not imported";

    /// <summary>No list at all. What the tests that are about the other rules import with.</summary>
    public static readonly LegacyModExclusions None = new(Empty(), null);

    private static LegacyModExclusions? _shipped;

    private readonly Dictionary<string, string> _reasons;

    private LegacyModExclusions(Dictionary<string, string> reasons, string? problem)
    {
        _reasons = reasons;
        Problem = problem;
    }

    /// <summary>The shipped file, beside the executable with the rest of <c>data/</c>.</summary>
    public static string DefaultPath => Path.Combine(AppPaths.ShippedData, FileName);

    /// <summary>The shipped list, read once: it is the same file for the life of the run.</summary>
    public static LegacyModExclusions Shipped => _shipped ??= Load(DefaultPath);

    /// <summary>
    /// Why the list is empty when it should not be: no file beside the app, or one that could not
    /// be read. Null when the file was read, whatever it held.
    /// </summary>
    public string? Problem { get; }

    public int Count => _reasons.Count;

    /// <summary>Every entry as it was written, <c>TITLEID/modfolder</c>.</summary>
    public IReadOnlyCollection<string> Entries => _reasons.Keys;

    public static LegacyModExclusions Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new LegacyModExclusions(Empty(), $"No {FileName} at {path}: no mod is excluded by name.");
            }

            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LegacyModExclusions(Empty(), $"{FileName}: {ex.Message}");
        }
    }

    public static LegacyModExclusions Parse(string text)
    {
        var reasons = Empty();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            // The entry is the first word; everything after the whitespace is the reason, which is
            // a sentence fragment and may well have a space in it.
            int gap = line.IndexOfAny(new[] { ' ', '\t' });
            var entry = (gap < 0 ? line : line[..gap]).Replace('\\', '/').Trim('/');
            if (entry.Length == 0) continue;

            var reason = gap < 0 ? string.Empty : line[gap..].Trim();
            reasons[entry] = reason.Length > 0 ? reason : DefaultReason;
        }

        return new LegacyModExclusions(reasons, null);
    }

    /// <summary>Why this mod is not imported, or null when the list says nothing about it.</summary>
    public string? Reason(string titleId, string dirName) =>
        _reasons.TryGetValue($"{titleId}/{dirName}", out var reason) ? reason : null;

    public bool Excludes(string titleId, string dirName) => Reason(titleId, dirName) is not null;

    private static Dictionary<string, string> Empty() => new(StringComparer.OrdinalIgnoreCase);
}
