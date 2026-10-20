using System.Diagnostics;

using RaCMAN.App;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The console menu's ui.pak (revision 1.23, qwark build 55): flags bit7, the header the client
/// reads, the fake console's own look at its pak, and the pak sent alone to a console whose module
/// is current. Every send runs against the fake console's in-memory filesystem, so what is where
/// afterwards can be read off <see cref="FakeQwarkServer.Files"/>.
/// </summary>
public class UiPakTests
{
    private const byte Current = QwarkClient.ExpectedQwarkBuild;

    /// <summary>The file ops in the request log, in order.</summary>
    private static Opcode[] FileOps(FakeQwarkServer server) =>
        server.RequestLog().Where(op => (ushort)op is >= 0x0070 and <= 0x0079).ToArray();

    /// <summary>
    /// The ops that change the console's files. The client's own refreshes list folders while it is
    /// connected, so a test with an AppState in it looks for these rather than for every file op.
    /// </summary>
    private static Opcode[] Writes(FakeQwarkServer server) => server.RequestLog()
        .Where(op => op is Opcode.FileWrite or Opcode.FileDelete or Opcode.FileRename or Opcode.DirDelete)
        .ToArray();

    /// <summary>
    /// A client folder of its own, with a qwark.sprx and, when given, a ui.pak beside it. Never the
    /// test data folder itself: other tests keep their SPRX there, and a ui.pak beside those would
    /// change what they send.
    /// </summary>
    private static string ClientCopy(byte[]? pak)
    {
        string folder = Path.Combine(TestDataFolder.Root, $"client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        string sprx = Path.Combine(folder, WebManLoader.SprxName);
        File.WriteAllBytes(sprx, new byte[] { 1, 2, 3, 4 });
        if (pak is not null) File.WriteAllBytes(Path.Combine(folder, UiPak.FileName), pak);
        return sprx;
    }

    private static FakeQwarkServer CurrentConsole()
    {
        var server = new FakeQwarkServer();
        server.Session = server.Session with { QwarkVersion = Current };
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

    /// <summary>
    /// Runs frames, the way the render loop would, until the condition holds, and keeps every toast
    /// that went up on the way: frames of more than a sixtieth of a second, which the look after a
    /// send needs to get anywhere, also run toasts out sooner.
    /// </summary>
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

        /// <summary>Frames for <paramref name="seconds"/> of the client's own time, for the tests whose point is that nothing happens.</summary>
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
    public void UiPakBadIsBit7()
    {
        Assert.Equal(0x80, (int)SessionFlags.UiPakBad);

        var bad = SessionInfo.Parse((SessionInfo.Empty with { Flags = SessionFlags.UiPakBad }).ToBytes());
        Assert.True(bad.UiPakBad);
        Assert.False(bad.MenuHelpOff);
        Assert.False(bad.VshMemory);

        var others = SessionInfo.Empty with { Flags = (SessionFlags)0x7F };
        Assert.False(SessionInfo.Parse(others.ToBytes()).UiPakBad);
    }

    /// <summary>
    /// The 32-byte header, laid out by hand: "QPAK", u16 format 1, u16 header size 32, u32 build, all
    /// big-endian. The build is the only field the client reads.
    /// </summary>
    [Fact]
    public void TheHeaderNamesTheBuildAndNothingElseIsRead()
    {
        var header = new byte[UiPak.HeaderSize];
        "QPAK"u8.CopyTo(header);
        header[5] = 1;
        header[7] = 32;
        header[11] = 55;

        Assert.True(UiPak.TryReadBuild(header, out int build));
        Assert.Equal(55, build);

        Assert.True(UiPak.TryReadBuild(FakeQwarkServer.UiPakFor(Current), out build));
        Assert.Equal(Current, build);

        // Shorter than the header, and the wrong magic, are not paks at all.
        Assert.False(UiPak.TryReadBuild(header.AsSpan(0, UiPak.HeaderSize - 1), out _));
        header[0] = (byte)'X';
        Assert.False(UiPak.TryReadBuild(header, out _));
    }

    [Fact]
    public void TheClientsCopyIsBesideTheSprxItSends()
    {
        string sprx = Path.Combine(TestDataFolder.Root, "somewhere", WebManLoader.SprxName);
        Assert.Equal(Path.Combine(TestDataFolder.Root, "somewhere", "ui.pak"), UiPak.BesideSprx(sprx));

        // The default, a bare name, is beside the executable, and so is the pak.
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "ui.pak"),
            UiPak.BesideSprx(Ps3Connect.ResolveSprx(null)));

        Assert.Equal("/dev_hdd0/qwark/ui.pak", UiPak.ConsolePath);
        Assert.Equal("/dev_hdd0/qwark/ui.pak.new", UiPak.StagedPath);
    }

