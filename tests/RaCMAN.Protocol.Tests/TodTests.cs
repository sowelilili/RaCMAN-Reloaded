using System.Diagnostics;
using System.Text;
using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Tools of Destruction (game 5, revision 1.27): the game id and its module, the three Lua ops'
/// layouts, the fake console's ToD, and what the client makes of them. The fake listens on
/// loopback only, as every fake here does.
/// </summary>
public class TodTests
{
    // ---------------------------------------------------------------- the game

    [Fact]
    public void ToDIsGameFiveWithAModuleOfItsOwnName()
    {
        Assert.Equal(5, (int)GameId.Tod);
        Assert.Equal("ToD", GameId.Tod.DisplayName());
        Assert.Equal("tod.sprx", GameModule.FileName(5));
        Assert.Equal("/dev_hdd0/qwark/tod.sprx", GameModule.ConsolePath(GameId.Tod));
        Assert.Equal(GameId.Tod, GameModule.Games[^1]);
        Assert.Equal("RaC1 and ToD", GameModule.Names(new[] { GameId.Rac1, GameId.Tod }));

        // The per-game files and settings are keyed by the lower-case enum name.
        Assert.Equal("tod", Settings.GameKey(GameId.Tod));
        Assert.Equal("tod", AutosplitRoutes.KeyFor(GameId.Tod));
        // Revision 1.28: ToD has a planet route, one line per LEVEL_ id.
        Assert.Equal(19, AutosplitRoutes.For(GameId.Tod).Count);
        Assert.Empty(LegacyLibraryImport.LegacyPlanets(GameId.Tod));
        Assert.Equal(new PlanetResetOptions(false, false), PlanetResetOptions.For(GameId.Tod, QwarkClient.ExpectedQwarkBuild));
    }

    [Fact]
    public void TheLuaOpsHaveTheirNumbers()
    {
        Assert.Equal(0x00E0, (int)Opcode.LuaExec);
        Assert.Equal(0x00E1, (int)Opcode.LuaStatus);
        Assert.Equal(0x00E2, (int)Opcode.LuaPatch);
        Assert.True(FakeQwarkServer.IsGameOp(Opcode.LuaExec));
        Assert.True(FakeQwarkServer.IsGameOp(Opcode.LuaStatus));
    }

    // ---------------------------------------------------------------- LUA_EXEC

    [Fact]
    public void ALuaExecRequestIsItsLengthNoFlagsAndTheSource()
    {
        var bytes = LuaExecRequest.Encode("return _VERSION");
        Assert.Equal(new byte[] { 0x00, 0x0F, 0x00, 0x00 }, bytes[..4]);
        Assert.Equal("return _VERSION", Encoding.UTF8.GetString(bytes[4..]));
        Assert.Equal("return _VERSION", LuaExecRequest.Decode(bytes));

        Assert.Throws<ArgumentException>(() => LuaExecRequest.Encode(string.Empty));
        Assert.Equal(4 + 8192, LuaExecRequest.Encode(new string('x', 8192)).Length);
        Assert.Throws<ArgumentException>(() => LuaExecRequest.Encode(new string('x', 8193)));
        Assert.Null(LuaExecRequest.Decode(new byte[] { 0x00, 0x05, 0x00, 0x00, (byte)'a' }));
    }

    [Fact]
    public void ALuaExecReplyIsATwelveByteHeadAndItsText()
    {
        byte[] wire =
        {
            0x00, 0x00, 0x00, 0x00,       // lua_status 0
            0x00, 0x00, 0x00, 0x07,       // seq 7
            0x04, 0x01, 0x00, 0x09,       // string, TRUNCATED, 9 bytes
            (byte)'L', (byte)'u', (byte)'a', (byte)' ', (byte)'5', (byte)'.', (byte)'0', (byte)'.', (byte)'2',
        };
        var result = LuaExecResult.Parse(wire);
        Assert.Equal(LuaExecStatus.Ok, result.Status);
        Assert.Equal(7u, result.Seq);
        Assert.Equal(LuaType.String, result.OutType);
        Assert.True(result.Truncated);
        Assert.Equal("Lua 5.0.2", result.Output);
        Assert.Equal(wire, result.ToBytes());

        var timeout = LuaExecResult.Parse(new LuaExecResult(LuaExecStatus.Timeout, 3, LuaType.None, LuaOutFlags.None, "").ToBytes());
        Assert.Equal(LuaExecStatus.Timeout, timeout.Status);
        Assert.Equal(0x200, (int)timeout.Status);
        Assert.Equal("TIMEOUT", timeout.Status.DisplayName());
        Assert.Equal("status 0x7FF", ((LuaExecStatus)0x7FF).DisplayName());

        Assert.Throws<ProtocolException>(() => LuaExecResult.Parse(wire.AsSpan(0, 11)));
        Assert.Throws<ProtocolException>(() => LuaExecResult.Parse(wire.AsSpan(0, wire.Length - 1)));
    }

