using System.Diagnostics;

using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The session qwark build 36 reports for a title it has no game module for: INGAME, game 0, and
/// the title id filled in. The memory ops answer there and every game op answers UNSUPPORTED, so
/// the client's half of it is three things: it asks for none of them, it reads the watches, the
/// freezes and the patches the way it always does, and the nav is down to the panels that work.
/// </summary>
public class UnknownGameTests
{
    private static async Task<AppState> ConnectedStateAsync(FakeQwarkServer server)
    {
        var state = new AppState(new Settings { AutoReconnect = false });
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

    private static async Task PumpForAsync(AppState state, int milliseconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            state.Tick(1f / 60f);
            await Task.Delay(16);
        }
    }

    // ---------------------------------------------------------------- what the session is called

    [Fact]
    public void AGameThisClientCannotNameIsStillAGame()
    {
        var session = SessionInfo.Empty with
        {
            State = SessionState.Ingame,
            Game = GameId.None,
            TitleId = "BLES00932",
        };

        Assert.True(session.IsUnknownGame);
        Assert.Equal("Unknown game", session.GameName);
    }

    [Fact]
    public void NoGameAtAllIsStillNoGame()
    {
        // The XMB: nothing is running, and "Unknown game" would be a game that is not there.
        Assert.False(SessionInfo.Empty.IsUnknownGame);
        Assert.Equal("no game", SessionInfo.Empty.GameName);

        // And a game the module does know is named after itself.
        var rac2 = SessionInfo.Empty with { State = SessionState.Ingame, Game = GameId.Rac2 };
        Assert.False(rac2.IsUnknownGame);
        Assert.Equal("RaC2", rac2.GameName);
    }

    // ---------------------------------------------------------------- the nav's rule

    [Fact]
    public void EveryPanelThatDrawsTheGameIsHiddenAndTheMemoryToolsAreNot()
    {
        foreach (int panel in new[]
                 {
                     PanelNav.Game, PanelNav.Unlocks, PanelNav.Positions, PanelNav.Combos,
                     PanelNav.Mods, PanelNav.SaveFiles, PanelNav.Autosplitter, PanelNav.LevelFlags,
                 })
        {
            Assert.False(PanelNav.Visible(panel, unknownGame: true, false, false),
                $"{PanelNav.Names[panel]} has nothing to draw for a title qwark does not know");
        }

        foreach (int panel in new[]
                 {
                     PanelNav.Connection, PanelNav.Memory, PanelNav.InputDisplay, PanelNav.Settings,
                 })
        {
            Assert.True(PanelNav.Visible(panel, unknownGame: true, false, false),
                $"{PanelNav.Names[panel]} works on any session");
        }
    }

    [Fact]
    public void AGameTheModuleKnowsKeepsTheNavItAlreadyHad()
    {
        for (int panel = 0; panel < PanelNav.Count; panel++)
        {
            Assert.True(PanelNav.Visible(panel, unknownGame: false, false, false));
        }

        // And the two panels a game can be without are still hidden one at a time.
        Assert.False(PanelNav.Visible(PanelNav.Unlocks, false, unlocksUnsupported: true, false));
        Assert.False(PanelNav.Visible(PanelNav.LevelFlags, false, false, levelFlagsUnsupported: true));
        Assert.True(PanelNav.Visible(PanelNav.Memory, false, unlocksUnsupported: true, levelFlagsUnsupported: true));
    }

    [Fact]
    public void SomeoneOnAHiddenPanelIsPutOnOneThatWorks()
    {
        Assert.Equal(PanelNav.Memory, PanelNav.Fallback(unknownGame: true));
        Assert.Equal(PanelNav.Game, PanelNav.Fallback(unknownGame: false));

        // Whichever it is, the nav has to be willing to draw it.
        Assert.True(PanelNav.Visible(PanelNav.Fallback(true), unknownGame: true, false, false));
        Assert.True(PanelNav.Visible(PanelNav.Fallback(false), unknownGame: false, false, false));
    }