    // ---------------------------------------------------------------- the fake console's look

    /// <summary>
    /// The fake sets bit7 the way qwark does: from build 55 on, while it has no good pak, and never
    /// in its RPCS3 shape. A pak naming its own build is a good one.
    /// </summary>
    [Theory]
    [InlineData(Current, false, false, true)]
    [InlineData(Current, true, false, false)]
    [InlineData((byte)(Current - 1), false, false, false)]
    [InlineData(Current, false, true, false)]
    public async Task TheFakeSetsBit7AsQwarkDoes(byte build, bool withPak, bool rpcs3, bool expected)
    {
        using var server = new FakeQwarkServer();
        server.Session = server.Session with { QwarkVersion = build };
        server.Emulator = rpcs3;
        server.NoCodePatches = rpcs3;
        if (withPak) server.PutUiPak();
        server.Start();

        using var client = await ClientAsync(server);

        Assert.Equal(expected, server.UiPakBad);
        Assert.Equal(expected, (await client.GetStateAsync()).Session.UiPakBad);
        Assert.Equal(expected, client.LatestSession!.UiPakBad);
    }

    [Fact]
    public async Task TheFakeLooksAgainAfterAFileOpOnThePaksPath()
    {
        using var server = CurrentConsole();
        server.PutUiPak();
        server.Start();

        using var client = await ClientAsync(server);
        async Task<bool> Bad() => (await client.GetStateAsync()).Session.UiPakBad;

        Assert.False(await Bad());

        await client.FileDeleteAsync(UiPak.ConsolePath);
        Assert.True(await Bad());

        // A write is looked at when its handle closes.
        await client.WriteFileAsync(UiPak.ConsolePath, FakeQwarkServer.UiPakFor(Current));
        Assert.False(await Bad());

        await client.DirDeleteAsync(UiPak.ConsoleDirectory);
        Assert.True(await Bad());

        // Nothing under the staged name counts until it is renamed into place.
        await client.WriteFileAsync(UiPak.StagedPath, FakeQwarkServer.UiPakFor(Current));
        Assert.True(await Bad());
        await client.FileRenameAsync(UiPak.StagedPath, UiPak.ConsolePath);
        Assert.False(await Bad());

        // Another build's pak is not a good one.
        await client.FileDeleteAsync(UiPak.ConsolePath);
        await client.WriteFileAsync(UiPak.ConsolePath, FakeQwarkServer.UiPakFor((byte)(Current - 1)));
        Assert.True(await Bad());
    }

    [Fact]
    public void AGoodStagedPakIsMovedIntoPlaceWhenTheModuleStarts()
    {
        var staged = FakeQwarkServer.UiPakFor(Current);
        var old = FakeQwarkServer.UiPakFor((byte)(Current - 1));

        using var server = CurrentConsole();
        server.Files[UiPak.ConsolePath] = old;
        server.Files[UiPak.StagedPath] = staged;
        server.Start();

        Assert.Equal(staged, server.Files[UiPak.ConsolePath]);
        Assert.False(server.Files.ContainsKey(UiPak.StagedPath));
        Assert.False(server.UiPakBad);
    }

