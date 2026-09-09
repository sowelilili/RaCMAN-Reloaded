using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace RaCMAN.App;

/// <summary>Where the name of the split after the current one came from.</summary>
public enum UpcomingNameSource
{
    /// <summary>Nowhere: the route cannot be checked, so a planet event does not split.</summary>
    None,

    /// <summary>LiveSplit answered <c>getupcomingsplitname</c>, which only newer builds do.</summary>
    LiveSplit,

    /// <summary>The run's own <c>.lss</c>, read off disk and lined up with the split index.</summary>
    SplitsFile,
}

/// <summary>
/// One LiveSplit run file: the game, the category and the segment names in order.
/// <para>
/// This exists because LiveSplit's TCP server has no command that returns a split by index, and
/// most installed builds do not answer <c>getupcomingsplitname</c> at all. The planet route needs
/// the name of the split <em>after</em> the current one, so the client reads the run's own file and
/// counts from <c>getsplitindex</c> instead.
/// </para>
/// <para>
/// Only the names are kept. A <c>.lss</c> is mostly base64 icons and split histories, so it is read
/// forwards with an <see cref="XmlReader"/> and abandoned at <c>&lt;/Segments&gt;</c> rather than
/// held in memory as a document.
/// </para>
/// </summary>
public sealed class LiveSplitRun
{
    public const string Extension = ".lss";

    /// <summary>What the file dialog calls this kind of file.</summary>
    public const string FilterDescription = "LiveSplit splits";

    /// <summary>Bigger than any real run file; a wrong path picked by hand is not read into memory.</summary>
    public const long MaxBytes = 32L * 1024 * 1024;

    private LiveSplitRun(string path, string gameName, string categoryName, string[] segmentNames)
    {
        Path = path;
        GameName = gameName;
        CategoryName = categoryName;
        SegmentNames = segmentNames;
    }

    /// <summary>Where it was read from. Empty for a run parsed straight out of a string.</summary>
    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The <c>&lt;GameName&gt;</c>, or empty when the file has none.</summary>
    public string GameName { get; }

    /// <summary>The <c>&lt;CategoryName&gt;</c>, or empty when the file has none.</summary>
    public string CategoryName { get; }

    /// <summary>Every segment name, in run order. Empty for a file with no segments.</summary>
    public string[] SegmentNames { get; }

    /// <summary>One line for the panel: the file, its category and how long the run is.</summary>
    public string Summary
    {
        get
        {
            string name = FileName.Length > 0 ? FileName : GameName;
            string category = CategoryName.Length > 0 ? $", {CategoryName}" : string.Empty;
            return $"{name}{category}, {SegmentNames.Length} segments";
        }
    }

    /// <summary>Reads one run file. Throws on an unreadable file or XML that is not one.</summary>
    public static LiveSplitRun Load(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Length > MaxBytes)
        {
            throw new InvalidDataException($"{info.Name} is {info.Length / (1024 * 1024)} MB, which is not a splits file");
        }