    // ---------------------------------------------------------------- the fake console's half

    [Fact]
    public async Task TheConsoleAnswersTheMemoryOpsAndRefusesEveryGameOp()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        server.UnknownGame = true;

        using var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(client.IsConnected);
        Assert.Equal(GameId.None, client.LatestSession!.Game);
        Assert.Equal(SessionState.Ingame, client.LatestSession.State);
        Assert.Equal(server.UnknownTitleId, client.LatestSession.TitleId);

        // The memory ops, which are the point of the session.
        Assert.Equal(16, (await client.MemReadAsync(server.MemoryBase, 16)).Length);
        await client.MemWriteAsync(server.MemoryBase, new byte[] { 1, 2, 3, 4 });
        await client.WatchAddAsync(server.MemoryBase, 4);
        Assert.Single(await client.WatchListAsync());
        await client.FreezeListAsync();
        await client.PatchListAsync();

        // And the game ops, which are not.
        foreach (var refused in new Func<Task>[]
                 {
                     () => client.DescribeAsync(),
                     () => client.PlanetListAsync(),
                     () => client.PosListAsync(),
                     () => client.UnlockListAsync(),
                     () => client.LevelFlagsGetAsync(0),
                     () => client.ModListAsync(),
                     () => client.ComboListAsync(),
                     () => client.AutosplitDescribeAsync(),
                     () => client.SaveFileInfoAsync(),
                     () => client.MobyTableAsync(),
                 })
        {
            Assert.Equal(Status.Unsupported, (await Assert.ThrowsAsync<QwarkStatusException>(refused)).Status);
        }
    }

    // ---------------------------------------------------------------- the client's half

    [Fact]
    public async Task NoGameOpIsSentForATitleQwarkDoesNotKnow()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        server.UnknownGame = true;

        using var state = await ConnectedStateAsync(server);

        // The three memory lists go out on their own, because the memory tools are drawn from them.
        var owed = new[] { Opcode.WatchList, Opcode.FreezeList, Opcode.PatchList };
        Assert.True(await PumpAsync(state, () => owed.All(server.RequestLog().Contains)),
            $"never asked for {string.Join(", ", owed.Except(server.RequestLog()))}");

        await PumpForAsync(state, 400);

        var sent = server.RequestLog();
        Assert.DoesNotContain(Opcode.Describe, sent);
        Assert.All(sent, opcode => Assert.False(FakeQwarkServer.IsGameOp(opcode), $"{opcode} was sent for a title qwark does not know"));

        // The console never had to refuse anything, so there is nothing to have toasted about.
        Assert.Equal(0, server.UnsupportedCount);
        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);
        Assert.Null(state.LastError);

        // The session is INGAME, which is what the Memory panel needs to draw its controls.
        Assert.True(state.UnknownGame);
        Assert.True(state.Ingame);

        // And a read of game memory works, which is the whole point of staying connected.
        Assert.Equal(64, (await state.Client.MemReadAsync(server.MemoryBase, 64)).Length);

        // The pad is read through the game module there is none of, so the input display has an
        // idle controller to draw rather than a stuck one.
        Assert.Equal(0u, state.Session.PadMask);
    }

    /// <summary>
    /// The watches are the one live thing such a session has: qwark reads the addresses it was
    /// given without knowing what they are, and the values ride the same telemetry packet.
    /// </summary>
    [Fact]
    public async Task TheWatchesAreListedAndStillCarryTheirValues()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        server.UnknownGame = true;
        server.Watches.Add(new WatchEntry(0, 4, server.MemoryBase));

        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Watches.Length == 1));
        Assert.True(await PumpAsync(state, () => state.WatchValueFor(0) is { Valid: true }));
    }

    [Fact]
    public async Task TheStatusLineNamesTheTitleAndSaysTheGameIsUnknown()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        server.UnknownGame = true;

        using var state = await ConnectedStateAsync(server);
        Assert.True(await PumpAsync(state, () => state.Ingame));

        string line = state.StatusLine();
        Assert.Contains("INGAME", line);
        Assert.Contains(server.UnknownTitleId, line);
        Assert.Contains("Unknown game", line);
        Assert.DoesNotContain("no game", line);
    }

    /// <summary>
    /// The everyday way into one of these: a game qwark knows is quit and something else is
    /// started. What was described goes off the screen with the nav, and the reads that follow are
    /// the memory lists and nothing else.
    /// </summary>
    [Fact]
    public async Task AKnownGameGivingWayToAnUnknownTitleStopsTheGameReads()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0));
        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));
        Assert.False(state.UnknownGame);

        // Everything the known game asked for has answered by now, so what lands in the log after
        // this was asked for on the title that replaces it and not on its way out.
        await PumpForAsync(state, 300);
        server.ClearRequestLog();
        server.UnknownGame = true;

        Assert.True(await PumpAsync(state, () => state.UnknownGame));

        var owed = new[] { Opcode.WatchList, Opcode.FreezeList, Opcode.PatchList };
        Assert.True(await PumpAsync(state, () => owed.All(server.RequestLog().Contains)),
            $"never asked for {string.Join(", ", owed.Except(server.RequestLog()))} on the new title");

        await PumpForAsync(state, 400);

        Assert.All(server.RequestLog(),
            opcode => Assert.False(FakeQwarkServer.IsGameOp(opcode), $"{opcode} was sent for a title qwark does not know"));
        Assert.Equal(0, server.UnsupportedCount);
        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);

        // Nothing read out of the old process is still on screen, and the nav has moved with it.
        Assert.Empty(state.Positions.Slots);
        Assert.False(PanelNav.Visible(PanelNav.Game, state.UnknownGame, state.UnlocksUnsupported, state.LevelFlagsUnsupported));
        Assert.True(PanelNav.Visible(PanelNav.Memory, state.UnknownGame, state.UnlocksUnsupported, state.LevelFlagsUnsupported));
    }

    /// <summary>
    /// "Re-read everything" is the one refresh the user asks for by hand, so every status it
    /// collects is theirs to see. It must therefore not collect any: the game ops are not sent
    /// here either.
    /// </summary>
    [Fact]
    public async Task TheUsersOwnReReadAsksForNothingTheConsoleWouldRefuse()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        server.UnknownGame = true;

        using var state = await ConnectedStateAsync(server);
        Assert.True(await PumpAsync(state, () => state.Ingame));

        server.ClearRequestLog();
        state.ForceRefresh();

        Assert.True(await PumpAsync(state, () => server.RequestLog().Contains(Opcode.WatchList)));
        await PumpForAsync(state, 300);

        Assert.All(server.RequestLog(),
            opcode => Assert.False(FakeQwarkServer.IsGameOp(opcode), $"{opcode} was sent by a re-read"));
        Assert.Equal(0, server.UnsupportedCount);
        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);
    }

    /// <summary>The fake script's step, which is what a headless run drives one of these with.</summary>
    [Fact]
    public void TheScriptStepPutsTheFakeConsoleOnATitleItKnowsNothingAbout()
    {
        using var server = new FakeQwarkServer();
        FakeScript.Apply(server, "unknown");

        Assert.True(server.UnknownGame);
        Assert.Equal(SessionState.Ingame, server.Session.State);
        Assert.Equal(GameId.None, server.Session.Game);
        Assert.Equal(server.UnknownTitleId, server.Session.TitleId);
        Assert.True(server.Session.IsUnknownGame);

        // And a game the module knows, started after it, leaves the refusals behind.
        FakeScript.Apply(server, "rac2");
        Assert.False(server.UnknownGame);
        Assert.Equal(GameId.Rac2, server.Session.Game);
    }
}
