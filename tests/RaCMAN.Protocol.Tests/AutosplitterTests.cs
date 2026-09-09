using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Stands in for LiveSplit's built-in TCP server: it records the commands it is given, answers the
/// get* queries out of a scripted split list, and moves its own phase and split index the way
/// LiveSplit would, so the engine's "ask again after every command" behaviour is exercised.
/// </summary>
internal sealed class FakeLiveSplitServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _commands = new();
    private readonly object _gate = new();

    public FakeLiveSplitServer(params string[] splits)
    {
        Splits = splits.Length > 0 ? splits : new[] { "Aranos", "Oozla", "Maktar" };
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    public string[] Splits { get; set; }

    public int SplitIndex { get; set; }

    public string Phase { get; set; } = "NotRunning";

    /// <summary>
    /// Commands this server ignores, the way the real one silently ignores everything outside its
    /// own list. Nothing is written back at all, which is exactly the case the client must not
    /// mistake for a dead connection.
    /// </summary>
    public HashSet<string> Ignored { get; } = new(StringComparer.Ordinal);

    /// <summary>Everything received, queries included, in order.</summary>
    public string[] Commands
    {
        get { lock (_gate) return _commands.ToArray(); }
    }

    /// <summary>Just the commands that do something: the queries are noise for most assertions.</summary>
    public string[] Actions
    {
        get { lock (_gate) return _commands.Where(c => !LiveSplitClient.IsQuery(c)).ToArray(); }
    }

    /// <summary>Every <c>addloadingtimes</c> line, argument included, in the order it arrived.</summary>
    public List<string> LoadingTimes { get; } = new();

    /// <summary>The seconds each <c>addloadingtimes</c> asked for, as LiveSplit's parser reads them.</summary>
    public double[] LoadingTimeSeconds
    {
        get
        {
            lock (_gate)
            {
                return LoadingTimes
                    .Select(c => double.Parse(c.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture))
                    .ToArray();
            }
        }
    }

    public void ClearCommands()
    {
        lock (_gate)
        {
            _commands.Clear();
            LoadingTimes.Clear();
        }
    }

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line is null) return;

                    string command = line.Trim();
                    lock (_gate) _commands.Add(command);

                    var reply = Handle(command);
                    if (reply is not null) await writer.WriteLineAsync(reply);
                }
            }
        }
        catch (Exception)
        {
            // The client went away.
        }
    }

    private string? Handle(string command)
    {
        lock (_gate)
        {
            if (Ignored.Contains(command)) return null;

            // "addloadingtimes 1.440000" and friends: the verb decides, the argument rides along.
            switch (command.Split(' ')[0])
            {
                case LiveSplitClient.StartTimer:
                    Phase = "Running";
                    SplitIndex = 0;
                    return null;

                case LiveSplitClient.StartOrSplit:
                    if (Phase == "Running") SplitIndex++;
                    else Phase = "Running";
                    return null;

                case LiveSplitClient.Split:
                case LiveSplitClient.SkipSplit:
                    if (Phase == "Running") SplitIndex++;
                    return null;

                case LiveSplitClient.Unsplit:
                    SplitIndex = Math.Max(0, SplitIndex - 1);
                    return null;

                case LiveSplitClient.Reset:
                    Phase = "NotRunning";
                    SplitIndex = 0;
                    return null;

                case LiveSplitClient.Pause:
                    Phase = "Paused";
                    return null;

                case LiveSplitClient.Resume:
                    Phase = "Running";
                    return null;

                case LiveSplitClient.GetCurrentTimerPhase:
                    return Phase;

                case LiveSplitClient.AddLoadingTimes:
                    LoadingTimes.Add(command);
                    return null;

                case LiveSplitClient.GetSplitIndex:
                    return SplitIndex.ToString();

                case LiveSplitClient.GetCurrentSplitName:
                    return NameAt(SplitIndex);

                case LiveSplitClient.GetUpcomingSplitName:
                    return NameAt(SplitIndex + 1);

                case LiveSplitClient.GetPreviousSplitName:
                    return NameAt(SplitIndex - 1);

                // The final segment, but only once the run has started: before that the real server
                // answers "-" here exactly as it does for the other two names.
                case LiveSplitClient.GetLastSplitName:
                    return SplitIndex >= 0 ? NameAt(Splits.Length - 1) : "-";

                case LiveSplitClient.Ping:
                    return "pong";

                default:
                    return null;
            }
        }
    }

    private string NameAt(int index) => index >= 0 && index < Splits.Length ? Splits[index] : string.Empty;

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        _cts.Dispose();
    }
}

/// <summary>
/// The decision engine: which events reach LiveSplit, as which command, and why. The console's
/// half is not involved here — events are handed straight to the engine, which is exactly what
/// QwarkClient does when a push or a poll turns one up.
/// </summary>
public class AutosplitterTests : IDisposable
{
    private static readonly AutosplitEventDesc PlanetEntered = new(
        1, AutosplitKind.Split, AutosplitEventFlags.EnabledByDefault | AutosplitEventFlags.PlanetRoute,
        0, "Planet entered");

    private static readonly AutosplitEventDesc BossDefeated = new(
        2, AutosplitKind.Split, AutosplitEventFlags.EnabledByDefault, 0, "Protopet defeated");

    private static readonly AutosplitEventDesc ArenaEntered = new(
        3, AutosplitKind.Split, AutosplitEventFlags.None, 0, "Maktar arena");

    /// <summary>RaC2's Protopet: the split is also a fixed seven frames off game time.</summary>
    private static readonly AutosplitEventDesc FlatBoss = new(
        2, AutosplitKind.Split, AutosplitEventFlags.EnabledByDefault | AutosplitEventFlags.Flat,
        116_667, "Protopet defeated");

    /// <summary>RaC1's load timer: everything past 7.56 s of a load comes off game time.</summary>
    private static readonly AutosplitEventDesc NormalisedLoad = new(
        4, AutosplitKind.LoadStart, AutosplitEventFlags.Normalise, 7_560_000, "Level load");

