using System.Buffers.Binary;
using System.Diagnostics;

using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Game modules (revision 1.26, qwark's PROTOCOL.md 9.11): a test build's games are SPRX files of
/// their own, <c>/dev_hdd0/qwark/rac1.sprx</c> .. <c>rac4.sprx</c>. SessionInfo flags2 bit5 says the running
/// game's module is not to be had and bit6 that it waits for memory, GAME_MODULE (0x0008) says
/// which game and why, and the client ships, installs and sends the modules exactly as it does
/// ui.pak. Since qwark build 57 a release ships them too, and its packaging refuses a release dist
/// that lacks any of them or a ui.pak with Tools of Destruction's Lua helper.
/// </summary>
public class GameModuleTests
{
    private const byte Current = QwarkClient.ExpectedQwarkBuild;

    /// <summary>The file ops in the request log, in order.</summary>
    private static Opcode[] FileOps(FakeQwarkServer server) =>
        server.RequestLog().Where(op => (ushort)op is >= 0x0070 and <= 0x0079).ToArray();

    private static Opcode[] Writes(FakeQwarkServer server) => server.RequestLog()
        .Where(op => op is Opcode.FileWrite or Opcode.FileDelete or Opcode.FileRename or Opcode.DirDelete)
        .ToArray();

    /// <summary>A signed-looking module: the SELF magic and filler, which is all the client reads.</summary>
    private static byte[] Sprx(byte seed, int length = 6000)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 17 + seed);
        GameModule.SelfMagic.CopyTo(bytes);
        return bytes;
    }

    /// <summary>
    /// A client folder of its own, with a qwark.sprx, a good ui.pak and the given game modules
    /// beside it: never the test data folder itself, where other tests keep their SPRX.
    /// </summary>
    private static string ClientCopy(params (GameId Game, byte[] Bytes)[] modules)
    {
        string folder = Path.Combine(TestDataFolder.Root, $"modules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        string sprx = Path.Combine(folder, WebManLoader.SprxName);
        File.WriteAllBytes(sprx, Sprx(1, 8000));
        File.WriteAllBytes(Path.Combine(folder, UiPak.FileName), FakeQwarkServer.UiPakFor(Current));
        foreach (var (game, bytes) in modules) File.WriteAllBytes(GameModule.BesideSprx(sprx, game), bytes);
        return sprx;
    }

    /// <summary>A console on this client's build whose games are modules, with its ui.pak in place.</summary>
    private static FakeQwarkServer ModuleConsole(byte build = Current)
    {
        var server = new FakeQwarkServer { GameModules = true };
        server.Session = server.Session with { QwarkVersion = build };
        server.PutUiPak();
        return server;
    }

    private static async Task<QwarkClient> ClientAsync(FakeQwarkServer server)
    {
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return client;
    }

    private static async Task<AppState> StateAsync(FakeQwarkServer server, string sprxPath, Settings? settings = null)
    {
        settings ??= new Settings();
        settings.Ps3.AutoReconnect = false;
        settings.Ps3.SprxPath = sprxPath;

        var state = new AppState(settings);
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);
        return state;
    }

    /// <summary>Frames, the way the render loop runs them, keeping every toast that went up.</summary>
    private sealed class Frames
    {
        public HashSet<Toast> Seen { get; } = new();

        public async Task<bool> UntilAsync(AppState state, Func<bool> condition, float delta = 1f / 60f, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                state.Tick(delta);
                Seen.UnionWith(state.Toasts);
                if (condition()) return true;
                await Task.Delay(16);
            }

            state.Tick(delta);
            Seen.UnionWith(state.Toasts);
            return condition();
        }

        public async Task ForAsync(AppState state, float seconds, float delta = 0.25f)
        {
            for (float elapsed = 0; elapsed < seconds; elapsed += delta)
            {
                state.Tick(delta);
                Seen.UnionWith(state.Toasts);
                await Task.Delay(16);
            }
        }

        public Toast[] Errors => Seen.Where(t => t.Kind == ToastKind.Error).ToArray();
    }

    // ---------------------------------------------------------------- the wire

    [Fact]
    public void TheTwoBitsAreFlags2Bits5And6()
    {
        Assert.Equal(0x20, (int)SessionFlags2.GameModuleBad);
        Assert.Equal(0x40, (int)SessionFlags2.GameWaiting);
        Assert.Equal(0x0008, (int)Opcode.GameModule);

        var ingame = SessionInfo.Empty with { State = SessionState.Ingame, TitleId = "NPEA00423" };

        var bad = SessionInfo.Parse((ingame with { Flags2 = SessionFlags2.GameModuleBad }).ToBytes());
        Assert.True(bad.GameModuleBad);
        Assert.False(bad.GameWaiting);
        Assert.True(bad.GameModulePending);
        Assert.False(bad.DevBuild);
        Assert.False(bad.UsbMods);
        Assert.Equal(ProfileFlags.None, bad.ProfileFlags);

        var waiting = SessionInfo.Parse((ingame with { Flags2 = SessionFlags2.GameWaiting | SessionFlags2.Dev }).ToBytes());
        Assert.True(waiting.GameWaiting);
        Assert.True(waiting.DevBuild);
        Assert.True(waiting.GameModulePending);

        // Byte 29 as it is on the wire: bit5 and bit6 beside DEV, and the profile bits apart.
        var raw = ingame.ToBytes();
        raw[29] = 0x7E;
        var all = SessionInfo.Parse(raw);
        Assert.True(all.GameModuleBad && all.GameWaiting && all.DevBuild);
        Assert.Equal(SessionInfo.ProfileBits, all.ProfileFlags);
        Assert.Equal(0x7E, all.ToBytes()[29]);

        // INGAME only: the bits outside a game are nothing to act on.
        Assert.False((SessionInfo.Empty with { Flags2 = SessionFlags2.GameModuleBad }).GameModulePending);
    }

    [Fact]
    public void APendingGameIsNotCalledAnUnknownGame()
    {
        var pending = SessionInfo.Empty with
        {
            State = SessionState.Ingame,
            TitleId = "NPEA00423",
            Flags2 = SessionFlags2.GameWaiting,
        };

        Assert.True(pending.IsUnknownGame);
        Assert.NotEqual("Unknown game", pending.GameName);
        Assert.Equal("Unknown game", (pending with { Flags2 = SessionFlags2.None }).GameName);
    }

    /// <summary>The 40-byte reply, laid out by hand: four bytes, then nine big-endian u32s.</summary>
    [Fact]
    public void TheReplyIsReadAsSection512LaysItOut()
    {
        var bytes = new byte[GameModuleInfo.WireSize + 8];
        bytes[0] = 3;
        bytes[1] = 4;
        bytes[2] = 1;
        bytes[3] = 0x06;
        uint[] words = { 212, 660, 128, 128, 3000, 3010, 0, 12_000, 0x23010500 };
        for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4 + i * 4), words[i]);

        // A longer reply is a later revision's, and its first 40 bytes are these.
        bytes[^1] = 0xEE;

        var info = GameModuleInfo.Parse(bytes);
        Assert.Equal(GameModuleState.Waiting, info.State);
        Assert.Equal(GameId.Rac4, info.Game);
        Assert.Equal(1, info.Pages);
        Assert.Equal(GameModuleFlags.Notified | GameModuleFlags.Modules, info.Flags);
        Assert.Equal(212u, info.LowestKb);
        Assert.Equal(660u, info.NowKb);
        Assert.Equal(128u, info.NeedKb);
        Assert.Equal(128u, info.FloorKb);
        Assert.Equal(3000u, info.WindowMs);
        Assert.Equal(3010u, info.WaitedMs);
        Assert.Equal(0u, info.Error);
        Assert.Equal(12_000u, info.LoadMicroseconds);
        Assert.Equal(0x23010500u, info.ModuleId);

        Assert.Equal(256u, info.RequiredKb);
        Assert.Equal(44u, info.ShortKb);
        Assert.True(info.IsWaiting);
        Assert.False(info.IsBad);

        Assert.Equal(bytes[..GameModuleInfo.WireSize], info.ToBytes());
        Assert.Throws<ProtocolException>(() => GameModuleInfo.Parse(bytes.AsSpan(0, GameModuleInfo.WireSize - 1)));
        Assert.Equal(0u, (info with { LowestKb = 900 }).ShortKb);
    }

    [Fact]
    public void TheFilesAreNamedByGameIdBesideUiPak()
    {
        Assert.Equal("rac1.sprx", GameModule.FileName(GameId.Rac1));
        Assert.Equal("rac3.sprx", GameModule.FileName(GameId.Rac3));
        Assert.Equal("rac4.sprx", GameModule.FileName(4));
        Assert.Equal(new[] { "rac1.sprx", "rac2.sprx", "rac3.sprx", "rac4.sprx", "tod.sprx" }, GameModule.FileNames);
        Assert.Equal("tod.sprx", GameModule.FileName(GameId.Tod));
        Assert.Equal("/dev_hdd0/qwark/tod.sprx.new", GameModule.StagedPath(GameId.Tod));
        Assert.True(GameModule.HasModule(GameId.Tod));
        Assert.Equal("/dev_hdd0/qwark/rac4.sprx", GameModule.ConsolePath(GameId.Rac4));
        Assert.Equal("/dev_hdd0/qwark/rac2.sprx.new", GameModule.StagedPath(GameId.Rac2));
        Assert.False(GameModule.HasModule(GameId.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => GameModule.FileName(GameId.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => GameModule.FileName(6));

        string sprx = Path.Combine(TestDataFolder.Root, "somewhere", WebManLoader.SprxName);
        Assert.Equal(Path.Combine(TestDataFolder.Root, "somewhere", "rac3.sprx"), GameModule.BesideSprx(sprx, GameId.Rac3));

        Assert.True(GameModule.LooksLikeSprx(Sprx(2)));
        Assert.False(GameModule.LooksLikeSprx(new byte[4096]));
        Assert.False(GameModule.LooksLikeSprx(Sprx(2, 32)));
        Assert.Equal("RaC1, RaC2 and Deadlocked", GameModule.Names(new[] { GameId.Rac1, GameId.Rac2, GameId.Rac4 }));
    }

    [Fact]
    public async Task TheModulesBesideTheClientAreTheOnesThereAndNoneIsNoError()
    {
        var none = await GameModule.ReadBesideAsync(ClientCopy());
        Assert.Empty(none);

        var a = Sprx(3);
        var b = Sprx(4);
        string sprx = ClientCopy((GameId.Rac4, b), (GameId.Rac2, a));
        var found = await GameModule.ReadBesideAsync(sprx);

        Assert.Equal(new[] { GameId.Rac2, GameId.Rac4 }, found.Select(m => m.Game));
        Assert.Equal(a, found[0].Bytes);
        Assert.Equal(new[] { GameId.Rac2, GameId.Rac4 }, GameModule.Beside(sprx));
    }

    // ---------------------------------------------------------------- the fake console

    /// <summary>A module whose games are linked in, as a release is: no bit, the game named, LINKED.</summary>
    [Fact]
    public async Task AReleaseNeverSetsTheBitsAndAnswersLinked()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var client = await ClientAsync(server);

        var session = (await client.GetStateAsync()).Session;
        Assert.False(session.GameModuleBad || session.GameWaiting);
        Assert.Equal(GameId.Rac1, session.Game);

        var info = await client.GameModuleAsync();
        Assert.Equal(GameModuleState.Linked, info.State);
        Assert.Equal(GameModuleFlags.None, info.Flags & GameModuleFlags.Modules);
        Assert.True(FakeQwarkServer.IsControlOp(Opcode.GameModule));
    }

    [Fact]
    public async Task AMissingModuleIsBit5AndGame0UntilTheFileIsWritten()
    {
        using var server = ModuleConsole();
        server.Start();
        using var client = await ClientAsync(server);

        var session = (await client.GetStateAsync()).Session;
        Assert.True(session.GameModuleBad);
        Assert.Equal(GameId.None, session.Game);
        Assert.Equal("NPEA00385", session.TitleId);

        var info = await client.GameModuleAsync();
        Assert.Equal(GameModuleState.Missing, info.State);
        Assert.Equal(GameId.Rac1, info.Game);

        // Every game op is refused meanwhile, as for a title with no game at all.
        var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.DescribeAsync());
        Assert.Equal(Status.Unsupported, refused.Status);

        await client.WriteFileAsync(GameModule.ConsolePath(GameId.Rac1), FakeQwarkServer.GameModuleFor(Current, GameId.Rac1));
        session = (await client.GetStateAsync()).Session;
        Assert.False(session.GameModuleBad);
        Assert.Equal(GameId.Rac1, session.Game);
        Assert.Equal(GameModuleState.Loaded, (await client.GameModuleAsync()).State);
    }

    [Theory]
    [InlineData("other", GameModuleState.Other, GameModuleInfo.OtherBuildNumber)]
    [InlineData("damaged", GameModuleState.Failed, 0u)]
    [InlineData("game", GameModuleState.Failed, 0x80000100u)]
    public async Task ABadModuleSaysWhy(string kind, GameModuleState expected, uint error)
    {
        using var server = ModuleConsole();
        server.Files[GameModule.ConsolePath(GameId.Rac1)] = kind switch
        {
            "other" => FakeQwarkServer.GameModuleFor((byte)(Current - 1), GameId.Rac1),
            "damaged" => new byte[4096],
            _ => FakeQwarkServer.GameModuleFor(Current, GameId.Rac2),
        };
        server.Start();
        using var client = await ClientAsync(server);

        Assert.True((await client.GetStateAsync()).Session.GameModuleBad);
        var info = await client.GameModuleAsync();
        Assert.Equal(expected, info.State);
        Assert.Equal(error, info.Error);
        Assert.True(info.IsBad);
    }

    [Fact]
    public async Task AWaitingModuleIsBit6AndTheGameArrivesWhenTheGatePasses()
    {
        using var server = ModuleConsole();
        server.PutGameModule(GameId.Rac1);
        server.WaitsForMemory = true;
        server.Start();
        using var client = await ClientAsync(server);

        var session = (await client.GetStateAsync()).Session;
        Assert.True(session.GameWaiting);
        Assert.False(session.GameModuleBad);
        Assert.Equal(GameId.None, session.Game);

        var info = await client.GameModuleAsync();
        Assert.Equal(GameModuleState.Waiting, info.State);
        Assert.Equal(212u, info.LowestKb);
        Assert.Equal(256u, info.RequiredKb);

        server.WaitsForMemory = false;
        session = (await client.GetStateAsync()).Session;
        Assert.False(session.GameWaiting);
        Assert.Equal(GameId.Rac1, session.Game);
    }

    /// <summary>GAME_MODULE is a control op: a launch, which refuses everything else BUSY, answers it.</summary>
    [Fact]
    public async Task GameModuleIsAnsweredThroughALaunch()
    {
        using var server = ModuleConsole();
        server.Start();
        using var client = await ClientAsync(server);

        server.Booting = true;
        Assert.Equal(GameModuleState.None, (await client.GameModuleAsync()).State);
        await Assert.ThrowsAsync<QwarkStatusException>(() => client.DescribeAsync());
    }

    /// <summary>A staged module is the game's own the next time it boots, as the core does.</summary>
    [Fact]
    public void AStagedModuleIsTakenWhenTheGameBoots()
    {
        var staged = FakeQwarkServer.GameModuleFor(Current, GameId.Rac1);

        using var server = ModuleConsole();
        server.Files[GameModule.StagedPath(GameId.Rac1)] = staged;
        server.Session = server.Session with { Generation = server.Session.Generation + 1 };

        Assert.Equal(staged, server.Files[GameModule.ConsolePath(GameId.Rac1)]);
        Assert.False(server.Files.ContainsKey(GameModule.StagedPath(GameId.Rac1)));
    }

    // ---------------------------------------------------------------- the send

    [Fact]
    public async Task TheModuleGoesUpAsNewIsReadBackAndIsThenRenamedIntoPlace()
    {
        var module = FakeQwarkServer.GameModuleFor(Current, GameId.Rac1);

        using var server = ModuleConsole();
        server.Files[GameModule.ConsolePath(GameId.Rac1)] = new byte[100];
        server.Start();
        using var client = await ClientAsync(server);
        Assert.True((await client.GetStateAsync()).Session.GameModuleBad);

        var said = new List<string>();
        server.ClearRequestLog();
        var result = await GameModuleSend.SendAsync(client, GameId.Rac1, module, new InlineProgress<string>(said.Add));

        Assert.Equal(
            new[]
            {
                Opcode.FileOpen, Opcode.FileWrite, Opcode.FileClose,
                Opcode.FileOpen, Opcode.FileRead, Opcode.FileClose,
                Opcode.FileDelete, Opcode.FileRename,
            },
            FileOps(server));

        Assert.True(result.ReplacedACopy);
        Assert.Equal(GameId.Rac1, result.Game);
        Assert.Equal(module, server.Files[GameModule.ConsolePath(GameId.Rac1)]);
        Assert.False(server.Files.ContainsKey(GameModule.StagedPath(GameId.Rac1)));
        Assert.Equal(
            new[]
            {
                "Uploading rac1.sprx to /dev_hdd0/qwark/rac1.sprx.new",
                "Putting /dev_hdd0/qwark/rac1.sprx.new in place of /dev_hdd0/qwark/rac1.sprx",
            },
            said);

        var session = (await client.GetStateAsync()).Session;
        Assert.False(session.GameModuleBad);
        Assert.Equal(GameId.Rac1, session.Game);
    }

    [Fact]
    public async Task ALocalCopyThatIsNoModuleSendsNothing()
    {
        using var server = ModuleConsole();
        server.Start();
        using var client = await ClientAsync(server);
        server.ClearRequestLog();

        var junk = await Assert.ThrowsAsync<GameModuleSendException>(
            () => GameModuleSend.SendAsync(client, GameId.Rac1, new byte[4096]));
        Assert.Equal(GameModuleSendStep.Local, junk.Step);
        Assert.Contains("is not a signed SPRX", junk.Message, StringComparison.Ordinal);

        string missing = Path.Combine(TestDataFolder.Root, $"absent-{Guid.NewGuid():N}", "rac1.sprx");
        var none = await Assert.ThrowsAsync<GameModuleSendException>(() => GameModuleSend.RunAsync(client, GameId.Rac1, missing));
        Assert.Equal(GameModuleSendStep.Local, none.Step);
        Assert.Contains("There is no rac1.sprx beside this client", none.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<GameModuleSendException>(() => GameModuleSend.SendAsync(client, GameId.None, Sprx(1)));

        Assert.Empty(FileOps(server));
    }

    // ---------------------------------------------------------------- the standalone update and the boot install

    [Fact]
    public async Task TheUpdateStagesEachModuleAsNewBeforeTheModule()
    {
        using var server = new FakeQwarkServer();
        server.Files[WebManLoader.BootPath] = Sprx(90, 4096);
        server.Files[GameModule.ConsolePath(GameId.Rac4)] = Sprx(91, 2048);
        server.Start();
        using var client = await ClientAsync(server);

        var one = Sprx(10);
        var four = Sprx(40);
        var said = new List<string>();
        var result = await SprxUpdate.RunAsync(client, Sprx(5, 9000), FakeQwarkServer.UiPakFor(Current),
            new InlineProgress<string>(said.Add),
            gameModules: new[] { new GameModuleFile(GameId.Rac1, one), new GameModuleFile(GameId.Rac4, four) });

        Assert.Equal(2, result.GameModulesStaged);
        Assert.True(result.UiPakStaged);
        Assert.Equal(one, server.Files[GameModule.StagedPath(GameId.Rac1)]);
        Assert.Equal(four, server.Files[GameModule.StagedPath(GameId.Rac4)]);

        // The running module keeps the copies it has until the game boots on the new one.
        Assert.Equal(Sprx(91, 2048), server.Files[GameModule.ConsolePath(GameId.Rac4)]);
        Assert.False(server.Files.ContainsKey(GameModule.ConsolePath(GameId.Rac1)));

        int pak = said.FindIndex(s => s.Contains(UiPak.FileName, StringComparison.Ordinal));
        int module = said.FindIndex(s => s.Contains("rac1.sprx", StringComparison.Ordinal));
        int sprx = said.FindIndex(s => s.StartsWith($"Uploading {WebManLoader.SprxName}", StringComparison.Ordinal));
        Assert.True(pak < module && module < sprx, string.Join(" | ", said));
    }

    [Fact]
    public async Task AReleaseUpdateStagesNoModule()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var client = await ClientAsync(server);

        var result = await SprxUpdate.RunAsync(client, Sprx(5), FakeQwarkServer.UiPakFor(Current));

        Assert.Equal(0, result.GameModulesStaged);
        Assert.DoesNotContain(server.Files.Keys, IsModulePath);
    }

    [Fact]
    public async Task AFailedModuleUploadStopsTheUpdateBeforeTheBootCopy()
    {
        var boot = Sprx(90, 4096);
        using var server = new FakeQwarkServer();
        server.Files[WebManLoader.BootPath] = boot;
        server.Start();
        using var client = await ClientAsync(server);

        // No pak, so the first write is the module's, and it is refused.
        server.FileWriteStatus = Status.IoError;
        var error = await Assert.ThrowsAsync<SprxUpdateException>(() => SprxUpdate.RunAsync(client, Sprx(5), null,
            gameModules: new[] { new GameModuleFile(GameId.Rac2, Sprx(20)) }));

        Assert.Equal(SprxUpdateStep.GameModule, error.Step);
        Assert.Contains("rac2.sprx", error.Message, StringComparison.Ordinal);
        Assert.Equal(boot, server.Files[WebManLoader.BootPath]);
    }

    /// <summary>The standalone update the client runs by itself carries the modules beside its qwark.sprx.</summary>
    [Fact]
    public async Task TheAutomaticUpdateCarriesTheModulesBesideTheClient()
    {
        var two = Sprx(22);
        using var server = new FakeQwarkServer();
        server.Files[WebManLoader.BootPath] = Sprx(90, 4096);
        server.Start();

        var settings = new Settings { Ps3 = { StandaloneConnection = true } };
        using var state = await StateAsync(server, ClientCopy((GameId.Rac2, two)), settings);
        var frames = new Frames();

        Assert.True(await frames.UntilAsync(state, () => state.QwarkUpdateStaged > 0));
        Assert.Equal(two, server.Files[GameModule.StagedPath(GameId.Rac2)]);
        Assert.Empty(frames.Errors);
    }

    [Fact]
    public async Task TheBootInstallSendsTheModulesBesideTheClientToo()
    {
        using var ftp = new FakeFtpServer();
        ftp.Start();

        var one = Sprx(11);
        var three = Sprx(33);
        var said = new List<string>();
        var result = await new WebManLoader { FtpPort = ftp.Port }.InstallToBootAsync(
            "127.0.0.1", ClientCopy((GameId.Rac1, one), (GameId.Rac3, three)), new InlineProgress<string>(said.Add));

        Assert.Equal(2, result.GameModulesSent);
        Assert.True(result.UiPakSent);
        Assert.Equal(one, ftp.Stored[GameModule.ConsolePath(GameId.Rac1)]);
        Assert.Equal(three, ftp.Stored[GameModule.ConsolePath(GameId.Rac3)]);
        Assert.Contains("Uploading rac3.sprx to /dev_hdd0/qwark/rac3.sprx", said);
        Assert.Contains("Sent 2 game modules with it.", ConnectionPanel.BootInstallMessage(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReleaseBootInstallSendsNoModuleAndSaysNothingOfThem()
    {
        using var ftp = new FakeFtpServer();
        ftp.Start();

        var said = new List<string>();
        var result = await new WebManLoader { FtpPort = ftp.Port }.InstallToBootAsync(
            "127.0.0.1", ClientCopy(), new InlineProgress<string>(said.Add));

        Assert.Equal(0, result.GameModulesSent);
        Assert.DoesNotContain(ftp.Stored.Keys, IsModulePath);
        Assert.DoesNotContain(said, s => GameModule.FileNames.Any(n => s.Contains(n, StringComparison.Ordinal)));
        Assert.DoesNotContain("game module", ConnectionPanel.BootInstallMessage(result), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- what the client says

    public static IEnumerable<object?[]> Problems() => new[]
    {
        new object?[] { GameModuleState.Missing, 0u, "Your console is missing rac4.sprx, so Deadlocked has no game features." },
        new object?[] { GameModuleState.Failed, 0u, "rac4.sprx on your console is damaged, so Deadlocked" },
        new object?[] { GameModuleState.Failed, 0x80010005u, "rac4.sprx on your console would not load (error 0x80010005)" },
        new object?[] { GameModuleState.Other, 3u, "rac4.sprx on your console is from another build, so Deadlocked" },
    };

    [Theory]
    [MemberData(nameof(Problems))]
    public void TheProblemNamesTheFileTheGameAndWhy(GameModuleState state, uint error, string says)
    {
        var info = new GameModuleInfo(state, GameId.Rac4, 1, GameModuleFlags.Modules, 0, 0, 128, 128, 3000, 0, error, 0, 0);
        string text = AppState.GameModuleProblem(info, "NPEA00423");

        Assert.Contains(says, text, StringComparison.Ordinal);
        Assert.StartsWith("Deadlocked support", AppState.GameModuleProblemShort(info, "NPEA00423"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWaitingLineHasTheFiguresTheGateUses()
    {
        var info = new GameModuleInfo(GameModuleState.Waiting, GameId.Rac4, 1, GameModuleFlags.Modules,
            212, 660, 128, 128, 3000, 3010, 0, 0, 0);

        Assert.Equal("Deadlocked is loading (212KB free, 256KB needed)", AppState.GameModuleWaitingText(info, "NPEA00423"));

        // Before the first answer the title id stands in for the game, and there are no figures,
        // and nothing trails the words.
        Assert.Equal("NPEA00423 is loading", AppState.GameModuleWaitingText(null, "NPEA00423"));
        Assert.Equal("this game", AppState.PendingGameName(GameId.None, string.Empty));
    }

    [Fact]
    public void ThePendingGameKeepsTheGamePanelAndNoOtherGamePanel()
    {
        Assert.True(PanelNav.Visible(PanelNav.Game, unknownGame: true, false, false, gameModulePending: true));
        Assert.False(PanelNav.Visible(PanelNav.Mods, unknownGame: true, false, false, gameModulePending: true));
        Assert.True(PanelNav.Visible(PanelNav.Memory, unknownGame: true, false, false, gameModulePending: true));
        Assert.False(PanelNav.Visible(PanelNav.Game, unknownGame: true, false, false));
        Assert.Equal(PanelNav.Game, PanelNav.Fallback(unknownGame: true, gameModulePending: true));
        Assert.Equal(PanelNav.Memory, PanelNav.Fallback(unknownGame: true));
    }

    // ---------------------------------------------------------------- the client around it

    /// <summary>
    /// A console on this client's build with the running game's module missing: GAME_MODULE names
    /// the game, one error toast says so with the fix, the Send button is offered, and a send puts
    /// the game back without a restart, described as any game is.
    /// </summary>
    [Fact]
    public async Task AMissingModuleIsToldOnceAndTheButtonSendsIt()
    {
        var module = FakeQwarkServer.GameModuleFor(Current, GameId.Rac1);

        using var server = ModuleConsole();
        server.Start();

        using var state = await StateAsync(server, ClientCopy((GameId.Rac1, module)));
        var frames = new Frames();

        Assert.True(await frames.UntilAsync(state, () => state.GameModuleReport is not null && state.GameModuleNeeded));
        Assert.True(state.GameModulePending);
        Assert.False(state.GameModuleWaiting);
        Assert.Equal(GameId.Rac1, state.PendingGame);
        Assert.True(state.GameModuleCanSend);
        Assert.EndsWith(" | RaC1", state.StatusLine().Split(" | combos")[0], StringComparison.Ordinal);
        Assert.DoesNotContain("no game support", state.StatusLine(), StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown game", state.StatusLine(), StringComparison.Ordinal);
        Assert.Contains("Your console is missing rac1.sprx", state.GameModuleStatusText(), StringComparison.Ordinal);

        await frames.ForAsync(state, 1.5f);
        var told = Assert.Single(frames.Errors);
        Assert.Contains("Your console is missing rac1.sprx, so RaC1 has no game features.", told.Text, StringComparison.Ordinal);
        Assert.Contains("Send it from the Connection panel.", told.Text, StringComparison.Ordinal);

        state.SendGameModule();
        Assert.True(state.GameModuleSending);
        Assert.True(await frames.UntilAsync(state, () => !state.GameModulePending && !state.GameModuleSending
                                                       && state.Describe.Features.Length > 0));

        Assert.Equal(module, server.Files[GameModule.ConsolePath(GameId.Rac1)]);
        Assert.Contains(frames.Seen, t => t.Kind == ToastKind.Success && t.Text.StartsWith("Sent rac1.sprx", StringComparison.Ordinal));
        Assert.Null(state.GameModuleReport);

        await frames.ForAsync(state, AppState.GameModuleCheckSeconds + 1);
        Assert.Single(frames.Errors);
    }

    /// <summary>
    /// A module waiting for memory: the figures, no toast and no button. When the gate passes the
    /// game arrives and is described, and nothing of the wait is left.
    /// </summary>
    [Fact]
    public async Task AWaitingModuleIsShownInPlainWordsAndTheGameArrivesByItself()
    {
        using var server = ModuleConsole();
        server.PutGameModule(GameId.Rac1);
        server.WaitsForMemory = true;
        server.Start();

        using var state = await StateAsync(server, ClientCopy((GameId.Rac1, Sprx(1))));
        var frames = new Frames();

        Assert.True(await frames.UntilAsync(state, () => state.GameModuleReport is not null));
        Assert.True(state.GameModuleWaiting);
        Assert.False(state.GameModuleNeeded);
        Assert.False(state.GameModuleCanSend);
        Assert.Equal("RaC1 is loading (212KB free, 256KB needed)", state.GameModuleStatusText());
        Assert.EndsWith(" | RaC1", state.StatusLine().Split(" | combos")[0], StringComparison.Ordinal);
        Assert.DoesNotContain("waiting for memory", state.StatusLine(), StringComparison.Ordinal);

        // Asked about once a second, not every frame.
        int asked = server.GameModuleCount;
        await frames.ForAsync(state, 2f, 0.25f);
        Assert.InRange(server.GameModuleCount - asked, 1, 4);

        server.WaitsForMemory = false;
        Assert.True(await frames.UntilAsync(state, () => !state.GameModulePending && state.Describe.Features.Length > 0));
        Assert.Null(state.GameModuleReport);
        Assert.Empty(frames.Errors);
        Assert.DoesNotContain(frames.Seen, t => t.Text.Contains("support", StringComparison.Ordinal));
    }

    /// <summary>
    /// A release client against a test module of its own build has no module to send: the line is
    /// there, the button and the "send it" are not.
    /// </summary>
    [Fact]
    public async Task AClientWithNoModuleBesideItOffersNoSend()
    {
        using var server = ModuleConsole();
        server.Start();

        using var state = await StateAsync(server, ClientCopy());
        var frames = new Frames();

        Assert.True(await frames.UntilAsync(state, () => state.GameModuleReport is not null && state.GameModuleNeeded));
        Assert.False(state.GameModuleCanSend);

        await frames.ForAsync(state, 1f);
        var told = Assert.Single(frames.Errors);
        Assert.DoesNotContain("Send it", told.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A module that sets neither bit is never asked GAME_MODULE, and an older module whose bit is
    /// set is the stale-build line's business, not a send's.
    /// </summary>
    [Fact]
    public async Task AModuleWithoutTheBitsIsNeverAsked()
    {
        using var server = new FakeQwarkServer();
        server.Session = server.Session with { QwarkVersion = Current };
        server.PutUiPak();
        server.Start();

        using var state = await StateAsync(server, ClientCopy((GameId.Rac1, Sprx(1))));
        var frames = new Frames();
        await frames.ForAsync(state, 2f);

        Assert.DoesNotContain(Opcode.GameModule, server.RequestLog());
        Assert.False(state.GameModulePending);
        Assert.Empty(frames.Errors);
    }

    [Fact]
    public async Task AnOlderModuleIsNotOfferedASend()
    {
        using var server = ModuleConsole((byte)(Current - 1));
        server.Start();

        var settings = new Settings();
        using var state = await StateAsync(server, ClientCopy((GameId.Rac1, Sprx(1))), settings);
        var frames = new Frames();

        Assert.True(await frames.UntilAsync(state, () => state.GameModuleReport is not null));
        Assert.True(state.QwarkStale);
        Assert.True(state.GameModulePending);
        Assert.False(state.GameModuleNeeded);
        Assert.False(state.GameModuleCanSend);
    }

    /// <summary>A refusal of the op is never shown, and the op is not asked again on that connection.</summary>
    [Fact]
    public async Task UnknownOpIsNeverShown()
    {
        using var server = ModuleConsole();
        server.RefusedOps[Opcode.GameModule] = Status.UnknownOp;
        server.Start();

        using var state = await StateAsync(server, ClientCopy((GameId.Rac1, Sprx(1))));
        var frames = new Frames();
        await frames.ForAsync(state, 3f);

        Assert.Equal(1, server.RequestLog().Count(op => op == Opcode.GameModule));
        Assert.DoesNotContain(frames.Errors, t => t.Text.Contains(nameof(Status.UnknownOp), StringComparison.Ordinal));
        Assert.True(state.GameModuleNeeded);

        // Without an answer the game is not named, so nothing is offered to send.
        Assert.False(state.GameModuleCanSend);
        Assert.Contains("NPEA00385", state.GameModuleStatusText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedSendStepNamesItself()
    {
        using var server = ModuleConsole();
        server.FileWriteStatus = Status.IoError;
        server.Start();

        using var state = await StateAsync(server, ClientCopy((GameId.Rac1, Sprx(1))));
        var frames = new Frames();

        Assert.True(await frames.UntilAsync(state, () => state.GameModuleCanSend));
        await frames.ForAsync(state, 0.5f);
        int before = frames.Errors.Length;

        state.SendGameModule();
        Assert.True(await frames.UntilAsync(state, () => frames.Errors.Length > before && !state.GameModuleSending));

        var error = frames.Errors.Last(t => t.Text.StartsWith("Sending rac1.sprx", StringComparison.Ordinal)).Text;
        Assert.Contains("uploading it to /dev_hdd0/qwark/rac1.sprx.new", error, StringComparison.Ordinal);
        Assert.Contains(nameof(Status.IoError), error, StringComparison.Ordinal);
        Assert.True(state.GameModuleNeeded);
        Assert.Empty(Writes(server).Where(op => op is Opcode.FileRename));
    }

    // ---------------------------------------------------------------- the release scripts

    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "release.ps1"))) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join('/', parts));
    }

    /// <summary>A packaging script with its arguments: exit code and output, or null with no PowerShell here.</summary>
    private static (int Exit, string Output)? RunScript(string script, params string[] arguments)
    {
        foreach (var shell in OperatingSystem.IsWindows() ? new[] { "powershell.exe", "pwsh" } : new[] { "pwsh" })
        {
            var info = new ProcessStartInfo(shell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments))
            {
                info.ArgumentList.Add(arg);
            }

            Process? process;
            try
            {
                process = Process.Start(info);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                continue;
            }

            using (process)
            {
                var stdout = process!.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                Assert.True(process.WaitForExit(60_000), "the script finished");
                return (process.ExitCode, stdout.Result + stderr.Result);
            }
        }

        return null;
    }

    private static string Dist(bool? dev, params int[] modules)
    {
        var dist = Path.Combine(TestDataFolder.Root, $"dist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dist);
        if (dev is { } d)
        {
            File.WriteAllText(Path.Combine(dist, QwarkBuildInfo.FileName),
                $"# qwark.build\nbuild {Current}\ndev {(d ? 1 : 0)}\nid 0x1A2B3C4D\n");
        }

        foreach (int n in modules) File.WriteAllBytes(Path.Combine(dist, GameModule.FileName(n)), Sprx((byte)n));
        return dist;
    }

    private static (int Exit, string Output, string[] Staged)? Stage(string dist, params string[] alreadyThere)
    {
        var stage = Path.Combine(TestDataFolder.Root, $"stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        foreach (var name in alreadyThere) File.WriteAllBytes(Path.Combine(stage, name), new byte[] { 9 });

        if (RunScript(RepoFile("packaging", "stage-game-modules.ps1"),
                "-Source", dist, "-BuildInfo", Path.Combine(dist, QwarkBuildInfo.FileName), "-Destination", stage) is not { } run)
        {
            return null;
        }

        var staged = Directory.GetFiles(stage, "*.sprx").Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        return (run.Exit, run.Output, staged);
    }

    [Fact]
    public void ATestDistHasItsModulesStaged()
    {
        // An earlier stage's rac2.sprx goes, and so does a module under the first test builds' name.
        if (Stage(Dist(true, 1, 2, 3, 4, 5), "rac2.sprx", "game-1.sprx") is not { } result) return;

        Assert.True(result.Exit == 0, result.Output);
        Assert.Equal(new[] { "rac1.sprx", "rac2.sprx", "rac3.sprx", "rac4.sprx", "tod.sprx" }, result.Staged);
    }

    [Fact]
    public void ATestDistWithNoModulesStagesNoneAndDoesNotFail()
    {
        if (Stage(Dist(true)) is not { } test) return;
        Assert.True(test.Exit == 0, test.Output);
        Assert.Empty(test.Staged);
        Assert.Contains("none in", test.Output, StringComparison.Ordinal);

        // A test build short of some stages what it has, and says which games go without.
        if (Stage(Dist(true, 1, 4)) is not { } some) return;
        Assert.True(some.Exit == 0, some.Output);
        Assert.Equal(new[] { "rac1.sprx", "rac4.sprx" }, some.Staged);
        Assert.Contains("only 2 of 5", some.Output, StringComparison.Ordinal);
    }

    /// <summary>Since qwark build 57 a release's dist has its modules staged, all five, as a test build's are.</summary>
    [Fact]
    public void AReleaseDistHasItsModulesStaged()
    {
        if (Stage(Dist(false, 1, 2, 3, 4, 5), "rac4.sprx", "game-4.sprx") is not { } result) return;

        Assert.True(result.Exit == 0, result.Output);
        Assert.Equal(new[] { "rac1.sprx", "rac2.sprx", "rac3.sprx", "rac4.sprx", "tod.sprx" }, result.Staged);

        // A dist with no qwark.build stages what it has, as a test build's.
        if (Stage(Dist(dev: null, 2)) is not { } unknown) return;
        Assert.True(unknown.Exit == 0, unknown.Output);
        Assert.Equal(new[] { "rac2.sprx" }, unknown.Staged);
    }

    /// <summary>A release dist without every module stops the stage: a release must ship all five.</summary>
    [Fact]
    public void AReleaseDistShortOfAModuleIsNotStaged()
    {
        if (Stage(Dist(false, 1, 2, 3, 4), "rac4.sprx") is not { } result) return;

        Assert.True(result.Exit != 0, result.Output);
        Assert.Contains("tod.sprx", result.Output, StringComparison.Ordinal);

        if (Stage(Dist(false)) is not { } none) return;
        Assert.True(none.Exit != 0, none.Output);
    }

    /// <summary>A release dist with every module and ui.pak with ToD's Lua helper (check-qwark-build.ps1).</summary>
    private static string ReleaseDistFull()
    {
        string dist = Dist(false);
        ReleaseDist.Write(dist, Current);
        return dist;
    }

    private static (int Exit, string Output)? Check(string dist) =>
        RunScript(RepoFile("packaging", "check-qwark-build.ps1"), "-Dist", dist);

    [Fact]
    public void TheReleaseCheckTakesAReleaseDistWithEveryModuleAndToDsPak()
    {
        if (Check(ReleaseDistFull()) is not { } clean) return;
        Assert.True(clean.Exit == 0, clean.Output);
        Assert.Contains("5 game modules", clean.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseCheckRefusesAReleaseDistShortOfAModule()
    {
        foreach (var game in GameModule.Games)
        {
            string dist = ReleaseDistFull();
            File.Delete(Path.Combine(dist, GameModule.FileName(game)));
            if (Check(dist) is not { } result) return;
            Assert.True(result.Exit != 0, result.Output);
            Assert.Contains(GameModule.FileName(game), result.Output, StringComparison.Ordinal);
        }

        // A file in a module's place that is no signed module.
        string junk = ReleaseDistFull();
        File.WriteAllText(Path.Combine(junk, "rac3.sprx"), "not a module");
        if (Check(junk) is not { } notSelf) return;
        Assert.True(notSelf.Exit != 0, notSelf.Output);
        Assert.Contains("rac3.sprx", notSelf.Output, StringComparison.Ordinal);

        // A module under the first test builds' name, which no module reads.
        string legacy = ReleaseDistFull();
        File.WriteAllBytes(Path.Combine(legacy, "game-2.sprx"), Sprx(2));
        if (Check(legacy) is not { } old) return;
        Assert.True(old.Exit != 0, old.Output);
        Assert.Contains("game-2.sprx", old.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseCheckRefusesAUiPakWithoutToDsLuaHelper()
    {
        string noTod = ReleaseDistFull();
        File.WriteAllBytes(Path.Combine(noTod, UiPak.FileName), ReleaseDist.Pak(Current, tod: false));
        if (Check(noTod) is not { } result) return;
        Assert.True(result.Exit != 0, result.Output);
        Assert.Contains("Tools of Destruction", result.Output, StringComparison.Ordinal);

        string other = ReleaseDistFull();
        File.WriteAllBytes(Path.Combine(other, UiPak.FileName), ReleaseDist.Pak((byte)(Current - 1)));
        if (Check(other) is not { } stale) return;
        Assert.True(stale.Exit != 0, stale.Output);
        Assert.Contains($"build {Current - 1}", stale.Output, StringComparison.Ordinal);

        string none = ReleaseDistFull();
        File.Delete(Path.Combine(none, UiPak.FileName));
        if (Check(none) is not { } missing) return;
        Assert.True(missing.Exit != 0, missing.Output);
        Assert.Contains("ui.pak", missing.Output, StringComparison.Ordinal);
    }

    /// <summary>The packaging scripts' list of module names is the client's, in the same order.</summary>
    [Fact]
    public void ThePackagingScriptsNameTheModulesAsTheClientDoes()
    {
        string list = File.ReadAllText(RepoFile("packaging", "game-module-names.ps1"));
        var line = list.Split('\n').Single(l => l.TrimStart().StartsWith("$GameModuleNames", StringComparison.Ordinal));
        var names = System.Text.RegularExpressions.Regex.Matches(line, "'([^']+)'").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(GameModule.FileNames, names);
    }

    private static bool IsModulePath(string path) =>
        GameModule.FileNames.Any(n => path.EndsWith("/" + n, StringComparison.Ordinal)
                                      || path.EndsWith("/" + n + ".new", StringComparison.Ordinal));

    [Fact]
    public void PublishStagesTheModulesThroughTheScript()
    {
        string publish = File.ReadAllText(RepoFile("publish.ps1"));
        Assert.Contains("stage-game-modules.ps1", publish, StringComparison.Ordinal);
        Assert.Contains("-BuildInfo $BuildInfoPath", publish, StringComparison.Ordinal);
    }
}