    [Fact]
    public void AStagedPakForAnotherBuildIsLeftWhereItIs()
    {
        var staged = FakeQwarkServer.UiPakFor((byte)(Current + 1));
        var current = FakeQwarkServer.UiPakFor(Current);

        using var server = CurrentConsole();
        server.Files[UiPak.ConsolePath] = current;
        server.Files[UiPak.StagedPath] = staged;
        server.Start();

        Assert.Equal(current, server.Files[UiPak.ConsolePath]);
        Assert.Equal(staged, server.Files[UiPak.StagedPath]);
        Assert.False(server.UiPakBad);

        // The next build starts, and that pak is its own.
        server.RestartModule((byte)(Current + 1));
        Assert.Equal(staged, server.Files[UiPak.ConsolePath]);
        Assert.False(server.Files.ContainsKey(UiPak.StagedPath));
        Assert.False(server.UiPakBad);
    }

    // ---------------------------------------------------------------- the send, against the console

    /// <summary>
    /// The four steps, in order: write the pak as ui.pak.new, read it back, delete the old pak,
    /// rename the upload. The module looks again just after the rename, so the next session block
    /// is clear of bit7.
    /// </summary>
    [Fact]
    public async Task ThePakGoesUpAsNewIsReadBackAndIsThenRenamedIntoPlace()
    {
        var pak = FakeQwarkServer.UiPakFor(Current);

        using var server = CurrentConsole();
        server.PutUiPak((byte)(Current - 1));
        server.Start();

        using var client = await ClientAsync(server);
        Assert.True((await client.GetStateAsync()).Session.UiPakBad);

        var said = new List<string>();
        server.ClearRequestLog();
        var result = await UiPakSend.SendAsync(client, pak, new InlineProgress<string>(said.Add));

        Assert.Equal(
            new[]
            {
                Opcode.FileOpen, Opcode.FileWrite, Opcode.FileClose,
                Opcode.FileOpen, Opcode.FileRead, Opcode.FileClose,
                Opcode.FileDelete, Opcode.FileRename,
            },
            FileOps(server));

        Assert.True(result.ReplacedACopy);
        Assert.Equal(pak.Length, result.Bytes);
        Assert.Equal(pak, server.Files[UiPak.ConsolePath]);
        Assert.False(server.Files.ContainsKey(UiPak.StagedPath));
        Assert.Equal(
            new[]
            {
                "Uploading ui.pak to /dev_hdd0/qwark/ui.pak.new",
                "Putting /dev_hdd0/qwark/ui.pak.new in place of /dev_hdd0/qwark/ui.pak",
            },
            said);

        Assert.False((await client.GetStateAsync()).Session.UiPakBad);
    }

    /// <summary>A console with no pak at all answers the delete NOT_FOUND, which is what was wanted.</summary>
    [Fact]
    public async Task AConsoleWithNoPakAtAllGetsOne()
    {
        var pak = FakeQwarkServer.UiPakFor(Current);

        using var server = CurrentConsole();
        server.Start();

        using var client = await ClientAsync(server);
        var result = await UiPakSend.SendAsync(client, pak);

        Assert.False(result.ReplacedACopy);
        Assert.Equal(pak, server.Files[UiPak.ConsolePath]);
        Assert.False((await client.GetStateAsync()).Session.UiPakBad);
    }

