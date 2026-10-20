using System.Diagnostics;

using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The DESCRIBE every new connection owes the description on screen. The description is held under
/// the game, the qwark build and the protocol revision, and a module replaced by another of the
/// same build keeps all three: the user's console restarted into a qwark.sprx 53 that gave UYA's
/// Reset trophies a status readout and a thirteenth readout name, and the client went on drawing
/// the old module's rows until it was restarted itself. So each connection reads DESCRIBE once, the
/// first time it finds the game INGAME, and compares: the same description keeps everything, a
/// different one resets the panels as a new game would. A reboot on the same connection asks nothing.
/// </summary>
public class ReconnectDescribeTests
{
    private static async Task<AppState> ConnectedStateAsync(FakeQwarkServer server)
    {
        var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);
        return state;
    }

    /// <summary>Runs frames until the condition holds, the way the render loop would.</summary>
    private static async Task<bool> PumpAsync(AppState state, Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            state.Tick(1f / 60f);
            if (condition()) return true;
            await Task.Delay(16);
        }

        state.Tick(1f / 60f);
        return condition();
    }

    /// <summary>
    /// Frames until the link is down and the client has taken in the Disconnected event that says
    /// so, which is what clears <see cref="AppState.Hello"/>. The transport flag drops first and the
    /// event is raised a moment later on the thread that saw the link go: a test that reconnects
    /// on the flag alone can have that event land after the new connection's SessionEstablished
    /// and undo what the new connection set up.
    /// </summary>
    private static Task<bool> DroppedAsync(AppState state) =>
        PumpAsync(state, () => !state.Connected && state.Hello is null);

    /// <summary>Runs frames for a while, so a request that should not go out has had every chance to.</summary>
    private static Task PumpForAsync(AppState state, int milliseconds) => PumpAsync(state, () => false, milliseconds);

    private static int Count(FakeQwarkServer server, Opcode opcode) => server.RequestLog().Count(op => op == opcode);

    /// <summary>
    /// UYA under the module from before the trophy status: the Reset trophies row names no readout
    /// and the readout list ends before "Trophy reset". <see cref="WithTrophyStatus"/> is the same
    /// build after it.
    /// </summary>
    private static DescribeResult WithoutTrophyStatus(DescribeResult describe) => describe with
    {
        Readouts = describe.Readouts.Where(name => name != FakeQwarkServer.TrophyStatusReadoutName).ToArray(),
        Features = describe.Features
            .Select(f => f.Label == FakeQwarkServer.TrophyResetLabel ? f with { Readout = SessionInfo.NoReadout } : f)
            .ToArray(),
    };

    private static DescribeResult WithTrophyStatus(DescribeResult describe) =>
        FakeQwarkServer.WithManipHeaderFeatures(describe with { Game = GameId.Rac3 }, GameId.Rac3);


    /// <summary>
    /// The user's case: the console restarted into a qwark.sprx of the same build that describes
    /// UYA with one more readout and a changed row. The connection comes back at the XMB, the game
    /// boots, and the first tick in it reads the new description, resets the panels and draws the
    /// status line the new row names, with one DESCRIBE and nothing refused on the way.
    /// </summary>
    [Fact]
    public async Task AReconnectToAModuleThatDescribesTheGameDifferentlyResetsThePanels()
    {
        using var server = new FakeQwarkServer();
        var newModule = WithTrophyStatus(server.Describe);
        var oldModule = WithoutTrophyStatus(newModule);
        server.Describe = oldModule;
        server.Session = server.Session with { Game = GameId.Rac3, TitleId = "NPEA00387" };
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0 && state.Manips is not null));
        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));
        var before = state.Describe;
        int described = server.DescribeCount;
        int resets = state.PanelResets;
        Assert.Equal(oldModule.Readouts.Length, before.Readouts.Length);
        Assert.Null(Array.Find(before.Features, f => f.Label == FakeQwarkServer.TrophyResetLabel)!.StatusReadout);

        // The console restarts. The new module is the same build, so the key the description is
        // held under is the same, and a fresh module counts its generations from the start again.
        server.DropClients();
        Assert.True(await DroppedAsync(state));

        server.Describe = newModule;
        server.Session = server.Session with
        {
            State = SessionState.Xmb,
            Game = GameId.None,
            TitleId = string.Empty,
            Generation = 1,
        };
        server.ClearRequestLog();
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        // At the XMB there is no game to describe, and through the boot qwark refuses DESCRIBE:
        // nothing is asked, and what is on screen stays, greyed out.
        Assert.True(await PumpAsync(state, () => state.Connected && state.Session.State == SessionState.Xmb));
        await PumpForAsync(state, 300);
        server.Booting = true;
        Assert.True(await PumpAsync(state, () => state.ConsoleBusy && state.InFlight == 0));
        int refused = server.BusyCount;
        await PumpForAsync(state, 300);
        Assert.Equal(0, Count(server, Opcode.Describe));
        Assert.Same(before, state.Describe);
        Assert.Equal(resets, state.PanelResets);

        // The game is up.
        server.Session = server.Session with
        {
            State = SessionState.Ingame,
            Game = GameId.Rac3,
            TitleId = "NPEA00387",
            Generation = 1,
        };

        Assert.True(await PumpAsync(state, () => state.Describe.Readouts.Length == newModule.Readouts.Length));
        Assert.Equal(1, state.DescriptionChecks);
        Assert.Equal(1, state.DescriptionsReplaced);
        Assert.Equal(resets + 1, state.PanelResets);
        Assert.Equal(GameId.Rac3, state.DescribedGame);
        Assert.Equal("NPEA00387", state.DescribedTitle);
        Assert.NotNull(Array.Find(state.Describe.Features, f => f.Label == FakeQwarkServer.TrophyResetLabel)!.StatusReadout);

        // Everything a key change reads comes back, and the line the new row names is drawn.
        Assert.True(await PumpAsync(state, () => state.Planets.Length > 0 && state.AutosplitEvents.Length > 0
                                                 && state.Positions.Slots.Length > 0 && state.EnumOptions.Count > 0
                                                 && state.Manips is not null));
        Assert.True(server.SetTrophyStatus(TrophyResetStatus.Ready(13, 1)));
        Assert.True(await PumpAsync(state, () => ManipsArea.Summary(state).EndsWith(
            "trophies=Trophies_ready_(13_s)", StringComparison.Ordinal)));

        // The answer in hand was adopted, not asked for a second time, and no read was refused.
        await PumpForAsync(state, 300);
        Assert.Equal(described + 1, server.DescribeCount);
        Assert.Equal(1, Count(server, Opcode.Describe));
        Assert.Equal(refused, server.BusyCount);
    }

    /// <summary>
    /// The usual reconnect: the same module, describing the game the same way. One DESCRIBE goes out
    /// and is found to say what is on screen, so the description object, the panels and their
    /// drafts all stay; the module's other tables are read once each beside it.
    /// </summary>
    [Fact]
    public async Task AReconnectToTheSameModuleKeepsEverythingAndDescribesOnce()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0 && state.EnumOptions.Count > 0
                                                 && state.Planets.Length > 0 && state.AutosplitEvents.Length > 0));
        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));
        var before = state.Describe;
        int described = server.DescribeCount;
        int resets = state.PanelResets;

        server.DropClients();
        Assert.True(await DroppedAsync(state));
        server.ClearRequestLog();
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.DescriptionChecks == 1));
        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));

        // The tables a check reads beside DESCRIBE have all been asked for, however long a busy
        // machine took to get to them; then a while longer, for any that should not come.
        int enums = before.Features.Count(f => f.Kind == FeatureKind.Enum);
        Assert.True(await PumpAsync(state, () => Count(server, Opcode.PlanetList) >= 1
                                                 && Count(server, Opcode.AutosplitDescribe) >= 1
                                                 && Count(server, Opcode.SaveFileInfo) >= 1
                                                 && Count(server, Opcode.FeatureOptions) >= enums
                                                 && state.InFlight == 0));
        await PumpForAsync(state, 400);

        Assert.Equal(described + 1, server.DescribeCount);
        Assert.Equal(1, Count(server, Opcode.Describe));
        Assert.Equal(0, state.DescriptionsReplaced);
        Assert.Equal(resets, state.PanelResets);
        Assert.Same(before, state.Describe);
        Assert.Equal(GameId.Rac1, state.DescribedGame);

        // The same blind spot's other tables, once each on the new connection.
        Assert.Equal(1, Count(server, Opcode.PlanetList));
        Assert.Equal(1, Count(server, Opcode.AutosplitDescribe));
        Assert.Equal(1, Count(server, Opcode.SaveFileInfo));
        Assert.Equal(before.Features.Count(f => f.Kind == FeatureKind.Enum), Count(server, Opcode.FeatureOptions));
        Assert.NotEmpty(state.Planets);
        Assert.NotEmpty(state.AutosplitEvents);

        // And nothing more for as long as the connection lasts: once per connection, not per tick.
        await PumpForAsync(state, 400);
        Assert.Equal(1, Count(server, Opcode.Describe));
        Assert.Equal(1, Count(server, Opcode.PlanetList));
    }

    /// <summary>
    /// An "on boot" box ticked before the link went. qwark sets the toggle's AUTO bit in DESCRIBE
    /// from toggle_auto as it answers, so the module's answer differs from the description on
    /// screen in that bit and nothing else, and that is not a module change.
    /// </summary>
    [Fact]
    public async Task AnOnBootBoxTickedBeforeAReconnectIsNotAModuleChange()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0));
        var before = state.Describe;
        int resets = state.PanelResets;

        await state.Client.FeatureSetAutoAsync(0, true);
        var answer = await state.Client.DescribeAsync();
        Assert.NotEqual(FakeQwarkServer.EncodeDescribe(before), FakeQwarkServer.EncodeDescribe(answer));
        Assert.True(answer.SameLayoutAs(before));

        server.DropClients();
        Assert.True(await DroppedAsync(state));
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.DescriptionChecks == 1));
        Assert.Equal(0, state.DescriptionsReplaced);
        Assert.Equal(resets, state.PanelResets);
        Assert.Same(before, state.Describe);
    }

    /// <summary>
    /// A reboot of the game, BOOTING and a new generation on the connection that was already up:
    /// the module is the one that described the game, so nothing is described again, as before.
    /// </summary>
    [Fact]
    public async Task AGameRebootOnTheSameConnectionDoesNotDescribeAgain()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0));
        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));
        var before = state.Describe;
        int described = server.DescribeCount;
        int resets = state.PanelResets;
        server.ClearRequestLog();

        // A read the last telemetry still sent for the game may be refused; nothing after the
        // client has seen the boot may be.
        server.Booting = true;
        Assert.True(await PumpAsync(state, () => state.ConsoleBusy && state.InFlight == 0));
        int refused = server.BusyCount;
        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length == 0));

        server.Session = server.Session with
        {
            State = SessionState.Ingame,
            Generation = server.Session.Generation + 1,
        };

        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));
        await PumpForAsync(state, 400);

        Assert.Equal(described, server.DescribeCount);
        Assert.Equal(0, Count(server, Opcode.Describe));
        Assert.Equal(0, state.DescriptionChecks);
        Assert.Equal(resets, state.PanelResets);
        Assert.Same(before, state.Describe);
        Assert.Equal(refused, server.BusyCount);
    }

    /// <summary>
    /// The first connection describes the game through the key change, as it always has, and that
    /// read is the connection's: no check follows it.
    /// </summary>
    [Fact]
    public async Task TheFirstConnectionDescribesOnceAndChecksNothing()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0 && state.Positions.Slots.Length > 0));
        await PumpForAsync(state, 400);

        Assert.Equal(1, server.DescribeCount);
        Assert.Equal(0, state.DescriptionChecks);
        Assert.Equal(0, state.PanelResets);
    }

    [Fact]
    public void SameLayoutComparesEveryFieldButTheAutoBit()
    {
        using var server = new FakeQwarkServer();
        var describe = server.Describe;
        var copy = FakeQwarkServer.WithAutoFlags(describe, 0);
        Assert.NotSame(describe, copy);
        Assert.True(describe.SameLayoutAs(copy));

        // The on-boot bit of a toggle, either way.
        Assert.True(describe.SameLayoutAs(FakeQwarkServer.WithAutoFlags(describe, 1UL << 0)));
        Assert.True(describe.SameLayoutAs(describe with
        {
            Features = describe.Features.Select(f => f with { Flags = f.Flags & ~FeatureFlags.Auto }).ToArray(),
        }));

        // Anything else is another description.
        Assert.False(describe.SameLayoutAs(describe with { Game = GameId.Rac2 }));
        Assert.False(describe.SameLayoutAs(describe with { Groups = describe.Groups.Append("Debug").ToArray() }));
        Assert.False(describe.SameLayoutAs(describe with { Readouts = describe.Readouts.Append("Trophy reset").ToArray() }));
        Assert.False(describe.SameLayoutAs(describe with { Readouts = describe.Readouts.Reverse().ToArray() }));
        Assert.False(describe.SameLayoutAs(describe with { Features = describe.Features[..^1] }));
        Assert.False(describe.SameLayoutAs(describe with
        {
            Features = describe.Features.Select(f => f.Id == 2 ? f with { Readout = 4 } : f).ToArray(),
        }));
        Assert.False(describe.SameLayoutAs(describe with
        {
            Features = describe.Features.Select(f => f.Id == 9 ? f with { Flags = FeatureFlags.None } : f).ToArray(),
        }));
        Assert.False(describe.SameLayoutAs(describe with
        {
            Features = describe.Features.Select(f => f.Id == 0 ? f with { Label = "Infinite ammo!" } : f).ToArray(),
        }));
    }
}