        using var stream = File.OpenRead(path);
        return Read(XmlReader.Create(stream, ReaderSettings), System.IO.Path.GetFullPath(path));
    }

    /// <summary>The same parse from a string, which is what the tests and a paste would use.</summary>
    public static LiveSplitRun Parse(string xml, string path = "") =>
        Read(XmlReader.Create(new StringReader(xml), ReaderSettings), path);

    /// <summary>The name of segment <paramref name="index"/>, or null when the run is shorter.</summary>
    public string? NameAt(int index) =>
        index >= 0 && index < SegmentNames.Length ? SegmentNames[index] : null;

    /// <summary>
    /// The split after the one at <paramref name="index"/>, which is what the route compares. A
    /// not-running timer reports -1, and LiveSplit's own upcoming name there is the first segment,
    /// so -1 answers the same thing. Null on the last segment: there is nothing after it.
    /// </summary>
    public string? Upcoming(int index) => index < -1 ? null : NameAt(index + 1);

    /// <summary>
    /// Whether this run is the one LiveSplit has loaded, as far as its answers can tell: the split
    /// at the reported index carries the reported name, and the one before it carries the previous
    /// name. A null previous is a question this build did not answer and is not held against it.
    /// </summary>
    public bool Agrees(int index, string? current, string? previous = null)
    {
        if (index < 0 || index >= SegmentNames.Length) return false;
        if (!Same(SegmentNames[index], current)) return false;
        if (index > 0 && previous is not null && !Same(SegmentNames[index - 1], previous)) return false;

        return true;
    }

    /// <summary>
    /// Whether the run ends with this name, which is what <c>getlastsplitname</c> answers on the
    /// builds that have it — but only while the timer is running; before that they answer "-".
    /// It is the one name that does not move with the run, so it is what tells two routes of the
    /// same game apart on the very first split, where every candidate still agrees.
    /// </summary>
    public bool EndsWith(string? name) => SegmentNames.Length > 0 && Same(SegmentNames[^1], name);

    private static bool Same(string mine, string? theirs) =>
        theirs is not null && string.Equals(mine.Trim(), theirs.Trim(), StringComparison.Ordinal);

    private static XmlReaderSettings ReaderSettings => new()
    {
        // A splits file is data, not a document that may reach out: no DTD, no resolver.
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
    };

    /// <summary>
    /// One forward pass: the two names at the top of the file, then every <c>&lt;Name&gt;</c> that
    /// is a direct child of a <c>&lt;Segment&gt;</c>. Depth is checked rather than the element name
    /// alone, because a segment's history and a game's autosplitter settings can hold anything.
    /// </summary>
    private static LiveSplitRun Read(XmlReader reader, string path)
    {
        string game = string.Empty;
        string category = string.Empty;
        var names = new List<string>();

        using (reader)
        {
            int segmentsDepth = -1;
            int segmentDepth = -1;
            bool more = reader.Read();

            while (more)
            {
                // ReadElementContentAsString leaves the reader on the node after the element it
                // read, so a turn that used it must not read again or it would skip that node —
                // which is exactly how the category, written straight after the game name, went
                // missing.
                bool consumed = false;

                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (segmentsDepth >= 0 && reader.Name == "Segments" && reader.Depth == segmentsDepth) break;
                }
                else if (reader.NodeType == XmlNodeType.Element)
                {
                    if (segmentsDepth < 0)
                    {
                        switch (reader.Name)
                        {
                            case "GameName":
                                game = reader.ReadElementContentAsString().Trim();
                                consumed = true;
                                break;
                            case "CategoryName":
                                category = reader.ReadElementContentAsString().Trim();
                                consumed = true;
                                break;
                            case "Segments":
                                if (reader.IsEmptyElement) return Done();
                                segmentsDepth = reader.Depth;
                                break;
                        }
                    }
                    else if (reader.Name == "Segment" && reader.Depth == segmentsDepth + 1)
                    {
                        segmentDepth = reader.Depth;
                    }
                    else if (reader.Name == "Name" && segmentDepth >= 0 && reader.Depth == segmentDepth + 1)
                    {
                        names.Add(reader.ReadElementContentAsString().Trim());
                        consumed = true;
                    }
                }

                more = consumed ? reader.NodeType != XmlNodeType.None : reader.Read();
            }
        }

        return Done();

        LiveSplitRun Done() => new(path, game, category, names.ToArray());
    }
}

/// <summary>
/// Finding LiveSplit's own folder and the runs it has open recently. LiveSplit keeps
/// <c>settings.cfg</c> beside <c>LiveSplit.exe</c>, and its <c>&lt;RecentSplits&gt;</c> list is the
/// only place the path of the run the user has loaded can be read from: the TCP server never says.
/// </summary>
public static class LiveSplitFolder
{
    public const string SettingsFileName = "settings.cfg";

    /// <summary>Enough recent runs to hold every splits file a person actually switches between.</summary>
    public const int MaxRecent = 60;

    /// <summary>
    /// The folder to look in: the one the user named, else the folder the running LiveSplit was
    /// started from. Null when neither is there, which is the normal case with LiveSplit closed.
    /// </summary>
    public static string? Find(string? hint = null)
    {
        hint = (hint ?? string.Empty).Trim();
        if (hint.Length > 0)
        {
            try
            {
                if (Directory.Exists(hint)) return hint;

                // A path to LiveSplit.exe itself is what someone would paste first.
                if (File.Exists(hint)) return System.IO.Path.GetDirectoryName(hint);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // A path that is not one is simply not a folder.
            }
        }

        return FromRunningProcess();
    }