    /// <summary>
    /// A copy that could not fix anything is refused before a request goes out: one that is not a
    /// pak, and one made with another build. The console's pak is left exactly as it was.
    /// </summary>
    [Fact]
    public async Task ALocalCopyThatCouldNotFixAnythingSendsNothing()
    {
        using var server = CurrentConsole();
        server.Start();

        using var client = await ClientAsync(server);
        server.ClearRequestLog();

        var other = await Assert.ThrowsAsync<UiPakSendException>(
            () => UiPakSend.SendAsync(client, FakeQwarkServer.UiPakFor((byte)(Current - 1))));
        Assert.Equal(UiPakSendStep.Local, other.Step);
        Assert.Contains($"for qwark build {Current - 1}, not build {Current}", other.Message, StringComparison.Ordinal);

        var junk = await Assert.ThrowsAsync<UiPakSendException>(
            () => UiPakSend.SendAsync(client, new byte[4096]));
        Assert.Equal(UiPakSendStep.Local, junk.Step);
        Assert.Contains("is not a ui.pak file", junk.Message, StringComparison.Ordinal);

        string missing = Path.Combine(TestDataFolder.Root, $"absent-{Guid.NewGuid():N}", UiPak.FileName);
        var none = await Assert.ThrowsAsync<UiPakSendException>(() => UiPakSend.RunAsync(client, missing));
        Assert.Equal(UiPakSendStep.Local, none.Step);
        Assert.Contains(missing, none.Message, StringComparison.Ordinal);

        Assert.Empty(FileOps(server));
        Assert.False(server.Files.ContainsKey(UiPak.ConsolePath));
        Assert.Null(UiPakSend.Problem(FakeQwarkServer.UiPakFor(Current), "x"));
    }

    // ---------------------------------------------------------------- the client around it

    /// <summary>
    /// A console on this client's build without its pak: one toast for the connection, the line
    /// and the button on the Connection panel, and a send that clears the flag. The look at the
    /// flag a few seconds on finds it gone and says nothing more.
    /// </summary>
    [Fact]
    public async Task ACurrentConsoleWithoutItsPakIsToldOnceAndTheButtonSendsIt()
    {
        var pak = FakeQwarkServer.UiPakFor(Current);

        using var server = CurrentConsole();
        server.Start();

        using var state = await StateAsync(server, ClientCopy(pak));
        var frames = new Frames();

        Assert.True(await frames.UntilAsync(state, () => state.UiPakNeeded));
        Assert.False(state.QwarkStale);

        state.SendUiPak();
        Assert.True(state.UiPakSending);
        Assert.True(await frames.UntilAsync(state, () => !state.UiPakNeeded && !state.UiPakSending));

        Assert.Equal(pak, server.Files[UiPak.ConsolePath]);
        Assert.False(server.Files.ContainsKey(UiPak.StagedPath));
        Assert.Contains(frames.Seen, t => t.Text == "Sent ui.pak to the console" && t.Kind == ToastKind.Success);

        await frames.ForAsync(state, AppState.UiPakCheckSeconds + 1);

        Assert.Single(frames.Seen, t => t.Text.StartsWith(AppState.UiPakWarning, StringComparison.Ordinal));
        Assert.Empty(frames.Errors);
    }

    /// <summary>
    /// A send that went through and a module that still says no: the copy beside the client is not
    /// the one the console's module was built with, and only the module's log can say why.
    /// </summary>
    [Fact]
    public async Task AFlagThatOutlivesTheSendIsReported()
    {
        using var server = CurrentConsole();
        server.RejectsUiPak = true;
        server.Start();

        using var state = await StateAsync(server, ClientCopy(FakeQwarkServer.UiPakFor(Current)));
        var frames = new Frames();

        Assert.True(await frames.UntilAsync(state, () => state.UiPakNeeded));
        state.SendUiPak();

        Assert.True(await frames.UntilAsync(state, () => frames.Errors.Length > 0, delta: 0.25f));

        var error = Assert.Single(frames.Errors);
        Assert.Contains("still says its ui.pak is missing", error.Text, StringComparison.Ordinal);
        Assert.Contains("qwark log", error.Text, StringComparison.Ordinal);
        Assert.Contains(frames.Seen, t => t.Text == "Sent ui.pak to the console");
        Assert.True(state.UiPakNeeded);
    }

