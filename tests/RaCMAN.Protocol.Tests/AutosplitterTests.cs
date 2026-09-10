using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using RaCMAN.App;
using RaCMAN.App.Panels;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Stands in for the LiveSplit development build's TCP server: it records the commands it is given,
/// answers the get* queries out of a scripted split list, and moves its own phase, split index and
/// clocks the way LiveSplit would, so the engine's "ask again after every command" behaviour is
/// exercised and its arithmetic is checked against a timer that behaves like the real one.
/// <para>
/// Two of LiveSplit's own habits are modelled on purpose, because the client is wrong without them:
/// a run's game time is null until something sets it (see <see cref="GameTime"/>), and a command
/// whose argument is missing throws inside the server and takes the server with it (see
/// <see cref="Fault"/>).
/// </para>
/// </summary>
internal sealed class FakeLiveSplitServer : IDisposable
{
    /// <summary>The commands whose argument the real server hands straight to a parser.</summary>
    private static readonly HashSet<string> NeedArgument = new(StringComparer.Ordinal)
    {
        LiveSplitClient.SetGameTime, LiveSplitClient.AddLoadingTimes,
        "setloadingtimes", "getsplitname",
    };

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _commands = new();
    private readonly object _gate = new();

    /// <summary>
    /// The run's loading times, which is what game time is measured against: while game time runs
    /// it is real time less this. Null until a loading-times or a game-time command creates one,
    /// which is where every LiveSplit run starts and why <c>addloadingtimes 0</c> is enough to give
    /// a run a game time in place, equal to real time and with nothing read back.
    /// </summary>
    private TimeSpan? _loadingTimes;

    /// <summary>What the pause froze, when there was a game time to freeze.</summary>
    private TimeSpan? _frozenGameTime;

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

    /// <summary>What <c>getlivesplitversion</c> answers, as the development build words it.</summary>
    public string Version { get; set; } = "1.8.37-57";

    /// <summary>
    /// The run's real time. It does not tick: a test says what the wall clock reads, and moving it
    /// on is how a test spends time in the XMB.
    /// </summary>
    public TimeSpan RealTime { get; set; }

    /// <summary>
    /// The run's game time, or null while the run has none — which is where LiveSplit starts, and
    /// the whole reason the quit sends <c>addloadingtimes 0</c> before it stops the clock. Setting
    /// this from a test is the same move <c>setgametime</c> makes.
    /// </summary>
    public TimeSpan? GameTime
    {
        get { lock (_gate) return CurrentGameTime(); }
        set
        {
            lock (_gate)
            {
                if (value is null)
                {
                    _loadingTimes = null;
                    _frozenGameTime = null;
                    return;
                }

                Write(value.Value);
            }
        }
    }

    /// <summary>Whether <c>pausegametime</c> has stopped the clock, as LiveSplit would have it.</summary>
    public bool GameTimePaused { get; set; }

    /// <summary>Every <c>setgametime</c> line, argument included, in the order it arrived.</summary>
    public List<string> GameTimesSet { get; } = new();

    /// <summary>
    /// What killed this server, if anything. LiveSplit does not trim the line before it hands the
    /// rest to its own parsers, so <c>setgametime</c> with nothing after it — or
    /// <c>getsplitname</c> with no index — throws inside the server and takes LiveSplit down. This
    /// does the same, so a client that ever builds one of those loses the connection in a test
    /// rather than the timer on somebody's stream.
    /// </summary>
    public string? Fault { get; private set; }

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
            GameTimesSet.Clear();
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
            string verb = command.Split(' ')[0];
            int space = command.IndexOf(' ');
            string argument = space < 0 ? string.Empty : command[(space + 1)..];

            if (NeedArgument.Contains(verb) && argument.Trim().Length == 0)
            {
                Fault = $"\"{command}\" threw inside LiveSplit: no argument to parse";
                throw new InvalidOperationException(Fault);
            }

            switch (verb)
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

                // Loading times are the other half of LiveSplit's game time, and the half that
                // creates one: LoadingTimes is null until a write like this lands, and from then
                // on game time is real time less it. So a correction takes time off the clock,
                // and adding nothing at all still leaves the run with a game time where it had
                // none. While game time is stopped the frozen value is what the timer shows and
                // the unpause recomputes the loading times from it, exactly as LiveSplit does.
                case LiveSplitClient.AddLoadingTimes:
                    LoadingTimes.Add(command);
                    _loadingTimes = (_loadingTimes ?? TimeSpan.Zero) + ParseSeconds(argument);
                    return null;

                // Game time the way LiveSplitState keeps it. Stopping it freezes a clock that
                // exists and does nothing at all to one that does not: while the run's game time
                // is null the timer answers real time, pause or no pause.
                case LiveSplitClient.PauseGameTime:
                    GameTimePaused = true;
                    if (_loadingTimes is { } loading) _frozenGameTime = RealTime - loading;
                    return null;

                case LiveSplitClient.UnpauseGameTime:
                    GameTimePaused = false;
                    if (_frozenGameTime is { } frozen)
                    {
                        _loadingTimes = RealTime - frozen;
                        _frozenGameTime = null;
                    }

                    return null;

                // The one command that brings game time into existence, whatever it is set to.
                case LiveSplitClient.SetGameTime:
                    GameTimesSet.Add(command);
                    Write(TimeSpan.Parse(argument, System.Globalization.CultureInfo.InvariantCulture));
                    return null;

                // The development build formats this with its PreciseTimeFormatter, which is a
                // TimeSpan's own text.
                case LiveSplitClient.GetCurrentGameTime:
                    return CurrentGameTime().ToString();