    // ---------------------------------------------------------------- LUA_STATUS

    [Fact]
    public void ALuaStatusReplyIsAnEightyEightByteHeadAndTheError()
    {
        var info = new LuaStatusInfo(LuaInstalled.Yes, LuaPhase.Waiting, 1234, 0x102043D0, 3, 0x102043D0,
            "hud.lua", "attempt to call a nil value");
        var wire = info.ToBytes();
        Assert.Equal(LuaStatusInfo.HeadSize + info.Error.Length, wire.Length);
        Assert.Equal(1, wire[0]);
        Assert.Equal(1, wire[1]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x04, 0xD2 }, wire[4..8]);
        Assert.Equal((byte)'h', wire[20]);
        Assert.Equal(0, wire[20 + 7]);
        Assert.Equal(new byte[] { 0x00, (byte)info.Error.Length }, wire[84..86]);

        var back = LuaStatusInfo.Parse(wire);
        Assert.Equal(info, back);
        Assert.True(back.IsInstalled);

        Assert.Throws<ProtocolException>(() => LuaStatusInfo.Parse(wire.AsSpan(0, LuaStatusInfo.HeadSize - 1)));
        Assert.Throws<ProtocolException>(() => LuaStatusInfo.Parse(wire.AsSpan(0, wire.Length - 1)));
    }

    // ---------------------------------------------------------------- the fake console

    private static async Task<QwarkClient> ConnectAsync(FakeQwarkServer server)
    {
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return client;
    }

    [Fact]
    public async Task OnAConsoleTheFirstLuaExecPutsTheHelperIn()
    {
        using var server = new FakeQwarkServer();
        server.UseTod();
        server.Start();
        using var client = await ConnectAsync(server);

        var describe = await client.DescribeAsync();
        Assert.Equal(GameId.Tod, describe.Game);
        Assert.Contains(describe.Features, f => f.Label == "God Ratchet" && f.Kind == FeatureKind.Toggle);
        Assert.Contains(describe.Features, f => f.Label == "Infinite health" && f.Kind == FeatureKind.Toggle);
        // qwark revision 1.29: Infinite ammo and Infinite health take "on boot" (not LIVE), as the
        // other games' do; God Ratchet stays LIVE, as RaC1's goodies menu, with no box.
        Assert.False(describe.Features.Single(f => f.Label == "Infinite ammo").IsLive);
        Assert.False(describe.Features.Single(f => f.Label == "Infinite health").IsLive);
        Assert.True(describe.Features.Single(f => f.Label == "God Ratchet").IsLive);
        Assert.DoesNotContain(describe.Features, f => f.Label.Contains("RYNO"));
        Assert.Equal(new[] { "Bolts", "Raritanium", "Challenge mode", "Cutscene cache" }, describe.Readouts);
        Assert.Equal(19, (await client.PlanetListAsync()).Names.Length);

        var before = await client.LuaStatusAsync();
        Assert.Equal(LuaInstalled.No, before.Installed);
        Assert.Equal(0u, before.Heartbeat);

        var result = await client.LuaExecAsync(LuaBox.StarterSource);
        Assert.Equal(LuaExecStatus.Ok, result.Status);
        Assert.Equal(LuaType.String, result.OutType);
        Assert.Equal(FakeQwarkServer.LuaVersion, result.Output);
        Assert.Equal(new[] { LuaBox.StarterSource }, server.LuaSources);

        var after = await client.LuaStatusAsync();
        Assert.True(after.IsInstalled);
        Assert.NotEqual(0u, after.State);

        var error = await client.LuaExecAsync("error(\"no\")");
        Assert.Equal(LuaExecStatus.RuntimeError, error.Status);
        Assert.NotEmpty(error.Output);

        // A console writes its helper itself, so there is no patch to hand out.
        var patch = await Assert.ThrowsAsync<QwarkStatusException>(() => client.LuaPatchAsync());
        Assert.Equal(Status.Unsupported, patch.Status);
    }

    [Fact]
    public async Task AGameWithoutLuaAnswersTheLuaOpsUnsupported()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var client = await ConnectAsync(server);

        foreach (var op in new Func<Task>[] { () => client.LuaStatusAsync(), () => client.LuaExecAsync("return 1"), () => client.LuaPatchAsync() })
        {
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(op);
            Assert.Equal(Status.Unsupported, refused.Status);
        }
    }

    [Fact]
    public async Task UnderRpcs3TheLuaPatchIsWordsStampedAsTheSavefileHelpersAre()
    {
        using var server = new FakeQwarkServer { Emulator = true, NoCodePatches = true };
        server.UseTod();
        server.Start();
        using var client = await ConnectAsync(server);

        var patch = await client.LuaPatchAsync();
        Assert.Equal(server.LuaPatchWords, patch.Words);
        Assert.Empty(patch.Bytes);
        Assert.Equal(PatchReply.StampFor(patch.Words, patch.Bytes), patch.Stamp);

        Assert.Equal(LuaInstalled.No, (await client.LuaStatusAsync()).Installed);
        server.LuaChecking = true;
        Assert.Equal(LuaInstalled.Checking, (await client.LuaStatusAsync()).Installed);
        Assert.Equal(Status.Busy, (await Assert.ThrowsAsync<QwarkStatusException>(() => client.LuaExecAsync("return 1"))).Status);
    }

    // ---------------------------------------------------------------- the client

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

    [Fact]
    public async Task TheClientDescribesToDAndSendsNoLuaExecByItself()
    {
        using var server = new FakeQwarkServer();
        server.UseTod();
        server.Start();

        using var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.HasLua && state.Describe.Features.Length > 0 && state.Planets.Length == 19));

        // The unlock table is the Unlocks panel's to read; its shape is ToD's four categories (the
        // skill points and gold bolts are the Collectables buttons' since the first hardware test).
        var unlocks = await state.Client.UnlockListAsync();
        Assert.Equal(new[] { "Weapons", "Devices", "Gadgets", "Items" }, unlocks.Categories);
        Assert.Equal(10, unlocks.Fields[1].Max);
        Assert.False(unlocks.Fields[2].IsNamed);
        Assert.Equal(GameId.Tod, state.DescribedGame);
        Assert.Equal(LuaInstalled.No, state.Lua!.Installed);
        Assert.True(state.SaveFile.Supported);
        Assert.True(state.Describe.HasSaveFileHelper);
        Assert.Contains(state.AutosplitEvents, d => d.Label == "Tachyon defeated" && d.Code == 7);
        Assert.Null(state.Manips);
        Assert.Contains("Debug", GamePanel.SubPagesWithContent(state));

        // Only a press sends LUA_EXEC: on a console the first one writes the helper into the game.
        state.RefreshLuaStatus();
        Assert.True(await PumpAsync(state, () => server.RequestLog().Count(op => op == Opcode.LuaStatus) >= 2));
        Assert.DoesNotContain(Opcode.LuaExec, server.RequestLog());
        Assert.DoesNotContain(Opcode.LuaPatch, server.RequestLog());
    }

    [Fact]
    public async Task ARaC1HasNoLuaBox()
    {
        using var server = new FakeQwarkServer();
        server.Start();

        using var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0
                                                 && server.RequestLog().Contains(Opcode.LuaStatus)));
        await Task.Delay(100);
        state.Tick(1f / 60f);
        Assert.False(state.HasLua);
        Assert.Null(state.Lua);

        // The fake's RaC1 has nothing the layout sends to Debug, so without Lua there is no such page.
        Assert.DoesNotContain(GameLayout.DebugSection, GamePanel.SubPagesWithContent(state));
    }

    // ---------------------------------------------------------------- the Lua box's words

    [Fact]
    public void TheLuaBoxNamesEveryStatus()
    {
        var ok = LuaBox.Describe(new LuaExecResult(LuaExecStatus.Ok, 1, LuaType.String, LuaOutFlags.None, "Lua 5.0.2"));
        Assert.Equal(LuaBox.LineKind.Ok, ok.Kind);
        Assert.Contains("OK (0)", ok.Line);
        Assert.Contains("string", ok.Line);
        Assert.Equal("Lua 5.0.2", ok.Output);

        var yes = LuaBox.Describe(new LuaExecResult(LuaExecStatus.Ok, 1, LuaType.Boolean, LuaOutFlags.True, "true"));
        Assert.Contains("boolean: true", yes.Line);

        var syntax = LuaBox.Describe(new LuaExecResult(LuaExecStatus.SyntaxError, 1, LuaType.String, LuaOutFlags.None, "near `<eof>'"));
        Assert.Equal(LuaBox.LineKind.Error, syntax.Kind);
        Assert.Contains("syntax error (3)", syntax.Line);
        Assert.Equal("near `<eof>'", syntax.Output);

        var paused = LuaBox.Describe(new LuaExecResult(LuaExecStatus.NotRunning, 1, LuaType.None, LuaOutFlags.None, ""));
        Assert.Equal(LuaBox.LineKind.Warning, paused.Kind);
        Assert.Contains("NOT_RUNNING (0x201)", paused.Line);

        Assert.Contains("BUSY", LuaBox.Refused(Status.Busy, rpcs3: false).Line);
        Assert.Contains("Connection panel", LuaBox.Refused(Status.Unsupported, rpcs3: true).Line);
        Assert.Contains("not in a level", LuaBox.Refused(Status.NotFound, rpcs3: false).Line);
    }

    [Fact]
    public void TheLuaBoxStatusLineSaysWhereTheHelperIs()
    {
        Assert.Contains("Asking", LuaBox.StatusLines(null, null, false, debug: true).Single().Text);
        Assert.Contains("NOT_INGAME", LuaBox.StatusLines(null, Status.NotIngame, false, debug: true).Single().Text);

        var none = new LuaStatusInfo(LuaInstalled.No, LuaPhase.Idle, 0, 0, 0, 0, "", "");
        Assert.Contains("first Run", LuaBox.StatusLines(none, null, rpcs3: false, debug: true).Single().Text);
        Assert.Contains("RPCS3 patch", LuaBox.StatusLines(none, null, rpcs3: true, debug: true).Single().Text);
        Assert.Contains("still checking",
            LuaBox.StatusLines(none with { Installed = LuaInstalled.Checking }, null, true, debug: true).Single().Text);

        var running = new LuaStatusInfo(LuaInstalled.Yes, LuaPhase.Idle, 4321, 0x102043D0, 2, 0, "hud.lua", "bad argument");
        var lines = LuaBox.StatusLines(running, null, false, debug: true);
        Assert.Contains("Heartbeat 4321", lines[0].Text);
        Assert.Contains("0x102043D0", lines[0].Text);
        Assert.Contains("hud.lua", lines[1].Text);
        Assert.Contains("bad argument", lines[1].Text);
    }

    [Fact]
    public void TheHelperStatusShowsInDebugModeOnly()
    {
        // Out of debug mode the helper's own state says nothing: no heartbeat, no lua_State, no "asking".
        var running = new LuaStatusInfo(LuaInstalled.Yes, LuaPhase.Idle, 4321, 0x102043D0, 0, 0, "", "");
        Assert.Empty(LuaBox.StatusLines(running, null, false, debug: false));
        Assert.Empty(LuaBox.StatusLines(null, null, false, debug: false));
        Assert.Empty(LuaBox.StatusLines(running with { Installed = LuaInstalled.Checking }, null, true, debug: false));

        // What a user needs still shows: a refusal, a helper not in the game, a hung chunk, script errors.
        Assert.Contains("NOT_INGAME", LuaBox.StatusLines(null, Status.NotIngame, false, debug: false).Single().Text);
        var none = running with { Installed = LuaInstalled.No };
        Assert.Contains("first Run", LuaBox.StatusLines(none, null, rpcs3: false, debug: false).Single().Text);
        Assert.Contains("RPCS3 patch", LuaBox.StatusLines(none, null, rpcs3: true, debug: false).Single().Text);
        var hung = LuaBox.StatusLines(running with { Phase = LuaPhase.Hung }, null, false, debug: false).Single();
        Assert.Equal(LuaBox.LineKind.Error, hung.Kind);
        Assert.DoesNotContain("Heartbeat", hung.Text);
        var errors = LuaBox.StatusLines(running with { ErrorSeq = 2, ErrorScript = "hud.lua", Error = "bad argument" },
            null, false, debug: false);
        Assert.Contains("bad argument", errors.Single().Text);
    }

    [Fact]
    public async Task ToDDescribesItsAutosplitter()
    {
        using var server = new FakeQwarkServer();
        server.UseTod();
        server.Start();
        using var client = await ConnectAsync(server);

        var rows = await client.AutosplitDescribeAsync();
        Assert.Equal(new byte[] { 1, 7, 5, 6, 4, 3 }, rows.Select(r => r.Code).ToArray());
        Assert.True(rows[0].PlanetRoute && rows[0].EnabledByDefault);
        Assert.Equal("Tachyon defeated", rows[1].Label);
        Assert.True(rows[1].EnabledByDefault);
        Assert.DoesNotContain(rows, r => r.Code == 2 || r.Code == 8 || r.Label == "Ending FMV" || r.Label == "Final fade to black");
        Assert.False(rows[2].EnabledByDefault || rows[3].EnabledByDefault);
        Assert.Equal(AutosplitKind.LoadStart, rows[4].Kind);
        Assert.True(rows[4].Hold && !rows[4].Normalise && !rows[4].Flat && rows[4].ParamUs == 0);
        Assert.True(rows[4].IsTiming);
        Assert.Equal("Long load", rows[4].Label);
        Assert.Equal(AutosplitKind.Pause, rows[5].Kind);
        Assert.Equal(19_950_000u, rows[5].ParamUs);
    }

    // ------------------------------------------- the savefile manager, revision 1.28

    [Fact]
    public async Task ToDHasTheSaveFilePanelsTwoFlaggedActions()
    {
        using var server = new FakeQwarkServer();
        server.UseTod();
        server.Start();
        using var client = await ConnectAsync(server);

        var describe = await client.DescribeAsync();
        Assert.True(describe.HasSaveFileHelper);
        Assert.Equal((byte)40, describe.SaveAsideAction!.Id);
        Assert.Equal((byte)41, describe.LoadAsideAction!.Id);
        Assert.Equal("Savefile", describe.Groups[describe.SaveAsideAction.Group]);

        var info = await client.SaveFileInfoAsync();
        Assert.True(info.Supported && info.Installed && info.Running);
        Assert.Equal((uint)FakeQwarkServer.TodSaveSize, info.Size);
    }

    [Fact]
    public async Task UnderRpcs3ToDsSaveFilesNeedNoPatchAndRoundTrip()
    {
        using var server = new FakeQwarkServer { Emulator = true, NoCodePatches = true };
        server.UseTod();
        server.Start();
        using var client = await ConnectAsync(server);
        var root = Path.Combine(Path.GetTempPath(), "racman-tod-saves-" + Guid.NewGuid().ToString("N"));

        try
        {
            // The game's own save system: in on every platform, and nothing for RPCS3 to patch in.
            var info = await client.SaveFileInfoAsync();
            Assert.True(info.Supported && info.Installed);
            var rpcs3 = SessionInfo.Empty with { Flags = SessionFlags.Emulator | SessionFlags.NoCodePatches };
            Assert.True(SaveFilesPanel.HelperIn(rpcs3, info));
            Assert.False(GamePanel.AsideBlocked(rpcs3, info));
            var patch = await Assert.ThrowsAsync<QwarkStatusException>(() => client.SaveFilePatchAsync());
            Assert.Equal(Status.Unsupported, patch.Status);

            // Save to a slot and load it back, as the Save files panel does for any game.
            var library = new SaveFileLibrary(root);
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");
            await SaveFileTransfer.StoreAsync(client, library, FakeQwarkServer.TodTitleId, "any%", "metropolis.sav",
                mirror: true);
            var saved = library.Read(FakeQwarkServer.TodTitleId, "any%", "metropolis.sav");
            Assert.Equal(FakeQwarkServer.TodSaveSize, saved.Length);

            var entry = Assert.Single(SaveFileMerge.Build(
                await client.SaveFileListAsync("any%"), library.FilesWithCrc(FakeQwarkServer.TodTitleId, "any%")));
            Assert.False(await SaveFileTransfer.LoadAsync(client, library, FakeQwarkServer.TodTitleId, "any%", entry));
            Assert.Equal(saved, server.LoadedSaveFile);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