    /// <summary>Deadlocked's quit to the XMB, which the old script gave back 14.8 s of.</summary>
    private static readonly AutosplitEventDesc NormalisedPause = new(
        5, AutosplitKind.Pause, AutosplitEventFlags.Normalise, 14_800_000, "Quit to XMB");

    /// <summary>RaC3's long load: a flat second, applied the moment the load starts.</summary>
    private static readonly AutosplitEventDesc FlatLoad = new(
        6, AutosplitKind.LoadStart, AutosplitEventFlags.Flat, 1_000_000, "Long load");

    /// <summary>This test's own folder for the splits files it writes; dropped when it is over.</summary>
    private readonly string _folder =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "racman-splits-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* the test is over */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Writes a splits file with these segment names and answers where it went.</summary>
    private string WriteSplits(string category, params string[] segments)
    {
        string path = Path.Combine(_folder, $"{category}.lss");
        File.WriteAllText(path, LiveSplitRunTests.Lss("Ratchet &amp; Clank", category, segments));
        return path;
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }

        return condition();
    }

    private sealed class Harness : IDisposable
    {
        public Harness(GameId game, string[] splits, params AutosplitEventDesc[] descriptors)
            : this(game, splits, null, descriptors)
        {
        }

        /// <param name="splitsFile">
        /// A <c>.lss</c> the engine should read the run's names from, for the LiveSplit builds that
        /// will not name the upcoming split. Null is the normal case: LiveSplit is asked.
        /// </param>
        public Harness(GameId game, string[] splits, string? splitsFile, AutosplitEventDesc[] descriptors)
        {
            Server = new FakeLiveSplitServer(splits);
            Settings = new Settings();
            Settings.Autosplit.Enabled = true;
            Settings.Autosplit.SplitsFile = splitsFile ?? string.Empty;

            // Discovery must never reach the machine's own LiveSplit: a test that read the
            // developer's fifty recent runs would be both slow and different on every box. The
            // test binaries' own folder exists and holds no settings.cfg, so nothing is found.
            Settings.Autosplit.LiveSplitFolder = AppContext.BaseDirectory;

            LiveSplit = new LiveSplitClient();

            // The app hands results to the render thread; a test applies them where they happen.
            Engine = new Autosplitter(Settings, LiveSplit, action => action())
            {
                Game = game,
                Descriptors = descriptors,
                PlanetNames = new[] { "Aranos", "Oozla", "Maktar", "Endako" },
            };

            LiveSplit.Start(LiveSplitClient.DefaultHost, Server.Port);
        }

        public FakeLiveSplitServer Server { get; }

        public Settings Settings { get; }

        public LiveSplitClient LiveSplit { get; }

        public Autosplitter Engine { get; }

        public AutosplitGameSettings Options => Settings.Autosplit.For(Engine.Game);

        public async Task ReadyAsync()
        {
            Assert.True(await WaitFor(() => LiveSplit.IsConnected), "the fake LiveSplit never accepted the client");

            // Connecting starts a refresh of its own, and the engine runs one at a time, so wait
            // for that one to land rather than racing it with a second that would be dropped.
            Assert.True(await WaitFor(() => Engine.View.Phase != LiveSplitPhase.Unknown),
                "LiveSplit never answered the first phase query");
            await RefreshAsync();
            Server.ClearCommands();
        }

        /// <summary>A refresh that has certainly happened, even if one was already in flight.</summary>
        public async Task RefreshAsync()
        {
            await Engine.RefreshAsync();
            await Task.Delay(30);
            await Engine.RefreshAsync();
        }

        /// <summary>Waits for the engine's fire-and-forget send to reach the server.</summary>
        public Task<bool> Sent(string command) => WaitFor(() => Server.Actions.Contains(command));

        public void Dispose()
        {
            LiveSplit.Dispose();
            Server.Dispose();
        }
    }

    private static AutosplitEvent Split(byte code, uint arg = 0, uint seq = 1) =>
        new(seq, 1200, AutosplitKind.Split, code, arg);

    // ---------------------------------------------------------------- which events split

    [Fact]
    public async Task ACodeThatIsOnByDefaultSplits()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated, ArenaEntered);
        await h.ReadyAsync();

        h.Engine.Handle(Split(BossDefeated.Code));

        Assert.True(await h.Sent(LiveSplitClient.Split));
        Assert.Equal(1, h.Engine.Acted);

        var entry = h.Engine.Log()[^1];
        Assert.Equal("Protopet defeated", entry.Event);
        Assert.True(entry.Acted);
    }

    [Fact]
    public async Task ACodeThatIsOffByDefaultDoesNothingUntilItIsTicked()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated, ArenaEntered);
        await h.ReadyAsync();

        h.Engine.Handle(Split(ArenaEntered.Code));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.Equal(0, h.Engine.Acted);
        Assert.Contains("switched off", h.Engine.Log()[^1].Action);

        h.Options.SetEvent(ArenaEntered.Label, true);
        h.Engine.Handle(Split(ArenaEntered.Code, seq: 2));

        Assert.True(await h.Sent(LiveSplitClient.Split));
    }

    [Fact]
    public async Task ACodeTheUserSwitchedOffStopsSplitting()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated);
        await h.ReadyAsync();

        h.Options.SetEvent(BossDefeated.Label, false);
        h.Engine.Handle(Split(BossDefeated.Code));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.Equal(1, h.Engine.Received);
        Assert.Equal(0, h.Engine.Acted);
    }

    // ---------------------------------------------------------------- the planet route

    [Fact]
    public async Task WithoutTheRouteEveryPlanetSplits()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla", "Maktar" }, PlanetEntered);
        await h.ReadyAsync();

        // Maktar (planet 2) while the next split says Oozla: no route, so it splits anyway.
        h.Engine.Handle(Split(PlanetEntered.Code, 2));

        Assert.True(await h.Sent(LiveSplitClient.Split));
    }

    [Fact]
    public async Task TheRouteComparesTheUpcomingSplitAndNothingElse()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla", "Maktar" }, PlanetEntered);
        await h.ReadyAsync();
        h.Options.PlanetRoute = true;

        // Split 0 is running, so the split you are on is "Aranos" and the upcoming one is "Oozla".
        // Entering Maktar is off route even though Maktar is a split further down the run.
        Assert.Equal("Aranos", h.Engine.View.CurrentSplit);
        Assert.Equal("Oozla", h.Engine.View.UpcomingSplit);

        h.Engine.Handle(Split(PlanetEntered.Code, 2));
        await Task.Delay(200);
        Assert.Empty(h.Server.Actions);
        Assert.Contains("not on this planet's route", h.Engine.Log()[^1].Action);

        // Oozla is planet 1, and rac2-planets.txt calls it "ozla", which "Oozla" contains.
        h.Engine.Handle(Split(PlanetEntered.Code, 1, seq: 2));
        Assert.True(await h.Sent(LiveSplitClient.Split));
        Assert.Contains("next split", h.Engine.Log()[^1].Action);
    }

    [Fact]
    public async Task OnTheLastSegmentAPlanetEventNeverSplits()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated);
        await h.ReadyAsync();
        h.Options.PlanetRoute = true;

        // The run is on its last segment, so there is no upcoming name to compare against.
        h.Server.SplitIndex = 1;
        await h.RefreshAsync();
        Assert.Null(h.Engine.View.UpcomingSplit);

        h.Engine.Handle(Split(PlanetEntered.Code, 1));
        await Task.Delay(200);
        Assert.Empty(h.Server.Actions);
        Assert.Contains("last segment", h.Engine.Log()[^1].Action);

        // Every other reason still splits: the route only ever gates the planet code.
        h.Engine.Handle(Split(BossDefeated.Code, seq: 2));
        Assert.True(await h.Sent(LiveSplitClient.Split));
    }

    // ------------------------------------------------- the route without getupcomingsplitname

    [Fact]
    public async Task TheRunFileNamesTheNextSplitWhenLiveSplitWillNot()
    {
        // The user's own LiveSplit: it knows getsplitindex and the two names either side of the
        // current split, and nothing at all about getupcomingsplitname. The run file is what turns
        // "split 0 of three" into "the next one is called Oozla".
        string splits = WriteSplits("GC NG+", "Aranos", "Oozla", "Maktar Resort");
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla", "Maktar Resort" }, splits,
            new[] { PlanetEntered });
        h.Server.Ignored.Add(LiveSplitClient.GetUpcomingSplitName);
        await h.ReadyAsync();
        h.Options.PlanetRoute = true;

        Assert.False(h.Engine.View.UpcomingSupported);
        Assert.Equal(UpcomingNameSource.SplitsFile, h.Engine.View.UpcomingSource);
        Assert.Equal("Oozla", h.Engine.View.UpcomingSplit);
        Assert.Equal(0, h.Engine.View.SplitIndex);

        // And the run file was confirmed against LiveSplit rather than assumed.
        var runs = h.Engine.Runs.State;
        Assert.True(runs.Verified);
        Assert.Equal("GC NG+.lss, GC NG+, 3 segments, verified", runs.Summary);

        // Maktar (planet 2) is not where the run goes next, so it does not split; Oozla is.
        h.Engine.Handle(Split(PlanetEntered.Code, 2));
        await Task.Delay(200);
        Assert.Empty(h.Server.Actions);
        Assert.Contains("not on this planet's route", h.Engine.Log()[^1].Action);

        h.Engine.Handle(Split(PlanetEntered.Code, 1, seq: 2));
        Assert.True(await h.Sent(LiveSplitClient.Split));
        Assert.Contains("Oozla", h.Engine.Log()[^1].Action);
    }

    [Fact]
    public async Task OnTheLastSegmentOfARunFileAPlanetEventStillNeverSplits()
    {
        string splits = WriteSplits("GC NG+", "Aranos", "Oozla");
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, splits,
            new[] { PlanetEntered, BossDefeated });
        h.Server.Ignored.Add(LiveSplitClient.GetUpcomingSplitName);
        await h.ReadyAsync();
        h.Options.PlanetRoute = true;

        h.Server.SplitIndex = 1;
        await h.RefreshAsync();
        Assert.Null(h.Engine.View.UpcomingSplit);
        Assert.True(h.Engine.View.NamesKnown);

        h.Engine.Handle(Split(PlanetEntered.Code, 1));
        await Task.Delay(200);
        Assert.Empty(h.Server.Actions);
        Assert.Contains("last segment", h.Engine.Log()[^1].Action);

        // Every other reason still splits: the route only ever gates the planet code.
        h.Engine.Handle(Split(BossDefeated.Code, seq: 2));
        Assert.True(await h.Sent(LiveSplitClient.Split));
    }

    [Fact]
    public async Task WithNeitherLiveSplitNorAFileNamingTheNextSplitTheRouteSaysSo()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered);
        h.Server.Ignored.Add(LiveSplitClient.GetUpcomingSplitName);
        await h.ReadyAsync();
        h.Options.PlanetRoute = true;

        Assert.False(h.Engine.View.UpcomingSupported);
        Assert.False(h.Engine.View.NamesKnown);
        Assert.Equal(UpcomingNameSource.None, h.Engine.View.UpcomingSource);

        // Oozla is where the run goes next, but nothing here can know that, so nothing splits.
        h.Engine.Handle(Split(PlanetEntered.Code, 1));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.Contains("split names are not known", h.Engine.Log()[^1].Action);
    }

    [Fact]
    public async Task ANewerLiveSplitIsBelievedOverTheRunFile()
    {
        // A file that disagrees with the LiveSplit answering for itself: the live answer wins, and
        // the file is not consulted behind its back.
        string splits = WriteSplits("stale", "Aranos", "Endako", "Maktar Resort");
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla", "Maktar Resort" }, splits,
            new[] { PlanetEntered });
        await h.ReadyAsync();
        h.Options.PlanetRoute = true;

        Assert.True(h.Engine.View.UpcomingSupported);
        Assert.Equal(UpcomingNameSource.LiveSplit, h.Engine.View.UpcomingSource);
        Assert.Equal("Oozla", h.Engine.View.UpcomingSplit);

        // The file said Endako (planet 3) came next; LiveSplit said Oozla, and Oozla splits.
        h.Engine.Handle(Split(PlanetEntered.Code, 3));
        await Task.Delay(200);
        Assert.Empty(h.Server.Actions);

        h.Engine.Handle(Split(PlanetEntered.Code, 1, seq: 2));
        Assert.True(await h.Sent(LiveSplitClient.Split));
    }

    [Fact]
    public async Task AFilePickedByHandThatDisagreesWithLiveSplitIsSaidToBeUnverified()
    {
        // The file names a run LiveSplit is not on. It was named by hand, so it is not thrown away
        // — the user said it was theirs — but nothing claims it was confirmed, and the panel shows
        // that in yellow rather than green.
        string splits = WriteSplits("someone elses", "Veldin", "Novalis", "Aridia");
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla", "Maktar Resort" }, splits,
            new[] { PlanetEntered });
        h.Server.Ignored.Add(LiveSplitClient.GetUpcomingSplitName);
        await h.ReadyAsync();

        var runs = h.Engine.Runs.State;
        Assert.True(runs.Manual);
        Assert.False(runs.Verified);
        Assert.Contains("unverified", runs.Summary);
        Assert.Equal("Novalis", h.Engine.View.UpcomingSplit);
    }

    [Fact]
    public async Task APlanetTheRouteFileDoesNotCoverNeverSplits()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered);
        await h.ReadyAsync();
        h.Options.PlanetRoute = true;

        h.Engine.Handle(Split(PlanetEntered.Code, 250));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.False(h.Engine.Log()[^1].Acted);
    }

    // ---------------------------------------------------------------- start, reset, pause

    [Fact]
    public async Task StartStartsATimerThatIsNotRunningAndLeavesOneThatIs()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered);
        await h.ReadyAsync();

        h.Engine.Handle(new AutosplitEvent(1, 10, AutosplitKind.Start, 0, 0));
        Assert.True(await h.Sent(LiveSplitClient.StartTimer));

        // The engine re-asks after every command, so it now knows the timer is running.
        Assert.True(await WaitFor(() => h.Engine.View.Phase == LiveSplitPhase.Running));
        h.Server.ClearCommands();

        h.Engine.Handle(new AutosplitEvent(2, 20, AutosplitKind.Start, 0, 0));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.Contains("the timer is Running", h.Engine.Log()[^1].Action);
    }

    [Fact]
    public async Task ResetThenStartInTheSameBurstStartsTheNewRun()
    {
        // The console sends RESET and START back to back on a new game. The read-back after the
        // reset is asynchronous, so the engine has to assume the reset landed or the START that
        // follows a millisecond later would still see the old run as Running.
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered);
        await h.ReadyAsync();

        h.Engine.Handle(new AutosplitEvent(1, 10, AutosplitKind.Start, 0, 0));
        Assert.True(await h.Sent(LiveSplitClient.StartTimer));
        Assert.True(await WaitFor(() => h.Engine.View.Phase == LiveSplitPhase.Running));
        h.Server.ClearCommands();

        h.Engine.Handle(new AutosplitEvent(2, 20, AutosplitKind.Reset, 0, 0));
        h.Engine.Handle(new AutosplitEvent(3, 20, AutosplitKind.Start, 0, 0));

        Assert.True(await h.Sent(LiveSplitClient.Reset));
        Assert.True(await h.Sent(LiveSplitClient.StartTimer));
    }

    [Fact]
    public async Task EachMasterSwitchStopsItsOwnKindOfEvent()
    {
        using var h = new Harness(GameId.Rac4, new[] { "Dread Zone", "Catacrom" }, PlanetEntered, BossDefeated);
        await h.ReadyAsync();

        h.Engine.Handle(new AutosplitEvent(1, 10, AutosplitKind.Reset, 0, 0));
        Assert.True(await h.Sent(LiveSplitClient.Reset));

        // Reset off is the All Exterminator Cards case the old script called AEC.
        h.Server.ClearCommands();
        h.Options.Reset = false;
        h.Engine.Handle(new AutosplitEvent(2, 20, AutosplitKind.Reset, 0, 0));
        await Task.Delay(200);
        Assert.Empty(h.Server.Actions);
        Assert.Contains("resetting the timer is switched off", h.Engine.Log()[^1].Action);

        h.Options.Start = false;
        h.Engine.Handle(new AutosplitEvent(3, 30, AutosplitKind.Start, 0, 0));
        await Task.Delay(200);
        Assert.Empty(h.Server.Actions);
        Assert.Contains("starting the timer is switched off", h.Engine.Log()[^1].Action);

        h.Options.Split = false;
        h.Engine.Handle(Split(BossDefeated.Code, seq: 4));
        await Task.Delay(200);
        Assert.Empty(h.Server.Actions);
        Assert.Contains("splitting is switched off", h.Engine.Log()[^1].Action);

        // Four events in, and only the first reset ever reached LiveSplit.
        Assert.Equal(4, h.Engine.Received);
        Assert.Equal(1, h.Engine.Acted);
    }

    [Fact]
    public async Task PauseAndResumeAreSentStraightThrough()
    {
        using var h = new Harness(GameId.Rac4, new[] { "Dread Zone", "Catacrom" });
        await h.ReadyAsync();

        h.Engine.Handle(new AutosplitEvent(1, 10, AutosplitKind.Pause, 0, 0));
        Assert.True(await h.Sent(LiveSplitClient.Pause));

        h.Engine.Handle(new AutosplitEvent(2, 20, AutosplitKind.Resume, 0, 0));
        Assert.True(await h.Sent(LiveSplitClient.Resume));
        Assert.Equal(2, h.Engine.Acted);
    }

    // ---------------------------------------------------------------- game time

    [Fact]
    public async Task AFlatSplitRowTakesItsTimeOffBeforeItSplits()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, FlatBoss);
        await h.ReadyAsync();

        h.Engine.Handle(Split(FlatBoss.Code));
        Assert.True(await h.Sent(LiveSplitClient.Split));

        // The order is the whole point: the old script subtracted its seven frames and only then
        // returned true, so LiveSplit's split time already has the correction in it.
        var actions = h.Server.Actions;
        int adjusted = Array.FindIndex(actions, a => a.StartsWith(LiveSplitClient.AddLoadingTimes, StringComparison.Ordinal));
        int split = Array.IndexOf(actions, LiveSplitClient.Split);
        Assert.True(adjusted >= 0, "no addloadingtimes was sent");
        Assert.True(adjusted < split, $"the correction has to come first, got {string.Join(", ", actions)}");

        Assert.Equal(new[] { 0.116667 }, h.Server.LoadingTimeSeconds);
        Assert.Equal(1, h.Engine.Adjustments);
        Assert.Equal(1, h.Engine.Acted);
    }

    [Fact]
    public async Task AFlatLoadRowTakesItsTimeOffWhenTheLoadStarts()
    {
        using var h = new Harness(GameId.Rac3, new[] { "Veldin", "Florana" }, PlanetEntered, FlatLoad);
        await h.ReadyAsync();

        h.Engine.Handle(new AutosplitEvent(1, 4_000, AutosplitKind.LoadStart, FlatLoad.Code, 0));

        Assert.True(await WaitFor(() => h.Server.LoadingTimes.Count == 1));
        Assert.Equal(new[] { 1.0 }, h.Server.LoadingTimeSeconds);
        Assert.DoesNotContain(LiveSplitClient.Split, h.Server.Actions);
    }

    [Fact]
    public async Task ANormalisedLoadGivesBackOnlyWhatItRanOver()
    {
        using var h = new Harness(GameId.Rac1, new[] { "Veldin", "Novalis" }, PlanetEntered, NormalisedLoad);
        await h.ReadyAsync();

        // Nine seconds of load against RaC1's 7.56 s allowance: 1.44 s comes off game time.
        h.Engine.Handle(new AutosplitEvent(1, 10_000, AutosplitKind.LoadStart, NormalisedLoad.Code, 0));
        h.Engine.Handle(new AutosplitEvent(2, 19_000, AutosplitKind.LoadEnd, NormalisedLoad.Code, 0));

        Assert.True(await WaitFor(() => h.Server.LoadingTimes.Count == 1));
        Assert.Equal(new[] { 1.44 }, h.Server.LoadingTimeSeconds);
        Assert.Contains("1.44", h.Engine.Log()[^1].Action);
        Assert.Equal(1, h.Engine.Adjustments);
    }

    [Fact]
    public async Task ALoadInsideItsAllowanceCostsTheRunNothing()
    {
        using var h = new Harness(GameId.Rac1, new[] { "Veldin", "Novalis" }, PlanetEntered, NormalisedLoad);
        await h.ReadyAsync();

        h.Engine.Handle(new AutosplitEvent(1, 1_000, AutosplitKind.LoadStart, NormalisedLoad.Code, 0));
        h.Engine.Handle(new AutosplitEvent(2, 5_000, AutosplitKind.LoadEnd, NormalisedLoad.Code, 0));
        await Task.Delay(200);

        Assert.Empty(h.Server.LoadingTimes);
        Assert.Equal(0, h.Engine.Adjustments);
        Assert.Contains("within its", h.Engine.Log()[^1].Action);
    }

    [Fact]
    public async Task DeadlockedsQuitIsNormalisedTheSameWayAndNeverPausesTheTimer()
    {
        using var h = new Harness(GameId.Rac4, new[] { "Dread Zone", "Catacrom" }, PlanetEntered, NormalisedPause);
        await h.ReadyAsync();

        // Twenty seconds in the XMB against the old script's 14.8 s: 5.2 s comes off.
        h.Engine.Handle(new AutosplitEvent(1, 1_000, AutosplitKind.Pause, NormalisedPause.Code, 0));
        h.Engine.Handle(new AutosplitEvent(2, 21_000, AutosplitKind.Resume, NormalisedPause.Code, 0));

        Assert.True(await WaitFor(() => h.Server.LoadingTimes.Count == 1));
        Assert.Equal(new[] { 5.2 }, h.Server.LoadingTimeSeconds);

        // Never the timer: game time is corrected, the clock is not stopped and not set.
        Assert.DoesNotContain(LiveSplitClient.Pause, h.Server.Actions);
        Assert.DoesNotContain(LiveSplitClient.Resume, h.Server.Actions);
        Assert.DoesNotContain(h.Server.Actions, a => a.StartsWith("setgametime", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Server.Actions, a => a.StartsWith("pausegametime", StringComparison.Ordinal));

        // A short quit gives nothing back, the same way a short load does not.
        h.Server.ClearCommands();
        h.Engine.Handle(new AutosplitEvent(3, 30_000, AutosplitKind.Pause, NormalisedPause.Code, 0));
        h.Engine.Handle(new AutosplitEvent(4, 32_000, AutosplitKind.Resume, NormalisedPause.Code, 0));
        await Task.Delay(200);
        Assert.Empty(h.Server.LoadingTimes);
    }

    [Fact]
    public async Task TheCorrectionsHappenWhateverTheCheckboxesSay()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, FlatBoss);
        await h.ReadyAsync();

        // Splitting off entirely, and the row's own checkbox off with it.
        h.Options.Split = false;
        h.Options.SetEvent(FlatBoss.Label, false);

        h.Engine.Handle(Split(FlatBoss.Code));

        Assert.True(await WaitFor(() => h.Server.LoadingTimes.Count == 1));
        Assert.Equal(new[] { 0.116667 }, h.Server.LoadingTimeSeconds);
        Assert.DoesNotContain(LiveSplitClient.Split, h.Server.Actions);

        // It still counts as having acted: a command went out for that event.
        Assert.Equal(1, h.Engine.Acted);
    }

    [Fact]
    public async Task AModuleClockThatWrapsStillMeasuresTheLoad()
    {
        using var h = new Harness(GameId.Rac1, new[] { "Veldin", "Novalis" }, PlanetEntered, NormalisedLoad);
        await h.ReadyAsync();

        // 49 days in, the millisecond counter rolls over in the middle of a nine-second load.
        h.Engine.Handle(new AutosplitEvent(1, uint.MaxValue - 3_999, AutosplitKind.LoadStart, NormalisedLoad.Code, 0));
        h.Engine.Handle(new AutosplitEvent(2, 5_000, AutosplitKind.LoadEnd, NormalisedLoad.Code, 0));

        Assert.True(await WaitFor(() => h.Server.LoadingTimes.Count == 1));
        Assert.Equal(new[] { 1.44 }, h.Server.LoadingTimeSeconds);
    }

    [Fact]
    public void TheTimeStringIsWhatLiveSplitParses()
    {
        // Confirmed against LiveSplit's own server: a bare number is read as seconds, and six
        // decimals carry a microsecond without rounding.
        Assert.Equal("0.116667", LiveSplitClient.FormatTime(116_667));
        Assert.Equal("1.440000", LiveSplitClient.FormatTime(1_440_000));
        Assert.Equal("7.560000", LiveSplitClient.FormatTime(7_560_000));
        Assert.Equal("addloadingtimes 1.440000", LiveSplitClient.AddLoadingTimesCommand(1_440_000));

        // Never negative: a correction only ever takes time off a run.
        Assert.Equal("0.000000", LiveSplitClient.FormatTime(-500_000));
    }

    // ---------------------------------------------------------------- when nothing should happen

    [Fact]
    public async Task EventsAreIgnoredAndLoggedWhileTheAutosplitterIsOff()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated);
        await h.ReadyAsync();

        h.Settings.Autosplit.Enabled = false;
        h.Engine.Handle(Split(BossDefeated.Code));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.Equal(1, h.Engine.Received);
        Assert.Equal(0, h.Engine.Acted);
        Assert.Contains("the autosplitter is off", h.Engine.Log()[^1].Action);
    }

    [Fact]
    public async Task EventsAreIgnoredAndLoggedWhileLiveSplitIsDisconnected()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated);
        await h.ReadyAsync();

        h.LiveSplit.Stop();
        h.Engine.Handle(Split(BossDefeated.Code));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.Contains("LiveSplit is not connected", h.Engine.Log()[^1].Action);
    }

    [Fact]
    public async Task TheLogKeepsTheLastHundredEventsAndTheirReasons()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated);
        await h.ReadyAsync();
        h.Settings.Autosplit.Enabled = false;

        for (uint i = 1; i <= Autosplitter.LogLength + 20; i++) h.Engine.Handle(Split(BossDefeated.Code, seq: i));

        var log = h.Engine.Log();
        Assert.Equal(Autosplitter.LogLength, log.Length);
        Assert.Equal(21u, log[0].Seq);
        Assert.Equal((uint)(Autosplitter.LogLength + 20), log[^1].Seq);

        h.Engine.ClearLog();
        Assert.Empty(h.Engine.Log());
    }

    [Fact]
    public async Task TheTestButtonsSendOneCommandAndAreLoggedAsManual()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" });
        await h.ReadyAsync();

        h.Engine.SendManual(LiveSplitClient.Split);
        Assert.True(await h.Sent(LiveSplitClient.Split));

        var entry = h.Engine.Log()[^1];
        Assert.Equal("(manual)", entry.Event);
        Assert.Equal(0, h.Engine.Acted);   // a manual press is not a decision about an event
    }

    [Fact]
    public async Task ThePhaseAndBothSplitNamesComeBackFromLiveSplitOnConnect()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" });
        Assert.True(await WaitFor(() => h.LiveSplit.IsConnected));

        // The handshake is one getcurrenttimerphase and nothing else: the real server answers no
        // version query at all, and asking for one is what used to drop the connection.
        Assert.True(await WaitFor(() => h.Server.Commands.Length > 0));
        Assert.Equal(LiveSplitClient.GetCurrentTimerPhase, h.Server.Commands[0]);
        Assert.DoesNotContain(h.Server.Commands, c => c.Contains("version", StringComparison.OrdinalIgnoreCase));

        await h.RefreshAsync();
        Assert.Equal(LiveSplitStatus.Connected, h.LiveSplit.Status);
        Assert.Contains("Connected to LiveSplit", h.LiveSplit.StatusLine);
        Assert.Equal(LiveSplitPhase.NotRunning, h.Engine.View.Phase);
        Assert.Equal("Aranos", h.Engine.View.CurrentSplit);
        Assert.Equal("Oozla", h.Engine.View.UpcomingSplit);
    }

    // ---------------------------------------------------------------- the connection itself

    [Fact]
    public async Task AQueryTheServerIgnoresIsUnknownRatherThanADroppedConnection()
    {
        // LiveSplit's real server answers nothing at all to a command outside its list, which is
        // most of them: getupcomingsplitname and getlivesplitversion included. Waiting a second
        // and then tearing the socket down is what made the link drop seconds after it came up.
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated);
        h.Server.Ignored.Add(LiveSplitClient.GetUpcomingSplitName);
        await h.ReadyAsync();

        // The first refresh spends the timeout finding out; after that the client knows.
        Assert.True(await WaitFor(() => !h.LiveSplit.Answers(LiveSplitClient.GetUpcomingSplitName)),
            "the unanswered query was never noticed");
        await h.RefreshAsync();

        Assert.True(h.LiveSplit.IsConnected);
        Assert.False(h.Engine.View.UpcomingSupported);
        Assert.Equal(LiveSplitPhase.NotRunning, h.Engine.View.Phase);
        Assert.Equal("Aranos", h.Engine.View.CurrentSplit);

        // Asked once, then never again: a query that costs a second is not worth repeating.
        int asked = h.Server.Commands.Count(c => c == LiveSplitClient.GetUpcomingSplitName);
        await h.RefreshAsync();
        await Task.Delay(100);
        Assert.Equal(asked, h.Server.Commands.Count(c => c == LiveSplitClient.GetUpcomingSplitName));

        // And the connection is still good for everything else.
        h.Engine.Handle(Split(BossDefeated.Code));
        Assert.True(await h.Sent(LiveSplitClient.Split));
        Assert.True(h.LiveSplit.IsConnected);
    }

    [Fact]
    public async Task ASurvivingConnectionKeepsWorkingForLongerThanAQueryTimeout()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated);
        h.Server.Ignored.Add(LiveSplitClient.GetUpcomingSplitName);
        await h.ReadyAsync();

        // Three seconds of the 1 Hz poll, which is where the drop always showed itself.
        for (int i = 0; i < 3; i++)
        {
            h.Engine.Tick(1.5);
            await Task.Delay(400);
            Assert.True(h.LiveSplit.IsConnected, $"the connection dropped on poll {i + 1}");
        }

        Assert.Equal(LiveSplitStatus.Connected, h.LiveSplit.Status);
    }

    /// <summary>
    /// Nothing listening is the everyday case: LiveSplit's server is off until somebody starts it.
    /// The client has to report it as a failed attempt rather than sit in "Connecting...", because
    /// that count is what puts the "LiveSplit not found" popup on the screen, and it has to keep
    /// counting so a second attempt the user asked for is told from the first.
    /// </summary>
    [Fact]
    public async Task AConnectToAPortNothingListensOnIsCountedAsAFailedAttempt()
    {
        // A listener started and stopped leaves a port nothing is on, which is exactly the shape
        // of LiveSplit with its server switched off.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var client = new LiveSplitClient();
        Assert.Equal(0, client.ConnectFailures);

        client.Start(LiveSplitClient.DefaultHost, deadPort);

        Assert.True(await WaitFor(() => client.ConnectFailures >= 1), "the refused connection was never reported");
        Assert.False(client.IsConnected);
        Assert.Equal(LiveSplitStatus.Disconnected, client.Status);
        Assert.NotNull(client.LastError);
        Assert.Contains($"Not connected to {LiveSplitClient.DefaultHost}:{deadPort}", client.StatusLine);

        // The reconnect loop keeps trying, and every retry counts: the panel is what decides which
        // of them is worth a popup.
        Assert.True(await WaitFor(() => client.ConnectFailures >= 2), "the reconnect loop stopped retrying");

        client.Stop();
    }

    /// <summary>The other half of it: a connection that comes up is not a failure of any kind.</summary>
    [Fact]
    public async Task AConnectionThatComesUpCountsNoFailure()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" });
        await h.ReadyAsync();

        Assert.Equal(0, h.LiveSplit.ConnectFailures);
    }

    // ---------------------------------------------------------------- the route files

    [Fact]
    public void EveryGameShipsARouteFileWithOneLinePerPlanet()
    {
        // The counts are qwark's planet tables: PLANET_LIST index is the line number.
        Assert.Equal(19, AutosplitRoutes.For(GameId.Rac1).Count);
        Assert.Equal(27, AutosplitRoutes.For(GameId.Rac2).Count);
        Assert.Equal(37, AutosplitRoutes.For(GameId.Rac3).Count);
        Assert.Equal(16, AutosplitRoutes.For(GameId.Rac4).Count);
        Assert.Empty(AutosplitRoutes.Problems);
    }

    [Fact]
    public void NullLinesAreUnusedPlanetsAndNeverMatch()
    {
        // Deadlocked's unused indices and RaC3's two placeholders.
        Assert.False(AutosplitRoutes.Knows(GameId.Rac4, 0));
        Assert.False(AutosplitRoutes.Knows(GameId.Rac4, 3));
        Assert.False(AutosplitRoutes.Knows(GameId.Rac3, 0));
        Assert.False(AutosplitRoutes.Knows(GameId.Rac3, 15));

        Assert.False(AutosplitRoutes.Matches(GameId.Rac4, 0, "Dread Zone"));
        Assert.False(AutosplitRoutes.Matches(GameId.Rac2, 1, null));
        Assert.False(AutosplitRoutes.Matches(GameId.Rac2, 99, "Oozla"));
    }

    [Fact]
    public void TheNameTestIsTheOldScriptsSubstringMatch()
    {
        Assert.True(AutosplitRoutes.Matches(GameId.Rac2, 1, "Oozla"));
        Assert.True(AutosplitRoutes.Matches(GameId.Rac2, 2, "Maktar Resort"));
        Assert.True(AutosplitRoutes.Matches(GameId.Rac2, 14, "Aranos 2 (Clank)"));
        Assert.False(AutosplitRoutes.Matches(GameId.Rac2, 5, "Notak"));

        Assert.True(AutosplitRoutes.Matches(GameId.Rac1, 3, "Kerwan"));
        Assert.True(AutosplitRoutes.Matches(GameId.Rac1, 3, "metropolis"));
        Assert.True(AutosplitRoutes.Matches(GameId.Rac1, 17, "Drek's Fleet"));
        Assert.False(AutosplitRoutes.Matches(GameId.Rac1, 1, "Aridia"));

        Assert.True(AutosplitRoutes.Matches(GameId.Rac3, 3, "Starship Phoenix"));
        Assert.True(AutosplitRoutes.Matches(GameId.Rac3, 26, "Metropolis Rangers"));
        // Deadlocked's list is the old dlplanets.txt verbatim, spellings and all: "dreadzone" has
        // no space in it, so a split called "Dread Zone" is matched by "marauder" and friends.
        Assert.True(AutosplitRoutes.Matches(GameId.Rac4, 1, "DreadZone"));
        Assert.True(AutosplitRoutes.Matches(GameId.Rac4, 1, "Marauder"));
        Assert.False(AutosplitRoutes.Matches(GameId.Rac4, 1, "Dread Zone"));
        Assert.True(AutosplitRoutes.Matches(GameId.Rac4, 13, "Maraxus"));
    }

    // ---------------------------------------------------------------- settings

    [Fact]
    public void AutosplitSettingsRoundTripThroughTheFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "racman-autosplit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "racman-reloaded.settings.json");
            var saved = Settings.Load(path);
            saved.Autosplit.Enabled = true;
            saved.Autosplit.Host = "10.0.0.4";
            saved.Autosplit.Port = 16835;
            saved.Autosplit.SplitsFile = @"C:\splits\GC NG+.lss";
            saved.Autosplit.LiveSplitFolder = @"C:\LiveSplit";

            var rac2 = saved.Autosplit.For(GameId.Rac2);
            rac2.PlanetRoute = true;
            rac2.SetEvent("Planet entered", false);
            rac2.SetEvent("Protopet defeated", true);

            saved.Autosplit.For(GameId.Rac4).Reset = false;
            saved.Save();

            // The two settings this build replaced are not written back out.
            string json = File.ReadAllText(path);
            Assert.DoesNotContain("neverReset", json);
            Assert.DoesNotContain("namesAreDestination", json);

            var loaded = Settings.Load(path);
            Assert.True(loaded.Autosplit.Enabled);
            Assert.Equal("10.0.0.4", loaded.Autosplit.Host);
            Assert.Equal(16835, loaded.Autosplit.Port);
            Assert.Equal(@"C:\splits\GC NG+.lss", loaded.Autosplit.SplitsFile);
            Assert.Equal(@"C:\LiveSplit", loaded.Autosplit.LiveSplitFolder);

            var back = loaded.Autosplit.For(GameId.Rac2);
            Assert.True(back.PlanetRoute);
            Assert.False(back.EventEnabled("Planet entered", byDefault: true));
            Assert.True(back.EventEnabled("Protopet defeated", byDefault: false));

            // The three masters default on, and only the one that was changed is off.
            Assert.True(back.Start);
            Assert.True(back.Split);
            Assert.True(back.Reset);
            Assert.False(loaded.Autosplit.For(GameId.Rac4).Reset);

            // A label the file never mentioned takes the console's default, either way round.
            Assert.True(back.EventEnabled("Maktar arena", byDefault: true));
            Assert.False(back.EventEnabled("Maktar arena", byDefault: false));

            // The per-game entries are keyed by game, and nothing else was invented.
            Assert.Equal(new[] { "rac2", "rac4" }, loaded.Autosplit.Games.Keys.OrderBy(k => k).ToArray());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SettingsFromAnOlderBuildLoadWithTheAutosplitterOff()
    {
        var folder = Path.Combine(Path.GetTempPath(), "racman-autosplit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "old.settings.json");
            File.WriteAllText(path, """{ "lastHost": "192.168.1.50" }""");

            var loaded = Settings.Load(path);
            Assert.False(loaded.Autosplit.Enabled);
            Assert.Equal(LiveSplitClient.DefaultHost, loaded.Autosplit.Host);
            Assert.Equal(LiveSplitClient.DefaultPort, loaded.Autosplit.Port);
            Assert.Empty(loaded.Autosplit.Games);

            // No splits file named is "find LiveSplit's own", which is what it does unconfigured.
            Assert.Equal(string.Empty, loaded.Autosplit.SplitsFile);
            Assert.Equal(string.Empty, loaded.Autosplit.LiveSplitFolder);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ASettingsFileFromTheBuildBeforeTheMastersIsMigratedSilently()
    {
        const string older = """
        {
          "autosplit": {
            "enabled": true,
            "games": {
              "rac4": { "events": { "Planet entered": true }, "planetRoute": true,
                        "namesAreDestination": true, "neverReset": true },
              "rac2": { "events": {}, "planetRoute": false, "neverReset": false }
            }
          }
        }
        """;

        var folder = Path.Combine(Path.GetTempPath(), "racman-autosplit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "racman-reloaded.settings.json");
            File.WriteAllText(path, older);

            var loaded = Settings.Load(path);

            // "never reset" was the Reset master turned off, and nothing else moved.
            var rac4 = loaded.Autosplit.For(GameId.Rac4);
            Assert.False(rac4.Reset);
            Assert.True(rac4.Start);
            Assert.True(rac4.Split);
            Assert.True(rac4.PlanetRoute);
            Assert.True(rac4.EventEnabled("Planet entered", byDefault: false));

            var rac2 = loaded.Autosplit.For(GameId.Rac2);
            Assert.True(rac2.Reset);

            // Neither retired setting survives the next save.
            loaded.Save();
            string json = File.ReadAllText(path);
            Assert.DoesNotContain("neverReset", json);
            Assert.DoesNotContain("namesAreDestination", json);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ALabelThisBuildNoLongerKnowsIsKeptInTheFile()
    {
        var settings = new Settings();
        var game = settings.Autosplit.For(GameId.Rac3);
        game.SetEvent("Something qwark retired", true);
        game.SetEvent("Planet entered", false);

        var json = JsonSerializer.Serialize(settings.Autosplit);
        var back = JsonSerializer.Deserialize<AutosplitSettings>(json)!;

        Assert.Contains("Something qwark retired", back.For(GameId.Rac3).Events.Keys);
        Assert.False(back.For(GameId.Rac3).EventEnabled("Planet entered", byDefault: true));
    }
}
