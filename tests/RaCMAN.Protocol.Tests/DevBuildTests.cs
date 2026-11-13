using System.Diagnostics;
using System.Text;

using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Test builds (revision 1.24, qwark's PROTOCOL.md 3.1.1). Every build before a release reports the
/// same build number, so a module also says whether it is a test build (SessionInfo flags2 bit4
/// DEV) and which one (HELLO's build ID after SessionInfo), and the client knows which it ships from
/// the qwark.build beside its qwark.sprx. A console's test module whose ID is not the shipped one's
/// is replaced exactly as an older build is; the same ID is current, so it is never sent again; a
/// release module is judged by its build number as before; and release.ps1 refuses a dist\ that
/// holds a test build.
/// </summary>
public class DevBuildTests
{
    private const byte Current = QwarkClient.ExpectedQwarkBuild;
    private const uint OurId = 0x1A2B3C4Du;
    private const uint OtherId = 0x0BADF00Du;

    private static string BuildText(byte build, bool dev, uint id) =>
        $"# qwark.build: the qwark.sprx beside this file (tools/buildid.py)\nbuild {build}\ndev {(dev ? 1 : 0)}\nid 0x{id:X8}\n";

    private static SessionInfo Hello(byte build, bool dev, uint id, bool emulator = false) =>
        SessionInfo.Empty with
        {
            QwarkVersion = build,
            Flags = emulator ? SessionFlags.Emulator | SessionFlags.NoCodePatches : SessionFlags.None,
            Flags2 = dev ? SessionFlags2.Dev : SessionFlags2.None,
            BuildId = id,
        };

    // ---------------------------------------------------------------- qwark.build

    [Fact]
    public void TheSidecarIsReadAsMakeWritesIt()
    {
        Assert.Equal(new QwarkBuildInfo(56, true, OurId), QwarkBuildInfo.Parse(BuildText(56, true, OurId)));
        Assert.Equal(new QwarkBuildInfo(56, false, OurId), QwarkBuildInfo.Parse(BuildText(56, false, OurId)));

        // Windows line endings, as a checkout may give it, and lower-case hex.
        Assert.Equal(new QwarkBuildInfo(57, true, 0xABCDEF01u),
            QwarkBuildInfo.Parse("build 57\r\ndev 1\r\nid 0xabcdef01\r\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("build 56\ndev 1\n")]                     // no id
    [InlineData("build 56\nid 0x1A2B3C4D\n")]              // no dev
    [InlineData("dev 0\nid 0x1A2B3C4D\n")]                 // no build
    [InlineData("build 56\ndev 2\nid 0x1A2B3C4D\n")]       // dev is 0 or 1
    [InlineData("build 56\ndev 1\nid 1A2B3C4D\n")]         // the id is hex with 0x
    [InlineData("build 300\ndev 1\nid 0x1A2B3C4D\n")]      // a build is a byte
    public void ASidecarWithoutAllThreeIsNone(string text)
    {
        Assert.Null(QwarkBuildInfo.Parse(text));
    }

    [Fact]
    public void TheSidecarIsTheOneBesideTheModule()
    {
        var folder = Path.Combine(TestDataFolder.Root, $"devbuild-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var sprx = Path.Combine(folder, WebManLoader.SprxName);

        Assert.Null(QwarkBuildInfo.BesideOrNull(sprx));

        File.WriteAllText(Path.Combine(folder, QwarkBuildInfo.FileName), BuildText(Current, true, OurId));
        Assert.Equal(new QwarkBuildInfo(Current, true, OurId), QwarkBuildInfo.BesideOrNull(sprx));
    }

    // ---------------------------------------------------------------- HELLO

    [Fact]
    public void HelloCarriesTheBuildIdAfterSessionInfo()
    {
        var sent = Hello(Current, true, OurId);
        var bytes = sent.ToHelloBytes();
        Assert.Equal(SessionInfo.Size + 4, bytes.Length);
        Assert.Equal(0x10, bytes[29] & 0x10);

        var read = SessionInfo.ParseHello(bytes);
        Assert.True(read.DevBuild);
        Assert.Equal(OurId, read.BuildId);

        // A module before the revision sends SessionInfo alone: no ID, and no DEV bit.
        var old = SessionInfo.ParseHello(Hello(Current, false, 0).ToBytes());
        Assert.False(old.DevBuild);
        Assert.Equal(0u, old.BuildId);

        // SessionInfo on its own, as GET_STATE and telemetry carry it, never reads an ID.
        Assert.Equal(0u, SessionInfo.Parse(bytes).BuildId);
        Assert.True(SessionInfo.Parse(bytes).DevBuild);
    }

    [Fact]
    public async Task TheClientReadsTheIdOffTheWire()
    {
        using var server = new FakeQwarkServer { DevBuild = true, HelloBuildId = OurId };
        server.Start();

        using var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);

        var hello = await client.HelloAsync();
        Assert.True(hello.DevBuild);
        Assert.Equal(OurId, hello.BuildId);

        server.HelloWithoutBuildId = true;
        hello = await client.HelloAsync();
        Assert.Equal(0u, hello.BuildId);
    }

    // ---------------------------------------------------------------- the rule

    public static TheoryData<string, SessionInfo, QwarkBuildInfo?, bool> Verdicts => new()
    {
        // Another build number: the number decides, as before, whatever the IDs say.
        { "an older build", Hello((byte)(Current - 1), true, OurId), new(Current, true, OurId), true },
        { "an older release", Hello((byte)(Current - 1), false, 0), new(Current, false, OurId), true },
        { "a newer build", Hello((byte)(Current + 1), true, OtherId), new(Current, true, OurId), false },

        // The same number, a test build on the console.
        { "another test build", Hello(Current, true, OtherId), new(Current, true, OurId), true },
        { "the same test build", Hello(Current, true, OurId), new(Current, true, OurId), false },
        { "a test build, and a release shipped", Hello(Current, true, OtherId), new(Current, false, OurId), true },
        { "a test build of the release's own sources", Hello(Current, true, OurId), new(Current, false, OurId), false },

        // The same number, a release on the console.
        { "two releases", Hello(Current, false, OtherId), new(Current, false, OurId), false },
        { "a release, and a test build shipped", Hello(Current, false, OtherId), new(Current, true, OurId), true },
        { "a release from before the ID, and a test build shipped", Hello(Current, false, 0), new(Current, true, OurId), true },
        { "a release with no ID, and a release shipped", Hello(Current, false, 0), new(Current, false, OurId), false },

        // Nothing known about the shipped module: the number alone.
        { "no qwark.build", Hello(Current, true, OtherId), null, false },
        { "a qwark.build of another build", Hello(Current, true, OtherId), new((byte)(Current + 1), true, OurId), false },

        // qwark-rpcs3: started by the client from beside itself, never DEV and with no ID.
        { "qwark-rpcs3", Hello(Current, false, 0, emulator: true), new(Current, true, OurId), false },
        { "qwark-rpcs3, older", Hello((byte)(Current - 1), false, 0, emulator: true), new(Current, true, OurId), true },
    };

    [Theory]
    [MemberData(nameof(Verdicts))]
    public void TheConsolesModuleIsStaleExactlyWhenItIsNotTheShippedOne(string what, SessionInfo hello, QwarkBuildInfo? shipped, bool stale)
    {
        Assert.True(stale == QwarkClient.IsStaleModule(hello, shipped), what);
    }

    // ---------------------------------------------------------------- the client

    private static string WriteShipped(byte[] module, QwarkBuildInfo? info, byte[]? pak = null)
    {
        var folder = Path.Combine(TestDataFolder.Root, $"devbuild-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        if (info is not null)
        {
            File.WriteAllText(Path.Combine(folder, QwarkBuildInfo.FileName), BuildText(info.Build, info.Dev, info.Id));
        }

        if (pak is not null) File.WriteAllBytes(Path.Combine(folder, UiPak.FileName), pak);

        var path = Path.Combine(folder, WebManLoader.SprxName);
        File.WriteAllBytes(path, module);
        return path;
    }

    private static byte[] Module(byte seed, int length = 4096)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 31 + seed);
        return bytes;
    }

    private static FakeQwarkServer Console(bool dev, uint id, byte[] onConsole)
    {
        var server = new FakeQwarkServer { DevBuild = dev, HelloBuildId = id };
        server.Session = server.Session with { QwarkVersion = Current };
        server.Files[WebManLoader.BootPath] = onConsole;
        server.Files[WebManLoader.BootPluginsPath] = Encoding.UTF8.GetBytes($"{WebManLoader.BootPath}\n");
        server.Start();
        return server;
    }

    private static async Task<AppState> ConnectAsync(FakeQwarkServer server, string sprxPath, bool standalone = true)
    {
        var settings = new Settings
        {
            Ps3 = { AutoReconnect = false, SprxPath = sprxPath, StandaloneConnection = standalone },
        };

        var state = new AppState(settings);
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);
        return state;
    }

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

    private static async Task PumpForAsync(AppState state, int milliseconds = 600)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            state.Tick(1f / 60f);
            await Task.Delay(16);
        }

        state.Tick(1f / 60f);
    }

    [Fact]
    public async Task AnotherTestBuildOfTheSameNumberIsReplacedAsAnOlderBuildIs()
    {
        var shipped = Module(7, 70_000);
        var onConsole = Module(90);

        using var server = Console(dev: true, OtherId, onConsole);
        using var state = await ConnectAsync(server, WriteShipped(shipped, new(Current, true, OurId)));

        Assert.True(await PumpAsync(state, () => state.QwarkUpdateStaged > 0));

        Assert.True(state.QwarkStale);
        Assert.True(state.QwarkOtherTestBuild);
        Assert.Equal(shipped, server.Files[WebManLoader.BootPath]);
        Assert.Equal(onConsole, server.Files[WebManLoader.BootPath + SprxUpdate.BackupSuffix]);
        Assert.Contains(state.Toasts, toast => toast.Text.Contains("test build", StringComparison.Ordinal));
        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);
    }

    [Fact]
    public async Task OnceTheConsoleRunsTheShippedTestBuildItIsCurrentAndTheNoticeGoes()
    {
        var shipped = Module(7, 70_000);

        using var server = Console(dev: true, OtherId, Module(90));
        using var state = await ConnectAsync(server, WriteShipped(shipped, new(Current, true, OurId)));
        Assert.True(await PumpAsync(state, () => state.QwarkUpdateStaged > 0));

        // The console restarts on the module that was put there, and the client connects again.
        await state.Client.DisconnectAsync();
        server.HelloBuildId = OurId;
        server.RestartModule(Current);
        server.ClearRequestLog();
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.Hello is { BuildId: OurId } && state.QwarkUpdateStaged == 0));
        Assert.False(state.QwarkStale);

        // And it is not sent again.
        await PumpForAsync(state);
        Assert.DoesNotContain(Opcode.FileWrite, server.RequestLog());
    }

    [Fact]
    public async Task TheSameTestBuildIsLeftAlone()
    {
        var onConsole = Module(90);

        using var server = Console(dev: true, OurId, onConsole);
        using var state = await ConnectAsync(server, WriteShipped(Module(7), new(Current, true, OurId)));

        await PumpForAsync(state);

        Assert.False(state.QwarkStale);
        Assert.Equal(0, state.QwarkUpdateStaged);
        Assert.Equal(onConsole, server.Files[WebManLoader.BootPath]);
        Assert.DoesNotContain(Opcode.FileWrite, server.RequestLog());
    }

    [Fact]
    public async Task AReleaseOfTheSameBuildIsLeftAloneByAReleaseClient()
    {
        var onConsole = Module(90);

        using var server = Console(dev: false, OtherId, onConsole);
        using var state = await ConnectAsync(server, WriteShipped(Module(7), new(Current, false, OurId)));

        await PumpForAsync(state);

        Assert.False(state.QwarkStale);
        Assert.Equal(0, state.QwarkUpdateStaged);
        Assert.Equal(onConsole, server.Files[WebManLoader.BootPath]);
    }

    [Fact]
    public async Task WithoutQwarkBuildTheNumberAloneDecides()
    {
        var onConsole = Module(90);

        using var server = Console(dev: true, OtherId, onConsole);
        using var state = await ConnectAsync(server, WriteShipped(Module(7), info: null));

        await PumpForAsync(state);

        Assert.False(state.QwarkStale);
        Assert.Equal(onConsole, server.Files[WebManLoader.BootPath]);
    }

    [Fact]
    public async Task WebManModeWarnsAboutAnotherTestBuildAndSendsNothingByItself()
    {
        var onConsole = Module(90);

        using var server = Console(dev: true, OtherId, onConsole);
        using var state = await ConnectAsync(server, WriteShipped(Module(7), new(Current, true, OurId)), standalone: false);

        await PumpForAsync(state);

        // The same as an older build in that mode: the warning, and the Load button is the fix.
        Assert.True(state.QwarkStale);
        Assert.Equal(0, state.QwarkUpdateStaged);
        Assert.Equal(onConsole, server.Files[WebManLoader.BootPath]);

        // Build 57 on hardware: the warning promised a button that only debug information drew.
        // Now the warning draws it, whatever the debug switch says.
        Assert.True(state.QwarkOtherTestBuild);
        Assert.Equal(ConnectionPanel.StaleBuildFix.WebManLoad, ConnectionPanel.StaleFixFor(state));
    }

    /// <summary>
    /// The stale-build warning names a button only where it draws one: webMAN mode with a module
    /// beside the client. Standalone sends the module by itself, a client with no module has
    /// nothing to send, and RPCS3's helper is a program on this PC.
    /// </summary>
    [Fact]
    public void TheStaleWarningPromisesAButtonOnlyWhereThereIsOne()
    {
        Assert.Equal(ConnectionPanel.StaleBuildFix.WebManLoad, ConnectionPanel.StaleFixFor(rpcs3Target: false, standalone: false, sprxExists: true));
        Assert.Equal(ConnectionPanel.StaleBuildFix.Standalone, ConnectionPanel.StaleFixFor(rpcs3Target: false, standalone: true, sprxExists: true));
        Assert.Equal(ConnectionPanel.StaleBuildFix.NoSprx, ConnectionPanel.StaleFixFor(rpcs3Target: false, standalone: false, sprxExists: false));
        Assert.Equal(ConnectionPanel.StaleBuildFix.NoSprx, ConnectionPanel.StaleFixFor(rpcs3Target: false, standalone: true, sprxExists: false));
        Assert.Equal(ConnectionPanel.StaleBuildFix.Rpcs3, ConnectionPanel.StaleFixFor(rpcs3Target: true, standalone: false, sprxExists: true));
        Assert.Equal(ConnectionPanel.StaleBuildFix.Rpcs3, ConnectionPanel.StaleFixFor(rpcs3Target: true, standalone: true, sprxExists: false));

        foreach (var fix in Enum.GetValues<ConnectionPanel.StaleBuildFix>())
        {
            string text = ConnectionPanel.StaleFixText(fix, "C:\\x\\qwark.sprx");
            Assert.Equal(fix == ConnectionPanel.StaleBuildFix.WebManLoad, text.Contains("button", StringComparison.Ordinal));
        }

        Assert.Contains("C:\\x\\qwark.sprx", ConnectionPanel.StaleFixText(ConnectionPanel.StaleBuildFix.NoSprx, "C:\\x\\qwark.sprx"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleTestBuildsBadPakIsTheUpdatesBusinessNotTheSendButtons()
    {
        // The pak beside the client is its own module's; the console's other test build may want
        // another, so the ui.pak line stays out of it while the module is stale.
        // Neither fake has a pak on its HDD, so both modules say theirs is bad.
        using var server = Console(dev: true, OtherId, Module(90));
        using var state = await ConnectAsync(server, WriteShipped(Module(7), new(Current, true, OurId)), standalone: false);

        Assert.True(await PumpAsync(state, () => state.Session.UiPakBad));
        Assert.True(state.QwarkStale);
        Assert.False(state.UiPakNeeded);

        // The same test build with a bad pak is what the button is for.
        using var same = Console(dev: true, OurId, Module(90));
        using var current = await ConnectAsync(same, WriteShipped(Module(7), new(Current, true, OurId)), standalone: false);

        Assert.True(await PumpAsync(current, () => current.Session.UiPakBad));
        Assert.False(current.QwarkStale);
        Assert.True(current.UiPakNeeded);
    }

    [Fact]
    public async Task TheRpcs3HelperIsNeverATestBuildToReplace()
    {
        // qwark-rpcs3 as it is: build number current, no DEV bit, ID 0, and EMULATOR set. The
        // client starts it from beside itself, so whatever this client ships, it is current.
        using var server = new FakeQwarkServer { Emulator = true, NoCodePatches = true };
        server.Session = server.Session with { QwarkVersion = Current };
        server.Start();

        using var state = await ConnectAsync(server, WriteShipped(Module(7), new(Current, true, OurId)));

        await PumpForAsync(state);

        Assert.False(state.QwarkStale);
        Assert.Equal(0, state.QwarkUpdateStaged);
    }

    // ---------------------------------------------------------------- the release

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

    /// <summary>
    /// Runs packaging/check-qwark-build.ps1, which release.ps1 and the release workflow run, against
    /// a dist folder: its exit code and what it wrote. Windows PowerShell, or PowerShell 7 where that
    /// is all there is; null where neither is (the check is a Windows release step).
    /// </summary>
    private static (int Exit, string Output)? RunCheck(string dist, string? protoH)
    {
        string script = RepoFile("packaging", "check-qwark-build.ps1");
        foreach (var shell in OperatingSystem.IsWindows() ? new[] { "powershell.exe", "pwsh" } : new[] { "pwsh" })
        {
            var info = new ProcessStartInfo(shell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Dist", dist })
            {
                info.ArgumentList.Add(arg);
            }

            if (protoH is not null)
            {
                info.ArgumentList.Add("-ProtoH");
                info.ArgumentList.Add(protoH);
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
                Assert.True(process.WaitForExit(60_000), "the check finished");
                return (process.ExitCode, stdout.Result + stderr.Result);
            }
        }

        return null;
    }

    private static (string Dist, string ProtoH) QwarkTree(string? buildFile, int protoBuild = Current)
    {
        var root = Path.Combine(TestDataFolder.Root, $"qwark-{Guid.NewGuid():N}");
        var dist = Path.Combine(root, "dist");
        Directory.CreateDirectory(dist);
        Directory.CreateDirectory(Path.Combine(root, "src", "core"));
        var proto = Path.Combine(root, "src", "core", "proto.h");
        File.WriteAllText(proto, $"#define QWARK_PROTOCOL_VERSION  1\n#define QWARK_BUILD             {protoBuild}\n");
        if (buildFile is not null) File.WriteAllText(Path.Combine(dist, QwarkBuildInfo.FileName), buildFile);
        // Since build 57 a release carries its game modules and ToD's Lua helper in ui.pak (ReleaseDist).
        ReleaseDist.Write(dist, (byte)protoBuild);
        return (dist, proto);
    }

    [Fact]
    public void TheReleaseCheckTakesAReleaseDist()
    {
        var (dist, proto) = QwarkTree(BuildText(Current, false, OurId));
        if (RunCheck(dist, proto) is not { } result) return;

        Assert.True(result.Exit == 0, result.Output);
        Assert.Contains("holds a release", result.Output, StringComparison.Ordinal);
        Assert.Contains("5 game modules", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseCheckRefusesATestBuild()
    {
        var (dist, proto) = QwarkTree(BuildText(Current, true, OurId));
        if (RunCheck(dist, proto) is not { } result) return;

        Assert.True(result.Exit != 0, result.Output);
        Assert.Contains("TEST build", result.Output, StringComparison.Ordinal);
        Assert.Contains("make release", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseCheckRefusesADistWithNoQwarkBuild()
    {
        var (dist, proto) = QwarkTree(buildFile: null);
        if (RunCheck(dist, proto) is not { } result) return;

        Assert.True(result.Exit != 0, result.Output);
        Assert.Contains("no dist", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseCheckRefusesAModuleOfAnotherBuildThanItsSources()
    {
        var (dist, proto) = QwarkTree(BuildText(Current, false, OurId), protoBuild: Current + 1);
        if (RunCheck(dist, proto) is not { } result) return;

        Assert.True(result.Exit != 0, result.Output);
        Assert.Contains($"build {Current + 1}", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasePs1RunsTheCheckAndBuildsAReleaseModule()
    {
        string release = File.ReadAllText(RepoFile("release.ps1"));
        Assert.Contains("check-qwark-build.ps1", release, StringComparison.Ordinal);
        Assert.Contains("QWARK_DEV=0", release, StringComparison.Ordinal);
        Assert.Contains("'qwark.build'", release, StringComparison.Ordinal);

        string workflow = File.ReadAllText(RepoFile(".github", "workflows", "release.yml"));
        Assert.Equal(2, workflow.Split("check-qwark-build.ps1").Length - 1);
    }
}
