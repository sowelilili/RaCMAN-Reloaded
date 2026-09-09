using System.Xml;

using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Reading the run's split names off disk, which is the only way this client can know the name of
/// the split after the current one: LiveSplit's server has no command that returns a split by
/// index, and the build the user actually has does not answer <c>getupcomingsplitname</c> at all.
/// </summary>
public class LiveSplitRunTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "racman-lss-" + Guid.NewGuid().ToString("N"));

    public LiveSplitRunTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* the test is over */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A run file as LiveSplit writes one, cut down: the icons and the history are what make a real
    /// file half a megabyte, and the shapes that could confuse a parser are all here — a name in
    /// the attempt history, names as attributes on the split times, and an autosplitter settings
    /// block after the segments with a &lt;Name&gt; of its own.
    /// </summary>
    public static string Lss(string? game, string? category, params string[] segments)
    {
        string body = string.Join("\n", segments.Select(name => $"""
            <Segment>
              <Name>{name}</Name>
              <Icon></Icon>
              <SplitTimes><SplitTime name="Personal Best"><RealTime>00:01:00</RealTime></SplitTime></SplitTimes>
              <SegmentHistory><Time id="1"><RealTime>00:01:00</RealTime></Time></SegmentHistory>
            </Segment>
        """));

        return $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Run version="1.7.0">
          <GameIcon></GameIcon>
        {(game is null ? string.Empty : $"  <GameName>{game}</GameName>")}
        {(category is null ? string.Empty : $"  <CategoryName>{category}</CategoryName>")}
          <Metadata>
            <Run id="" />
            <Platform usesEmulator="False">PlayStation 3</Platform>
            <Variables><Variable name="Name">not a segment</Variable></Variables>
          </Metadata>
          <AttemptHistory><Attempt id="1" started="01/01/2020 00:00:00" /></AttemptHistory>
          <Segments>
        {body}
          </Segments>
          <AutoSplitterSettings><Name>not a segment either</Name></AutoSplitterSettings>
        </Run>
        """;
    }

    private string Write(string name, string xml)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, xml);
        return path;
    }

    /// <summary>A settings.cfg holding just the recent-splits list, as LiveSplit lays it out.</summary>
    private static string SettingsCfg(params string[] paths)
    {
        string rows = string.Join("\n", paths.Select(path =>
            $"""    <SplitsFile gameName="Ratchet &amp; Clank: Up Your Arsenal" categoryName="NG+ (SSD)" lastTimingMethod="RealTime" lastHotkeyProfile="Default">{path}</SplitsFile>"""));

        return $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Settings version="1.8.17">
          <WarnOnReset>True</WarnOnReset>
          <RecentSplits>
        {rows}
          </RecentSplits>
        </Settings>
        """;
    }

    // ---------------------------------------------------------------- the run file

    [Fact]
    public void ARunFileGivesUpItsGameCategoryAndSegmentNamesInOrder()
    {
        var run = LiveSplitRun.Parse(
            Lss("Ratchet &amp; Clank: Going Commando", "NG+ (SSD)", "Aranos", "Oozla", "Maktar Resort"),
            @"C:\splits\GC NG+.lss");

        Assert.Equal("Ratchet & Clank: Going Commando", run.GameName);
        Assert.Equal("NG+ (SSD)", run.CategoryName);
        Assert.Equal(new[] { "Aranos", "Oozla", "Maktar Resort" }, run.SegmentNames);
        Assert.Equal("GC NG+.lss", run.FileName);
        Assert.Equal("GC NG+.lss, NG+ (SSD), 3 segments", run.Summary);

        // Only the segments' own names: not the one in the metadata, and not the one the
        // autosplitter settings block carries after the segments end.
        Assert.DoesNotContain("not a segment", run.SegmentNames);
        Assert.DoesNotContain("not a segment either", run.SegmentNames);
    }

    [Fact]
    public void AFileWithNoCategoryOrGameIsStillARun()
    {
        var run = LiveSplitRun.Parse(Lss(null, null, "Veldin", "Novalis"));

        Assert.Equal(string.Empty, run.GameName);
        Assert.Equal(string.Empty, run.CategoryName);
        Assert.Equal(new[] { "Veldin", "Novalis" }, run.SegmentNames);

        // And a run with no segments at all is empty rather than an error.
        Assert.Empty(LiveSplitRun.Parse("<Run version=\"1.7.0\"><GameName>x</GameName></Run>").SegmentNames);
    }

    [Fact]
    public void SomethingThatIsNotXmlThrowsRatherThanReadingAsAnEmptyRun()
    {
        Assert.Throws<XmlException>(() => LiveSplitRun.Parse("this is not a splits file"));
        Assert.Throws<FileNotFoundException>(() => LiveSplitRun.Load(Path.Combine(_folder, "nothing.lss")));
    }

    [Fact]
    public void TheUpcomingNameIsTheOneAfterTheIndexAndNothingAfterTheLast()
    {
        var run = LiveSplitRun.Parse(Lss("game", "category", "Aranos", "Oozla", "Maktar"));

        // -1 is a timer that is not running, and LiveSplit's own upcoming name there is the first
        // segment, so this answers the same thing.
        Assert.Equal("Aranos", run.Upcoming(-1));
        Assert.Equal("Oozla", run.Upcoming(0));
        Assert.Equal("Maktar", run.Upcoming(1));
        Assert.Null(run.Upcoming(2));
        Assert.Null(run.Upcoming(9));
        Assert.Null(run.Upcoming(-5));
    }

    // ---------------------------------------------------------------- LiveSplit's own settings

    [Fact]
    public void TheRecentSplitsListIsReadOutOfLiveSplitsSettingsFile()
    {
        // The entity is the point: every Ratchet game's name has an ampersand in it.
        const string cfg = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Settings version="1.8.17">
          <RecentSplits>
            <SplitsFile gameName="Ratchet &amp; Clank: Up Your Arsenal" categoryName="NG+ (SSD)" lastTimingMethod="RealTime" lastHotkeyProfile="Default">C:\Users\atune\Documents\LiveSplit\Splits\for src\UYA NG+.lss</SplitsFile>
            <SplitsFile gameName="Ratchet &amp; Clank: Going Commando" categoryName="NG+" lastTimingMethod="RealTime" lastHotkeyProfile="Default">C:\Users\atune\Documents\LiveSplit\Splits\GC NG+.lss</SplitsFile>
            <SplitsFile gameName="Ratchet &amp; Clank: Up Your Arsenal" categoryName="NG+ (SSD)" lastTimingMethod="GameTime" lastHotkeyProfile="Default">C:\Users\atune\Documents\LiveSplit\Splits\for src\UYA NG+.lss</SplitsFile>
          </RecentSplits>
        </Settings>
        """;

        var recent = LiveSplitFolder.ParseRecentSplits(cfg);

        // Newest first, and the same file listed twice is one candidate.
        Assert.Equal(new[]
        {
            @"C:\Users\atune\Documents\LiveSplit\Splits\for src\UYA NG+.lss",
            @"C:\Users\atune\Documents\LiveSplit\Splits\GC NG+.lss",
        }, recent);

        // A settings file from a LiveSplit that has never opened a run is not a failure.
        Assert.Empty(LiveSplitFolder.ParseRecentSplits("<Settings version=\"1.8.17\" />"));
        Assert.Throws<XmlException>(() => LiveSplitFolder.ParseRecentSplits("nonsense"));
    }

    [Fact]
    public void AFolderWithNoSettingsFileHasNoRecentSplitsAndNoComplaint()
    {
        Assert.Empty(LiveSplitFolder.RecentSplits(_folder, out string? problem));
        Assert.Null(problem);

        // A folder the user named wins over the running process; a path to the exe means its folder.
        Assert.Equal(_folder, LiveSplitFolder.Find(_folder));
        string exe = Write("LiveSplit.exe", "not really");
        Assert.Equal(_folder, LiveSplitFolder.Find(exe));
    }

    // ---------------------------------------------------------------- picking the right run

    [Fact]
    public void TheRunLiveSplitHasOpenIsTheOneWhoseNamesAgreeWithIt()
    {
        string gc = Write("GC NG+.lss", Lss("Going Commando", "NG+", "Aranos", "Oozla", "Maktar Resort"));
        string uya = Write("UYA NG+.lss", Lss("Up Your Arsenal", "NG+ (SSD)", "Veldin", "Florana", "Phoenix"));
        File.WriteAllText(Path.Combine(_folder, LiveSplitFolder.SettingsFileName), SettingsCfg(gc, uya));

        var library = new LiveSplitRunLibrary();
        var loaded = library.Load(splitsFile: null, folderHint: _folder);

        Assert.Equal(2, loaded.Candidates);
        Assert.False(loaded.Manual);
        Assert.Null(loaded.Problem);

        // Nothing is chosen until LiveSplit says where it is: two runs, both plausible.
        Assert.Null(loaded.Run);
        Assert.False(loaded.HasNames);

        // Split 1 of a run whose previous split is "Veldin" is UYA's, and Going Commando's file
        // agrees with neither name.
        Assert.True(library.Validate(1, "Florana", "Veldin"));

        var state = library.State;
        Assert.Equal(uya, state.Run!.Path);
        Assert.True(state.Verified);
        Assert.Equal("UYA NG+.lss, NG+ (SSD), 3 segments, verified", state.Summary);
        Assert.Equal("Phoenix", library.Upcoming(1));
    }

    [Fact]
    public void LoadingDifferentSplitsInLiveSplitMovesTheClientToThem()
    {
        string gc = Write("GC NG+.lss", Lss("Going Commando", "NG+", "Aranos", "Oozla", "Maktar Resort"));
        string uya = Write("UYA NG+.lss", Lss("Up Your Arsenal", "NG+ (SSD)", "Veldin", "Florana", "Phoenix"));
        File.WriteAllText(Path.Combine(_folder, LiveSplitFolder.SettingsFileName), SettingsCfg(gc, uya));

        var library = new LiveSplitRunLibrary();
        library.Load(null, _folder);

        Assert.True(library.Validate(1, "Oozla", "Aranos"));
        Assert.Equal(gc, library.Run!.Path);

        // A stopped timer says index -1 and names nothing: there is nothing to check the run
        // against, so it is kept and only its "verified" claim is given up.
        Assert.True(library.Validate(-1, null));
        Assert.Equal(gc, library.Run!.Path);
        Assert.False(library.State.Verified);
        Assert.Equal("Oozla", library.Upcoming(0));

        // The user loads the other run. The current name stops matching, so the client looks again.
        Assert.True(library.Validate(0, "Veldin"));
        Assert.Equal(uya, library.Run!.Path);
        Assert.True(library.State.Verified);
        Assert.Equal("Florana", library.Upcoming(0));

        // And a run nothing on disk agrees with leaves the client with no names rather than the
        // wrong ones: the route would silently gate on another game's splits.
        Assert.False(library.Validate(0, "Dread Zone"));
        Assert.Null(library.Run);
        Assert.Null(library.Upcoming(0));
        Assert.False(library.State.HasNames);
    }

    [Fact]
    public void AFilePickedByHandIsUsedWhateverLiveSplitSays()
    {
        string picked = Write("DL NG+.lss", Lss("Deadlocked", "NG+", "Dread Zone", "Catacrom", "Sarathos"));

        var library = new LiveSplitRunLibrary();
        var state = library.Load(picked);

        // It is in hand at once, with nothing to confirm it yet.
        Assert.True(state.Manual);
        Assert.False(state.Verified);
        Assert.Equal(picked, state.Run!.Path);
        Assert.Equal("Catacrom", library.Upcoming(0));

        Assert.True(library.Validate(1, "Catacrom", "Dread Zone"));
        Assert.True(library.State.Verified);

        // A disagreement does not throw the user's own choice away; it stops claiming to be right.
        Assert.True(library.Validate(1, "Oozla", "Aranos"));
        Assert.Equal(picked, library.Run!.Path);
        Assert.False(library.State.Verified);
    }

    [Fact]
    public void TwoRunsOfTheSameGameAreToldApartByWhereTheyEnd()
    {
        // The same route as far as the split LiveSplit is on: only the end of the run differs, and
        // it is the whole reason getlastsplitname is asked. Without it either file would do, and
        // they stop agreeing three splits later.
        string any = Write("GC Any.lss", Lss("Going Commando", "Any%", "Aranos", "Oozla", "Yeedil"));
        string apb = Write("GC APB.lss", Lss("Going Commando", "APB", "Aranos", "Oozla", "Damosel"));
        File.WriteAllText(Path.Combine(_folder, LiveSplitFolder.SettingsFileName), SettingsCfg(any, apb));

        var library = new LiveSplitRunLibrary();
        library.Load(null, _folder);

        Assert.True(library.Validate(1, "Oozla", "Aranos", "Damosel"));
        Assert.Equal(apb, library.Run!.Path);

        // Reloaded so the earlier choice does not simply stand, then asked the other way round.
        library.Load(null, _folder);
        Assert.True(library.Validate(1, "Oozla", "Aranos", "Yeedil"));
        Assert.Equal(any, library.Run!.Path);

        // "-", which is what the server answers before a run starts, rules nothing out.
        library.Load(null, _folder);
        Assert.True(library.Validate(1, "Oozla", "Aranos"));
        Assert.Equal(any, library.Run!.Path);

        // Nor does a name no file ends with: a build that means something else by "the last split"
        // must leave the client with the run the other names point at, not with none at all.
        library.Load(null, _folder);
        Assert.True(library.Validate(1, "Oozla", "Aranos", "Aranos"));
        Assert.Equal(any, library.Run!.Path);
    }

    [Fact]
    public void TheRunAlreadyInHandKeepsItWhileItStillFits()
    {
        // Both files agree about the split LiveSplit is on, so which one is chosen has to stay
        // chosen: a route whose comparison name changed every second would split at random.
        string first = Write("first.lss", Lss("game", "one", "Aranos", "Oozla", "Yeedil"));
        string second = Write("second.lss", Lss("game", "two", "Aranos", "Oozla", "Yeedil"));
        File.WriteAllText(Path.Combine(_folder, LiveSplitFolder.SettingsFileName), SettingsCfg(second, first));

        var library = new LiveSplitRunLibrary();
        library.Load(null, _folder);

        Assert.True(library.Validate(0, "Aranos", null, "Yeedil"));
        Assert.Equal(second, library.Run!.Path);

        for (int i = 0; i < 5; i++) Assert.True(library.Validate(0, "Aranos", null, "Yeedil"));
        Assert.Equal(second, library.Run!.Path);
    }

    [Fact]
    public void AFileThatIsNotARunIsReportedRatherThanIgnored()
    {
        string broken = Write("broken.lss", "half a file <Run>");

        var state = new LiveSplitRunLibrary().Load(broken);

        Assert.Null(state.Run);
        Assert.Equal(0, state.Candidates);
        Assert.NotNull(state.Problem);
        Assert.Contains("broken.lss", state.Problem!);
    }

    [Fact]
    public void AnEmptyFolderSaysWhyThereAreNoSplits()
    {
        File.WriteAllText(Path.Combine(_folder, LiveSplitFolder.SettingsFileName),
            SettingsCfg(Path.Combine(_folder, "gone.lss")));

        var state = new LiveSplitRunLibrary().Load(null, _folder);

        Assert.Null(state.Run);
        Assert.NotNull(state.Problem);
        Assert.Contains("no splits files", state.Problem!);
    }
}