    /// <summary>
    /// Where the running LiveSplit lives, from its own main module. Windows only, and everything
    /// about another process can fail — it may exit between the two calls, or be a build this user
    /// may not read — so nothing in here is allowed to throw.
    /// </summary>
    public static string? FromRunningProcess()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            foreach (var process in Process.GetProcessesByName("LiveSplit"))
            {
                using (process)
                {
                    try
                    {
                        string? file = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(file)) return System.IO.Path.GetDirectoryName(file);
                    }
                    catch (Exception)
                    {
                        // Access denied, or it exited while we asked. Try the next one.
                    }
                }
            }
        }
        catch (Exception)
        {
            // No process list on this platform.
        }

        return null;
    }

    public static string SettingsFile(string folder) => System.IO.Path.Combine(folder, SettingsFileName);

    /// <summary>
    /// The <c>.lss</c> paths LiveSplit has opened recently, newest first, as its own settings file
    /// lists them. Empty when there is no settings file; <paramref name="problem"/> says why when
    /// there was one and it could not be read.
    /// </summary>
    public static string[] RecentSplits(string folder, out string? problem)
    {
        problem = null;
        string path = SettingsFile(folder);

        try
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            return ParseRecentSplits(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
        {
            problem = $"{SettingsFileName}: {ex.Message}";
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The paths inside <c>&lt;RecentSplits&gt;</c>, in order, without repeats. The game and
    /// category on each entry are LiveSplit's cache of the file's own, so they are not read here:
    /// the run file is the truth and it is opened anyway.
    /// </summary>
    public static string[] ParseRecentSplits(string xml)
    {
        // File.ReadAllText strips a byte-order mark, a hand-made string may not have been through it.
        var document = XDocument.Parse(xml.TrimStart('\uFEFF'));

        var recent = document.Root?.Element("RecentSplits");
        if (recent is null) return Array.Empty<string>();

        return recent.Elements("SplitsFile")
            .Select(element => element.Value.Trim())
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxRecent)
            .ToArray();
    }
}

/// <summary>
/// What <see cref="LiveSplitRunLibrary"/> knows, as one value: the panel reads it on the render
/// thread while the LiveSplit worker replaces it, so it is never half a state.
/// </summary>
/// <param name="Verified">
/// True when LiveSplit's own answers agreed with this run at the split index it reported. A run
/// stays chosen but unverified while the timer is not running, because a stopped timer has no index
/// and no current split to check against.
/// </param>
/// <param name="Manual">True when the user named the file, rather than it being discovered.</param>
public sealed record LiveSplitRunState(
    LiveSplitRun? Run, bool Verified, bool Manual, int Candidates, string? Problem)
{
    public static LiveSplitRunState Empty { get; } = new(null, false, false, 0, null);

    /// <summary>True when there are names to count from, which is what the planet route needs.</summary>
    public bool HasNames => Run is not null && Run.SegmentNames.Length > 0;

    /// <summary>The panel's line: which run, how long, and whether LiveSplit confirmed it.</summary>
    public string Summary => Run is null
        ? "no splits file"
        : $"{Run.Summary}, {(Verified ? "verified" : "unverified")}";
}

/// <summary>
/// The run files this client might be looking at, and which one LiveSplit actually has open.
/// <para>
/// Discovery is LiveSplit's own recent-splits list, and the choice between the runs in it is made
/// by asking LiveSplit what it thinks the current split is and finding the file that agrees. A file
/// named by hand skips all of that and is simply used.
/// </para>
/// <para>
/// Loading reads files, so it happens off the render thread; the panel only ever reads
/// <see cref="State"/>, which is replaced whole.
/// </para>
/// </summary>
public sealed class LiveSplitRunLibrary
{
    private readonly object _gate = new();

    private LiveSplitRun[] _candidates = Array.Empty<LiveSplitRun>();
    private LiveSplitRunState _state = LiveSplitRunState.Empty;

    public LiveSplitRunState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>The run currently believed to be LiveSplit's, or null when there is none.</summary>
    public LiveSplitRun? Run => State.Run;

    /// <summary>
    /// Reads the candidates: the file the user named, or every run in LiveSplit's recent list. The
    /// chosen run is dropped, because the list it was chosen from has just been replaced; the next
    /// refresh picks one again from whatever LiveSplit says.
    /// </summary>
    /// <param name="splitsFile">A <c>.lss</c> the user picked; empty to discover instead.</param>
    /// <param name="folderHint">Where LiveSplit is, when it is not running to be asked.</param>
    public LiveSplitRunState Load(string? splitsFile, string? folderHint = null)
    {
        splitsFile = (splitsFile ?? string.Empty).Trim().Trim('"').Trim();

        var candidates = new List<LiveSplitRun>();
        string? problem = null;
        bool manual = splitsFile.Length > 0;

        if (manual)
        {
            try
            {
                candidates.Add(LiveSplitRun.Load(splitsFile));
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                problem = $"{System.IO.Path.GetFileName(splitsFile)}: {ex.Message}";
            }
        }
        else
        {
            string? folder = LiveSplitFolder.Find(folderHint);
            if (folder is null)
            {
                problem = "LiveSplit's folder was not found, so its recent splits could not be read. "
                          + "Start LiveSplit, or pick the .lss file yourself.";
            }
            else
            {
                foreach (var path in LiveSplitFolder.RecentSplits(folder, out problem))
                {
                    try
                    {
                        if (File.Exists(path)) candidates.Add(LiveSplitRun.Load(path));
                    }
                    catch (Exception ex) when (IsReadFailure(ex))
                    {
                        // One unreadable run in a list of fifty is not worth a message: the file
                        // the user has open is what matters, and it is still in the list.
                        _ = ex;
                    }
                }

                if (problem is null && candidates.Count == 0)
                {
                    problem = $"{LiveSplitFolder.SettingsFile(folder)} lists no splits files that still exist.";
                }
            }
        }

        lock (_gate)
        {
            _candidates = candidates.ToArray();

            // A named file is the answer on its own; a discovered one has to earn it.
            var run = manual ? candidates.FirstOrDefault() : null;
            _state = new LiveSplitRunState(run, false, manual, candidates.Count, problem);
            return _state;
        }
    }

    /// <summary>
    /// Checks the chosen run against what LiveSplit just said, and picks another when it no longer
    /// agrees — the user loaded different splits, or renamed one. A stopped timer reports index -1
    /// and no current split: there is nothing to check, so the last run stands as unverified rather
    /// than being thrown away, and the next split confirms it.
    /// </summary>
    /// <param name="last">
    /// <c>getlastsplitname</c>, which matters most on the first split of a run: the recent-splits
    /// list holds several routes through the same game and they all agree about segment zero, but
    /// only one of them ends where LiveSplit says the run ends.
    /// </param>
    /// <returns>True when a run is in hand, verified or not.</returns>
    public bool Validate(int index, string? current, string? previous = null, string? last = null)
    {
        lock (_gate)
        {
            if (index < 0 || string.IsNullOrEmpty(current))
            {
                _state = _state with { Verified = false };
                return _state.Run is not null;
            }

            // Every name first, then everything but the last one. The second pass is what keeps
            // this working on a build that means something else by "the last split": the name is
            // then simply evidence nothing satisfies, rather than evidence against every run.
            var chosen = Pick(index, current, previous, last) ?? Pick(index, current, previous, null);

            // Nothing agrees. A file the user named stays, because they said it was theirs; a
            // discovered one goes, because gating the route on the wrong run's names is worse than
            // not gating it at all.
            _state = chosen is not null
                ? _state with { Run = chosen, Verified = true }
                : _state with { Run = _state.Manual ? _state.Run : null, Verified = false };

            return _state.Run is not null;
        }
    }

    /// <summary>
    /// The first candidate that fits, with the run already in hand given first refusal: two runs
    /// of the same game agree about their opening splits, and swapping between them every second
    /// would make the route's answer depend on the order of a list nobody can see.
    /// </summary>
    private LiveSplitRun? Pick(int index, string? current, string? previous, string? last)
    {
        bool Fits(LiveSplitRun run) =>
            run.Agrees(index, current, previous) && (last is null || run.EndsWith(last));

        if (_state.Run is not null && Fits(_state.Run)) return _state.Run;

        foreach (var candidate in _candidates)
        {
            if (Fits(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>The name of the split after <paramref name="index"/>, from the chosen run.</summary>
    public string? Upcoming(int index) => State.Run?.Upcoming(index);

    private static bool IsReadFailure(Exception ex) =>
        ex is IOException or XmlException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or InvalidDataException;
}