                case LiveSplitClient.GetLiveSplitVersion:
                    return Version;

                case LiveSplitClient.GetSplitIndex:
                    return SplitIndex.ToString();

                case LiveSplitClient.GetCurrentSplitName:
                    return NameAt(SplitIndex);

                case LiveSplitClient.GetUpcomingSplitName:
                    return NameAt(SplitIndex + 1);

                case LiveSplitClient.Ping:
                    return "pong";

                default:
                    return null;
            }
        }
    }

    private string NameAt(int index) => index >= 0 && index < Splits.Length ? Splits[index] : string.Empty;

    /// <summary>A time argument as the server's own parser reads a bare number: seconds.</summary>
    private static TimeSpan ParseSeconds(string argument) =>
        TimeSpan.FromSeconds(double.Parse(argument, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Game time as the server would report it right now, real time while it has none.</summary>
    private TimeSpan CurrentGameTime()
    {
        if (_frozenGameTime is { } frozen) return frozen;
        if (_loadingTimes is { } loading) return RealTime - loading;
        return RealTime;
    }

    /// <summary>
    /// What <c>SetGameTime</c> does: the run has a game time from here on, at this value. LiveSplit
    /// writes it as loading times, and as the pause value too while game time is stopped.
    /// </summary>
    private void Write(TimeSpan value)
    {
        _loadingTimes = RealTime - value;
        if (GameTimePaused) _frozenGameTime = value;
    }

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
public class AutosplitterTests
{
    private static readonly AutosplitEventDesc PlanetEntered = new(
        1, AutosplitKind.Split, AutosplitEventFlags.EnabledByDefault | AutosplitEventFlags.PlanetRoute,
        0, "Planet entered");

    private static readonly AutosplitEventDesc BossDefeated = new(
        2, AutosplitKind.Split, AutosplitEventFlags.EnabledByDefault, 0, "Protopet defeated");

    /// <summary>Deadlocked's second split row, so its table is the one the console describes.</summary>
    private static readonly AutosplitEventDesc VoxDefeated = new(
        2, AutosplitKind.Split, AutosplitEventFlags.EnabledByDefault, 0, "Vox defeated");

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

    // ---------------------------------------------------------------- what the panel offers

    [Fact]
    public void OnlyAGameThatReportsAPausePairGetsThePauseSwitch()
    {
        // Deadlocked's table: the quit to the XMB is the one thing in any of the four games that
        // stops the clock, so it is the only one the panel has a Pause box for.
        Assert.True(AutosplitterPanel.HasPause(new[] { PlanetEntered, VoxDefeated, NormalisedPause }));

        // RaC1, RaC2 and UYA report splits and loads and nothing else.
        Assert.False(AutosplitterPanel.HasPause(new[] { PlanetEntered, FlatBoss, NormalisedLoad }));
        Assert.False(AutosplitterPanel.HasPause(Array.Empty<AutosplitEventDesc>()));
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
        {
            Server = new FakeLiveSplitServer(splits);
            Settings = new Settings();
            Settings.Autosplit.Enabled = true;

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

    /// <summary>
    /// Deadlocked with the route on, which is the case the user runs with. The run is on Catacrom
    /// and going to Sarathos, so a planet event for Catacrom is off route and one for Sarathos is
    /// the split. The descriptors are Deadlocked's own, straight off the wire.
    /// </summary>
    [Fact]
    public async Task DeadlockedsRouteSplitsOnlyOnTheUpcomingPlanet()
    {
        using var h = new Harness(GameId.Rac4, new[] { "Dread Zone", "Catacrom", "Sarathos" },
            PlanetEntered, VoxDefeated);
        await h.ReadyAsync();
        h.Options.PlanetRoute = true;

        // Split 1 is running, so the split just ahead of it is Sarathos.
        h.Server.SplitIndex = 1;
        await h.RefreshAsync();
        Assert.Equal("Sarathos", h.Engine.View.UpcomingSplit);

        // Catacrom is planet 2, and the run is not going there.
        h.Engine.Handle(Split(PlanetEntered.Code, 2));
        await Task.Delay(200);
        Assert.Empty(h.Server.Actions);
        Assert.Contains("not on this planet's route", h.Engine.Log()[^1].Action);

        // Sarathos is planet 4, and rac4-planets.txt calls it "sara".
        h.Engine.Handle(Split(PlanetEntered.Code, 4, seq: 2));
        Assert.True(await h.Sent(LiveSplitClient.Split));
        Assert.Contains("Sarathos", h.Engine.Log()[^1].Action);
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

    /// <summary>
    /// A route line of <c>null</c> is "no names", so the index it covers never matches a split
    /// name. Deadlocked's unused planets are those, and so is index 0, which is the main menu.
    /// </summary>
    [Fact]
    public async Task ARouteLineOfNullNeverMatchesASplitName()
    {
        using var h = new Harness(GameId.Rac4, new[] { "Dread Zone", "Catacrom" }, PlanetEntered);
        await h.ReadyAsync();
        h.Options.PlanetRoute = true;

        Assert.True(AutosplitRoutes.Unused(GameId.Rac4, 3));
        Assert.False(AutosplitRoutes.Knows(GameId.Rac4, 3));

        h.Engine.Handle(Split(PlanetEntered.Code, 3));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.Contains("not on this planet's route", h.Engine.Log()[^1].Action);
    }

    /// <summary>
    /// The old script's one exception: "Always split on 0, this corresponds to Vox split". Index 0
    /// is not a planet a run passes through, so the route has nothing to compare and the split
    /// stands whatever the upcoming name is.
    /// </summary>
    [Fact]
    public async Task WithTheRouteOnPlanetZeroStillSplits()
    {
        using var h = new Harness(GameId.Rac4, new[] { "Dread Zone", "Catacrom" }, PlanetEntered);
        await h.ReadyAsync();
        h.Options.PlanetRoute = true;

        // The upcoming split is Catacrom, which planet 0 is emphatically not.
        Assert.Equal("Catacrom", h.Engine.View.UpcomingSplit);

        h.Engine.Handle(Split(PlanetEntered.Code, 0));

        Assert.True(await h.Sent(LiveSplitClient.Split));
        Assert.Contains("planet 0", h.Engine.Log()[^1].Action);
    }

    /// <summary>
    /// The bug the user hit on hardware. The app hands the engine the game the console last
    /// described, and that goes to None every time the description is re-read — a quit to the XMB,
    /// a re-describe, the seconds after connecting. The engine used to follow it, read a per-game
    /// settings entry for "none" that had never been ticked, find the planet route off in it and
    /// split on every planet regardless of the route. It now keeps the last real game.
    /// </summary>
    [Fact]
    public async Task AnUndescribedGameKeepsTheLastGamesRouteAndSettings()
    {
        using var h = new Harness(GameId.Rac4, new[] { "Dread Zone", "Catacrom", "Sarathos" },
            PlanetEntered, VoxDefeated);
        await h.ReadyAsync();
        h.Options.PlanetRoute = true;

        h.Server.SplitIndex = 1;
        await h.RefreshAsync();
        Assert.Equal("Sarathos", h.Engine.View.UpcomingSplit);

        // What the app does around a re-describe: the game goes away and the rows with it.
        h.Engine.Game = GameId.None;
        h.Engine.Descriptors = Array.Empty<AutosplitEventDesc>();

        Assert.Equal(GameId.Rac4, h.Engine.Game);
        Assert.True(h.Engine.GameOptions.PlanetRoute);

        h.Engine.Handle(Split(PlanetEntered.Code, 2));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.Contains("not on this planet's route", h.Engine.Log()[^1].Action);

        // And nothing invented a settings entry for a game that is not one.
        Assert.DoesNotContain("none", h.Settings.Autosplit.Games.Keys);
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

        h.Options.Pause = false;
        h.Engine.Handle(new AutosplitEvent(5, 40, AutosplitKind.Pause, 0, 0));
        await Task.Delay(200);
        Assert.Empty(h.Server.Actions);
        Assert.Contains("pausing is switched off", h.Engine.Log()[^1].Action);

        // Five events in, and only the first reset ever reached LiveSplit.
        Assert.Equal(5, h.Engine.Received);
        Assert.Equal(1, h.Engine.Acted);
    }

    [Fact]
    public async Task PauseAndResumeAreSentStraightThrough()
    {
        // A pause with no normalised row behind it is the timer's own pause, not a game-time one:
        // no game has such a row today, but the path is the one a game without a parameter takes.
        using var h = new Harness(GameId.Rac4, new[] { "Dread Zone", "Catacrom" });
        await h.ReadyAsync();

        h.Engine.Handle(new AutosplitEvent(1, 10, AutosplitKind.Pause, 0, 0));
        Assert.True(await h.Sent(LiveSplitClient.Pause));

        h.Engine.Handle(new AutosplitEvent(2, 20, AutosplitKind.Resume, 0, 0));
        Assert.True(await h.Sent(LiveSplitClient.Resume));
        Assert.Equal(2, h.Engine.Acted);

        Assert.DoesNotContain(LiveSplitClient.PauseGameTime, h.Server.Actions);
        Assert.DoesNotContain(LiveSplitClient.UnpauseGameTime, h.Server.Actions);
        Assert.False(h.Server.GameTimePaused);
        Assert.Empty(h.Server.GameTimesSet);
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

    /// <summary>
    /// Starts a Deadlocked run on the fake timer, so a quit can be driven through it and what came
    /// back checked against the old script's arithmetic. Real time and game time start together,
    /// and a test moves real time on to spend time in the XMB.
    /// </summary>
    /// <param name="gameTimeSeconds">
    /// What the run's game time reads, or null for a run that has none yet — which is where every
    /// LiveSplit run starts, and where <c>getcurrentgametime</c> answers real time instead.
    /// </param>
    private static async Task<Harness> RunningDeadlockedAsync(double realTimeSeconds, double? gameTimeSeconds)
    {
        var h = new Harness(GameId.Rac4, new[] { "Dread Zone", "Catacrom" }, PlanetEntered, NormalisedPause);
        await h.ReadyAsync();

        h.Engine.Handle(new AutosplitEvent(1, 10, AutosplitKind.Start, 0, 0));
        Assert.True(await h.Sent(LiveSplitClient.StartTimer));
        Assert.True(await WaitFor(() => h.Engine.View.Phase == LiveSplitPhase.Running));

        h.Server.RealTime = TimeSpan.FromSeconds(realTimeSeconds);
        h.Server.GameTime = gameTimeSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
        h.Server.ClearCommands();
        return h;
    }

    /// <summary>
    /// The old script's <c>isLoading</c>: game time stops for the quit and 14.8 s goes on at the
    /// resume, whatever the quit really took. Five seconds in the XMB here.
    /// </summary>
    [Fact]
    public async Task DeadlockedsQuitStopsGameTimeAndTheResumePaysTheFixedTime()
    {
        using var h = await RunningDeadlockedAsync(600, 600);

        h.Engine.Handle(new AutosplitEvent(2, 1_000, AutosplitKind.Pause, NormalisedPause.Code, 0));
        Assert.True(await h.Sent(LiveSplitClient.PauseGameTime));
        await Task.Delay(200);

        // Make sure there is a game time, then stop it. The first command adds nothing to the
        // loading times and so moves nothing: it is there because a run whose game time does not
        // exist yet has nothing for the pause to freeze. Nothing is read and nothing is rewound.
        Assert.Equal(new[] { LiveSplitClient.InitialiseGameTime, LiveSplitClient.PauseGameTime },
            h.Server.Actions);

        var quit = h.Server.Commands;
        int made = Array.IndexOf(quit, LiveSplitClient.InitialiseGameTime);
        int stopped = Array.IndexOf(quit, LiveSplitClient.PauseGameTime);
        Assert.True(made >= 0 && made < stopped,
            $"the quit has to make a game time and only then pause, got {string.Join(", ", quit)}");
        Assert.DoesNotContain(LiveSplitClient.GetCurrentGameTime, quit);

        Assert.True(h.Server.GameTimePaused);
        Assert.Equal(TimeSpan.FromSeconds(600), h.Server.GameTime!.Value);
        Assert.Contains(LiveSplitClient.PauseGameTime, h.Engine.Log()[^1].Action);

        // Five seconds in the XMB, which real time keeps and game time must not.
        h.Server.RealTime = TimeSpan.FromSeconds(605);
        h.Engine.Handle(new AutosplitEvent(3, 6_000, AutosplitKind.Resume, NormalisedPause.Code, 0));
        Assert.True(await h.Sent(LiveSplitClient.UnpauseGameTime));

        // Read the frozen clock, write it back 14.8 s later, then let it run: 10:00 becomes 10:14.8.
        var commands = h.Server.Commands;
        int read = Array.LastIndexOf(commands, LiveSplitClient.GetCurrentGameTime);
        int set = Array.FindLastIndex(commands, c => c.StartsWith(LiveSplitClient.SetGameTime, StringComparison.Ordinal));
        int unpause = Array.IndexOf(commands, LiveSplitClient.UnpauseGameTime);
        Assert.True(read >= 0 && read < set && set < unpause,
            $"the resume has to read, set and only then unpause, got {string.Join(", ", commands)}");

        Assert.Equal(TimeSpan.FromSeconds(614.8), h.Server.GameTime!.Value);
        Assert.False(h.Server.GameTimePaused);

        // One write, at the resume: the quit no longer has to rewind the clock to itself first.
        Assert.Equal(new[] { "setgametime 0:10:14.8000000" }, h.Server.GameTimesSet.ToArray());

        // No correction of any length went out, only the zero that makes a game time exist, and
        // never the timer's own pause: the phase stayed Running.
        Assert.Equal(new[] { 0.0 }, h.Server.LoadingTimeSeconds);
        Assert.DoesNotContain(LiveSplitClient.Pause, h.Server.Actions);
        Assert.DoesNotContain(LiveSplitClient.Resume, h.Server.Actions);
        Assert.Equal("Running", h.Server.Phase);
        Assert.Equal(LiveSplitPhase.Running, h.Engine.View.Phase);
        Assert.Null(h.Server.Fault);

        Assert.Equal(1, h.Engine.Adjustments);
        Assert.Contains("then unpausegametime", h.Engine.Log()[^1].Action);
    }

    /// <summary>
    /// The point of the whole thing: a long quit costs the run exactly what a short one does. The
    /// old script never measured the quit, and neither does this.
    /// </summary>
    [Fact]
    public async Task ALongQuitCostsTheRunTheSameFixedTimeAsAShortOne()
    {
        using var h = await RunningDeadlockedAsync(600, 600);

        h.Engine.Handle(new AutosplitEvent(2, 1_000, AutosplitKind.Pause, NormalisedPause.Code, 0));
        Assert.True(await h.Sent(LiveSplitClient.PauseGameTime));

        // Thirty seconds in the XMB, and the run still pays 14.8 s of game time for it.
        h.Server.RealTime = TimeSpan.FromSeconds(630);
        h.Engine.Handle(new AutosplitEvent(3, 31_000, AutosplitKind.Resume, NormalisedPause.Code, 0));
        Assert.True(await h.Sent(LiveSplitClient.UnpauseGameTime));

        Assert.Equal(TimeSpan.FromSeconds(614.8), h.Server.GameTime!.Value);
        Assert.Equal(new[] { 0.0 }, h.Server.LoadingTimeSeconds);
        Assert.Null(h.Server.Fault);
    }

    /// <summary>
    /// The case the ordering exists for. A run that has not had its game time set yet has none at
    /// all: LiveSplit answers real time and stopping game time freezes nothing. The zero write
    /// before the pause is what makes the freeze real, so the resume reads where the player left
    /// rather than where the wall clock has got to, and the quit still costs 14.8 s. Skip it and
    /// this test reads 30 s of XMB into the run.
    /// </summary>
    [Fact]
    public async Task AQuitBeforeAnythingHasSetGameTimeStillCostsExactlyTheFixedTime()
    {
        using var h = await RunningDeadlockedAsync(600, gameTimeSeconds: null);

        // Nothing has set game time, so the timer answers real time to anyone who asks.
        Assert.Equal(TimeSpan.FromSeconds(600), h.Server.GameTime!.Value);

        h.Engine.Handle(new AutosplitEvent(2, 1_000, AutosplitKind.Pause, NormalisedPause.Code, 0));
        Assert.True(await h.Sent(LiveSplitClient.PauseGameTime));
        Assert.Equal(new[] { LiveSplitClient.InitialiseGameTime, LiveSplitClient.PauseGameTime },
            h.Server.Actions);

        // The zero moved nothing: game time is still the clock the player left, and it exists now.
        Assert.Equal(TimeSpan.FromSeconds(600), h.Server.GameTime!.Value);

        // Half a minute in the XMB. Game time is a real value now and frozen, so it does not move.
        h.Server.RealTime = TimeSpan.FromSeconds(630);
        Assert.Equal(TimeSpan.FromSeconds(600), h.Server.GameTime!.Value);

        h.Engine.Handle(new AutosplitEvent(3, 31_000, AutosplitKind.Resume, NormalisedPause.Code, 0));
        Assert.True(await h.Sent(LiveSplitClient.UnpauseGameTime));

        Assert.Equal(TimeSpan.FromSeconds(614.8), h.Server.GameTime!.Value);
        Assert.Equal(new[] { "setgametime 0:10:14.8000000" }, h.Server.GameTimesSet.ToArray());
        Assert.Equal(new[] { 0.0 }, h.Server.LoadingTimeSeconds);
        Assert.Null(h.Server.Fault);
    }

    /// <summary>
    /// A build that will not say what the clock reads cannot have time put back on it — nothing
    /// here invents a value, and <c>setgametime</c> with no argument would take LiveSplit down. The
    /// resume says so in the log, and game time is never left stopped. The quit itself no longer
    /// asks LiveSplit anything, so there is nothing there to go wrong.
    /// </summary>
    [Fact]
    public async Task AQuitWhoseClockCannotBeReadIsLoggedAsAnErrorAndStillUnpaused()
    {
        using var h = await RunningDeadlockedAsync(600, 600);
        h.Server.Ignored.Add(LiveSplitClient.GetCurrentGameTime);

        h.Engine.Handle(new AutosplitEvent(2, 1_000, AutosplitKind.Pause, NormalisedPause.Code, 0));
        Assert.True(await h.Sent(LiveSplitClient.PauseGameTime));
        Assert.DoesNotContain("error", h.Engine.Log()[^1].Action);

        h.Engine.Handle(new AutosplitEvent(3, 6_000, AutosplitKind.Resume, NormalisedPause.Code, 0));
        Assert.True(await h.Sent(LiveSplitClient.UnpauseGameTime));
        Assert.Contains("error", h.Engine.Log()[^1].Action);

        // Nothing was guessed at: no clock written, no correction, and the timer runs again.
        Assert.Empty(h.Server.GameTimesSet);
        Assert.Equal(new[] { 0.0 }, h.Server.LoadingTimeSeconds);
        Assert.False(h.Server.GameTimePaused);
        Assert.Equal(0, h.Engine.Adjustments);
        Assert.Null(h.Server.Fault);
        Assert.True(h.LiveSplit.IsConnected);
    }

    /// <summary>
    /// The Pause switch. A category that does not charge for a quit wants none of it: game time is
    /// never frozen, the resume pays no penalty, and both halves say so in the log.
    /// </summary>
    [Fact]
    public async Task WithPausingSwitchedOffAQuitCostsTheRunNothingAndSendsNothing()
    {
        using var h = await RunningDeadlockedAsync(600, 600);
        h.Options.Pause = false;

        h.Engine.Handle(new AutosplitEvent(2, 1_000, AutosplitKind.Pause, NormalisedPause.Code, 0));
        await Task.Delay(200);
        Assert.Empty(h.Server.Actions);
        Assert.Contains("ignored: pausing is switched off", h.Engine.Log()[^1].Action);
        Assert.False(h.Server.GameTimePaused);

        // Half a minute in the XMB, and the resume adds nothing to anything.
        h.Server.RealTime = TimeSpan.FromSeconds(630);
        h.Engine.Handle(new AutosplitEvent(3, 31_000, AutosplitKind.Resume, NormalisedPause.Code, 0));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.Contains("ignored: pausing is switched off", h.Engine.Log()[^1].Action);
        Assert.Empty(h.Server.GameTimesSet);
        Assert.Empty(h.Server.LoadingTimes);
        Assert.False(h.Server.GameTimePaused);
        Assert.Equal(0, h.Engine.Adjustments);

        // The start that opened the run is the only event this engine has ever acted on.
        Assert.Equal(1, h.Engine.Acted);
        Assert.Null(h.Server.Fault);
    }

    /// <summary>
    /// The switch is about the quit, not about the run: a start still gives the run a game time,
    /// so turning pausing back on mid-run has something real to freeze.
    /// </summary>
    [Fact]
    public async Task WithPausingSwitchedOffTheStartStillGivesTheRunAGameTime()
    {
        using var h = new Harness(GameId.Rac4, new[] { "Dread Zone", "Catacrom" }, PlanetEntered, NormalisedPause);
        await h.ReadyAsync();
        h.Options.Pause = false;
        h.Server.RealTime = TimeSpan.FromSeconds(12);

        h.Engine.Handle(new AutosplitEvent(1, 10, AutosplitKind.Start, 0, 0));
        Assert.True(await h.Sent(LiveSplitClient.InitialiseGameTime));

        Assert.Equal(new[] { LiveSplitClient.StartTimer, LiveSplitClient.InitialiseGameTime },
            h.Server.Actions);
        Assert.Equal(TimeSpan.FromSeconds(12), h.Server.GameTime!.Value);
    }

    /// <summary>
    /// A RESUME with no PAUSE of its own — the client connected in the middle of a quit, or the
    /// pair went with a different game. Nothing is added to game time, because this engine never
    /// stopped it and does not know what it would be adding to, but the unpause goes out all the
    /// same: it does nothing when nothing is paused, and it is the only thing that can rescue a
    /// run whose clock somebody else froze. A frozen game time is the one state to never leave.
    /// </summary>
    [Fact]
    public async Task AResumeWithNoQuitBehindItStillUnpausesGameTime()
    {
        using var h = await RunningDeadlockedAsync(600, 600);

        h.Engine.Handle(new AutosplitEvent(2, 6_000, AutosplitKind.Resume, NormalisedPause.Code, 0));
        Assert.True(await h.Sent(LiveSplitClient.UnpauseGameTime));
        await Task.Delay(200);

        Assert.Equal(new[] { LiveSplitClient.UnpauseGameTime }, h.Server.Actions);
        Assert.Equal(TimeSpan.FromSeconds(600), h.Server.GameTime!.Value);
        Assert.Empty(h.Server.GameTimesSet);
        Assert.Contains("no pause of that code was open", h.Engine.Log()[^1].Action);
    }

    // ---------------------------------------------------------------- a quit outlives its session

    /// <summary>
    /// The whole point of this build. A Deadlocked quit takes the console to the XMB and boots the
    /// game again, so the RESUME belongs to a different session, a different generation and, for a
    /// moment, no descriptors at all: the client is told the game is None and then the same game
    /// again, and the AUTOSPLIT_DESCRIBE rows only come back once it is INGAME. The pause has to
    /// survive all of that, or game time stays frozen for the rest of the run.
    /// </summary>
    [Fact]
    public async Task AQuitSurvivesTheSessionGoingAwayAndComingBack()
    {
        using var h = await RunningDeadlockedAsync(600, 600);

        h.Engine.Handle(new AutosplitEvent(2, 1_000, AutosplitKind.Pause, NormalisedPause.Code, 0));
        Assert.True(await h.Sent(LiveSplitClient.PauseGameTime));

        // QUITTING, XMB, BOOTING, INGAME. The descriptors go with the session and are not back yet
        // when the resume lands, so the row that opened the pair is the only one there is.
        h.Engine.Game = GameId.None;
        h.Engine.Descriptors = Array.Empty<AutosplitEventDesc>();
        h.Engine.Game = GameId.Rac4;

        // Thirty seconds of quit and reboot on the wall clock, and the module's own clock kept
        // counting through all of it, which is why the event stamps still line up.
        h.Server.RealTime = TimeSpan.FromSeconds(630);
        h.Engine.Handle(new AutosplitEvent(3, 31_000, AutosplitKind.Resume, NormalisedPause.Code, 0));
        Assert.True(await h.Sent(LiveSplitClient.UnpauseGameTime));

        Assert.Equal(TimeSpan.FromSeconds(614.8), h.Server.GameTime!.Value);
        Assert.Equal(new[] { "setgametime 0:10:14.8000000" }, h.Server.GameTimesSet.ToArray());
        Assert.False(h.Server.GameTimePaused);
        Assert.Equal(1, h.Engine.Adjustments);
        Assert.Contains("then unpausegametime", h.Engine.Log()[^1].Action);
        Assert.Null(h.Server.Fault);
    }

    /// <summary>
    /// The other side of it: a different game is a different run, so nothing of the last one is
    /// still owed. The unpause still goes out, because game time must never be left frozen.
    /// </summary>
    [Fact]
    public async Task ADifferentGameDropsTheQuitAndTheResumeOnlyUnpauses()
    {
        using var h = await RunningDeadlockedAsync(600, 600);

        h.Engine.Handle(new AutosplitEvent(2, 1_000, AutosplitKind.Pause, NormalisedPause.Code, 0));
        Assert.True(await h.Sent(LiveSplitClient.PauseGameTime));
        h.Server.ClearCommands();

        h.Engine.Game = GameId.None;
        h.Engine.Game = GameId.Rac2;

        h.Server.RealTime = TimeSpan.FromSeconds(630);
        h.Engine.Handle(new AutosplitEvent(3, 31_000, AutosplitKind.Resume, NormalisedPause.Code, 0));
        Assert.True(await h.Sent(LiveSplitClient.UnpauseGameTime));
        await Task.Delay(200);

        Assert.Equal(new[] { LiveSplitClient.UnpauseGameTime }, h.Server.Actions);
        Assert.Empty(h.Server.GameTimesSet);
        Assert.False(h.Server.GameTimePaused);
        Assert.Contains("no pause of that code was open", h.Engine.Log()[^1].Action);
    }

    /// <summary>A new run starts with nothing open, whatever the last one left behind.</summary>
    [Fact]
    public async Task AStartClearsWhateverTheLastRunLeftOpen()
    {
        using var h = await RunningDeadlockedAsync(600, 600);

        h.Engine.Handle(new AutosplitEvent(2, 1_000, AutosplitKind.Pause, NormalisedPause.Code, 0));
        Assert.True(await h.Sent(LiveSplitClient.PauseGameTime));
        h.Server.ClearCommands();

        // The timer is already running, so this start sends nothing at all; it still ends the run
        // the quit belonged to.
        h.Engine.Handle(new AutosplitEvent(3, 20_000, AutosplitKind.Start, 0, 0));
        Assert.Contains("the timer is Running", h.Engine.Log()[^1].Action);

        h.Engine.Handle(new AutosplitEvent(4, 31_000, AutosplitKind.Resume, NormalisedPause.Code, 0));
        Assert.True(await h.Sent(LiveSplitClient.UnpauseGameTime));
        await Task.Delay(200);

        Assert.Equal(new[] { LiveSplitClient.UnpauseGameTime }, h.Server.Actions);
        Assert.Empty(h.Server.GameTimesSet);
        Assert.Contains("no pause of that code was open", h.Engine.Log()[^1].Action);
    }

    /// <summary>
    /// The run gets its game time with the timer, so the first quit has something real to freeze
    /// and nothing has to be read back to make it so.
    /// </summary>
    [Fact]
    public async Task TheStartGivesTheRunAGameTime()
    {
        using var h = new Harness(GameId.Rac4, new[] { "Dread Zone", "Catacrom" }, PlanetEntered, NormalisedPause);
        await h.ReadyAsync();
        h.Server.RealTime = TimeSpan.FromSeconds(12);

        h.Engine.Handle(new AutosplitEvent(1, 10, AutosplitKind.Start, 0, 0));
        Assert.True(await h.Sent(LiveSplitClient.InitialiseGameTime));

        Assert.Equal(new[] { LiveSplitClient.StartTimer, LiveSplitClient.InitialiseGameTime },
            h.Server.Actions);
        Assert.Equal(new[] { 0.0 }, h.Server.LoadingTimeSeconds);

        // It moved nothing: game time is still real time, it simply exists now.
        Assert.Equal(TimeSpan.FromSeconds(12), h.Server.GameTime!.Value);
        Assert.Contains($"then {LiveSplitClient.InitialiseGameTime}", h.Engine.Log()[^1].Action);
        Assert.Null(h.Server.Fault);
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
        // Confirmed against the installed LiveSplit's own TimeSpanParser: a bare number is read as
        // seconds, six decimals carry a microsecond without rounding, and a leading minus is read
        // as a negative time, which is how a loading time gives a run time back rather than taking
        // it away.
        Assert.Equal("0.116667", LiveSplitClient.FormatTime(116_667));
        Assert.Equal("1.440000", LiveSplitClient.FormatTime(1_440_000));
        Assert.Equal("7.560000", LiveSplitClient.FormatTime(7_560_000));
        Assert.Equal("addloadingtimes 1.440000", LiveSplitClient.AddLoadingTimesCommand(1_440_000));

        Assert.Equal("-0.500000", LiveSplitClient.FormatTime(-500_000));
        Assert.Equal("addloadingtimes -14.800000", LiveSplitClient.AddLoadingTimesCommand(-14_800_000));

        // The one that corrects nothing and is sent for its side effect: a run that had no game
        // time has one after it, equal to real time.
        Assert.Equal("addloadingtimes 0.000000", LiveSplitClient.InitialiseGameTime);
        Assert.Equal(LiveSplitClient.InitialiseGameTime, LiveSplitClient.AddLoadingTimesCommand(0));
    }

    [Fact]
    public void TheGameTimeStringIsAClockLiveSplitParses()
    {
        // Colons, and hours counted straight through: the server splits on colons, so a run past
        // midnight must not come out as TimeSpan's own "1.00:00:00".
        Assert.Equal("0:10:14.8000000", LiveSplitClient.FormatGameTime(TimeSpan.FromSeconds(614.8)));
        Assert.Equal("1:00:00.0000000", LiveSplitClient.FormatGameTime(TimeSpan.FromHours(1)));
        Assert.Equal("25:00:00.0000000", LiveSplitClient.FormatGameTime(TimeSpan.FromHours(25)));
        Assert.Equal("-0:00:05.2500000", LiveSplitClient.FormatGameTime(TimeSpan.FromSeconds(-5.25)));
        Assert.Equal("setgametime 0:10:14.8000000",
            LiveSplitClient.SetGameTimeCommand(TimeSpan.FromSeconds(614.8)));

        // And back: what the installed build's PreciseTimeFormatter writes is a TimeSpan's own text.
        Assert.True(LiveSplitClient.TryParseGameTime("00:10:14.8000000", out var time));
        Assert.Equal(TimeSpan.FromSeconds(614.8), time);
        Assert.True(LiveSplitClient.TryParseGameTime(" 0:00:00 ", out time));
        Assert.Equal(TimeSpan.Zero, time);

        // A build that answers in bare seconds means seconds, never TimeSpan.Parse's days.
        Assert.True(LiveSplitClient.TryParseGameTime("614.8", out time));
        Assert.Equal(TimeSpan.FromSeconds(614.8), time);

        // "-" is LiveSplit for "no time at all", and a build that ignores the query says nothing.
        Assert.False(LiveSplitClient.TryParseGameTime("-", out _));
        Assert.False(LiveSplitClient.TryParseGameTime(null, out _));
        Assert.False(LiveSplitClient.TryParseGameTime(string.Empty, out _));
        Assert.False(LiveSplitClient.TryParseGameTime("what", out _));
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
    public async Task TheHandshakeAsksForTheVersionAndThenThePhase()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" });
        Assert.True(await WaitFor(() => h.LiveSplit.IsConnected));

        // The version first, because it is what decides whether this build is worth driving, and
        // the phase straight after, so the engine knows whether a run is under way.
        Assert.True(await WaitFor(() => h.Server.Commands.Length >= 2));
        Assert.Equal(LiveSplitClient.GetLiveSplitVersion, h.Server.Commands[0]);
        Assert.Equal(LiveSplitClient.GetCurrentTimerPhase, h.Server.Commands[1]);

        await h.RefreshAsync();
        Assert.Equal(LiveSplitStatus.Connected, h.LiveSplit.Status);
        Assert.False(h.LiveSplit.TooOld);
        Assert.Equal(h.Server.Version, h.LiveSplit.Version);
        Assert.Contains("Connected to LiveSplit", h.LiveSplit.StatusLine);
        Assert.Contains(h.Server.Version, h.LiveSplit.StatusLine);
        Assert.Equal(LiveSplitPhase.NotRunning, h.Engine.View.Phase);
        Assert.Equal("Aranos", h.Engine.View.CurrentSplit);
        Assert.Equal("Oozla", h.Engine.View.UpcomingSplit);
    }

    /// <summary>
    /// The build wall. Everything this client sends belongs to the development build, so a server
    /// that will not name its version is one there is nothing to be done with: it is told apart
    /// from "nothing was listening", said so in its own words, and not retried.
    /// </summary>
    [Fact]
    public async Task ALiveSplitThatWillNotNameItsVersionIsTurnedAway()
    {
        using var server = new FakeLiveSplitServer("Aranos", "Oozla");
        server.Ignored.Add(LiveSplitClient.GetLiveSplitVersion);

        using var client = new LiveSplitClient();
        client.Start(LiveSplitClient.DefaultHost, server.Port);

        Assert.True(await WaitFor(() => client.TooOldFailures >= 1), "the old build was never turned away");
        Assert.True(client.TooOld);
        Assert.False(client.IsConnected);
        Assert.False(client.Enabled);
        Assert.Null(client.Version);
        Assert.StartsWith(LiveSplitClient.TooOldStatus, client.StatusLine);

        // Nothing was listening is a different failure with different words, and this is not it.
        Assert.Equal(0, client.ConnectFailures);

        // It asked the one question and stopped: no phase query, and no reconnect loop behind it.
        await Task.Delay(1500);
        Assert.Equal(new[] { LiveSplitClient.GetLiveSplitVersion }, server.Commands);
        Assert.Equal(1, client.TooOldFailures);

        client.Stop();
    }

    /// <summary>The two popups say different things, and the second one says which build to get.</summary>
    [Fact]
    public void TheTooOldPopupNamesTheDevelopmentBuild()
    {
        Assert.NotEqual(LiveSplitModal.Body, LiveSplitModal.TooOldBody);
        Assert.Contains("development build", LiveSplitModal.TooOldBody);
        Assert.Contains(LiveSplitClient.GetUpcomingSplitName, LiveSplitModal.TooOldBody);
        Assert.Contains("livesplit.org", LiveSplitModal.TooOldBody);
    }

    // ---------------------------------------------------------------- the connection itself

    [Fact]
    public async Task AQueryTheServerIgnoresIsUnknownRatherThanADroppedConnection()
    {
        // LiveSplit's real server answers nothing at all to a command outside its list. The build
        // this client supports answers everything it is asked, so nothing should ever land here —
        // but waiting a second and then tearing the socket down is what made the link drop seconds
        // after it came up, and the net that caught that stays.
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated);
        h.Server.Ignored.Add(LiveSplitClient.GetUpcomingSplitName);
        await h.ReadyAsync();

        // The first refresh spends the timeout finding out; after that the client knows.
        Assert.True(await WaitFor(() => !h.LiveSplit.Answers(LiveSplitClient.GetUpcomingSplitName)),
            "the unanswered query was never noticed");
        await h.RefreshAsync();

        Assert.True(h.LiveSplit.IsConnected);
        Assert.Null(h.Engine.View.UpcomingSplit);
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

            var rac2 = saved.Autosplit.For(GameId.Rac2);
            rac2.PlanetRoute = true;
            rac2.SetEvent("Planet entered", false);
            rac2.SetEvent("Protopet defeated", true);

            saved.Autosplit.For(GameId.Rac4).Reset = false;
            saved.Autosplit.For(GameId.Rac4).Pause = false;
            saved.Save();

            // The two settings this build replaced are not written back out.
            string json = File.ReadAllText(path);
            Assert.DoesNotContain("neverReset", json);
            Assert.DoesNotContain("namesAreDestination", json);

            var loaded = Settings.Load(path);
            Assert.True(loaded.Autosplit.Enabled);
            Assert.Equal("10.0.0.4", loaded.Autosplit.Host);
            Assert.Equal(16835, loaded.Autosplit.Port);

            var back = loaded.Autosplit.For(GameId.Rac2);
            Assert.True(back.PlanetRoute);
            Assert.False(back.EventEnabled("Planet entered", byDefault: true));
            Assert.True(back.EventEnabled("Protopet defeated", byDefault: false));

            // The four masters default on, and only the ones that were changed are off.
            Assert.True(back.Start);
            Assert.True(back.Split);
            Assert.True(back.Reset);
            Assert.True(back.Pause);
            Assert.False(loaded.Autosplit.For(GameId.Rac4).Reset);
            Assert.False(loaded.Autosplit.For(GameId.Rac4).Pause);

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
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The two settings the .lss route left behind. Nothing reads them, and a file that still names
    /// them has to load exactly as it did rather than being refused for holding a retired key.
    /// </summary>
    [Fact]
    public void ASettingsFileThatStillNamesASplitsFileLoads()
    {
        const string older = """
        {
          "autosplit": {
            "enabled": true,
            "host": "10.0.0.4",
            "splitsFile": "C:\\splits\\GC NG+.lss",
            "liveSplitFolder": "C:\\LiveSplit",
            "games": { "rac4": { "planetRoute": true } }
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
            Assert.True(loaded.Autosplit.Enabled);
            Assert.Equal("10.0.0.4", loaded.Autosplit.Host);
            Assert.True(loaded.Autosplit.For(GameId.Rac4).PlanetRoute);
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

            // A file from before the Pause switch existed pauses, which is what it did.
            Assert.True(rac4.Pause);
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