    public static IEnumerable<object?[]> LocalCopies() => new[]
    {
        new object?[] { null, "There is no ui.pak beside this client to send" },
        new object?[] { new byte[100], "is not a ui.pak file" },
        new object?[] { FakeQwarkServer.UiPakFor((byte)(Current - 1)), $"for qwark build {Current - 1}" },
    };

    [Theory]
    [MemberData(nameof(LocalCopies))]
    public async Task TheButtonRefusesACopyThatCouldNotFixAnything(byte[]? pak, string says)
    {
        using var server = CurrentConsole();
        server.Start();

        using var state = await StateAsync(server, ClientCopy(pak));
        var frames = new Frames();

        Assert.True(await frames.UntilAsync(state, () => state.UiPakNeeded));
        server.ClearRequestLog();

        state.SendUiPak();
        Assert.True(await frames.UntilAsync(state, () => frames.Errors.Length > 0 && !state.UiPakSending));

        Assert.Contains(says, Assert.Single(frames.Errors).Text, StringComparison.Ordinal);
        Assert.Empty(Writes(server));
        Assert.False(server.Files.ContainsKey(UiPak.StagedPath));
        Assert.False(server.Files.ContainsKey(UiPak.ConsolePath));
        Assert.True(state.UiPakNeeded);
    }

    /// <summary>A step the console refuses is one toast that names the step and carries the status.</summary>
    [Fact]
    public async Task ARefusedStepNamesItselfAndTheStatus()
    {
        using var server = CurrentConsole();
        server.FileWriteStatus = Status.IoError;
        server.Start();

        using var state = await StateAsync(server, ClientCopy(FakeQwarkServer.UiPakFor(Current)));
        var frames = new Frames();

        Assert.True(await frames.UntilAsync(state, () => state.UiPakNeeded));
        state.SendUiPak();
        Assert.True(await frames.UntilAsync(state, () => frames.Errors.Length > 0 && !state.UiPakSending));

        var error = Assert.Single(frames.Errors).Text;
        Assert.Contains("uploading it to /dev_hdd0/qwark/ui.pak.new", error, StringComparison.Ordinal);
        Assert.Contains(nameof(Status.IoError), error, StringComparison.Ordinal);
        Assert.False(server.Files.ContainsKey(UiPak.ConsolePath));
        Assert.True(state.UiPakNeeded);
    }

    /// <summary>
    /// Only a console on exactly this client's build is offered the pak alone. An older module's
    /// update carries the pak, a newer one is beyond what this client has, and RPCS3 has no menu.
    /// Bit7 is set by hand here, so it is the client's rule that is tested and not the fake's.
    /// </summary>
    [Theory]
    [InlineData(Current, false, false, true)]
    [InlineData((byte)(Current - 1), false, false, false)]
    [InlineData((byte)(Current + 1), false, false, false)]
    [InlineData(Current, true, false, false)]
    [InlineData(Current, false, true, false)]
    public async Task OnlyACurrentConsoleIsOfferedThePak(byte build, bool emulator, bool rpcs3Target, bool offered)
    {
        using var server = new FakeQwarkServer();
        server.Session = server.Session with
        {
            QwarkVersion = build,
            Flags = SessionFlags.UiPakBad | (emulator ? SessionFlags.Emulator | SessionFlags.NoCodePatches : 0),
        };
        server.Start();

        var settings = new Settings { Rpcs3Target = rpcs3Target };
        settings.Rpcs3.AutoReconnect = false;
        using var state = await StateAsync(server, ClientCopy(FakeQwarkServer.UiPakFor(Current)), settings);
        var frames = new Frames();

        Assert.True(await frames.UntilAsync(state, () => state.Session.UiPakBad));
        await frames.ForAsync(state, 0.5f, 1f / 60f);

        Assert.Equal(offered, state.UiPakNeeded);
        Assert.Equal(offered ? 1 : 0,
            frames.Seen.Count(t => t.Text.StartsWith(AppState.UiPakWarning, StringComparison.Ordinal)));
    }
}
