using System.Diagnostics;

using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Qwark revision 1.30: the run-start warnings. RUN_WARN and SessionInfo flags3 (the Settings
/// page's Cheat warnings dropdown, a hidden checkbox from build 57 until revision 1.33's third
/// value), a mod's <c>#- legal:</c> line as the library and MOD_LIST carry it, and the green line a
/// run-legal mod's details end with.
/// </summary>
public class RunWarningsTests
{
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

    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync(bool reports = true, bool settings133 = true)
    {
        var server = new FakeQwarkServer { ReportsRunWarnings = reports, ReportsSettings133 = settings133 };
        server.Start();

        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    // ------------------------------------------------------------------ RUN_WARN and flags3

    [Fact]
    public async Task RunWarnTurnsTheWarningsOffAndOnAgain()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            Assert.Equal(0x0096, (int)Opcode.RunWarn);
            var session = (await client.GetStateAsync()).Session;
            Assert.True(session.HasRunWarnings);
            Assert.False(session.RunWarningsOff);
            Assert.True(QwarkSettings.ShowsCheatWarnings(session));
            Assert.Equal(RunWarningMode.ModsAndCheats, session.RunWarnings);

            await client.RunWarnAsync(false);
            Assert.False(server.RunWarnings);
            session = (await client.GetStateAsync()).Session;
            Assert.True(session.RunWarningsOff);
            Assert.Equal(RunWarningMode.Off, session.RunWarnings);
            Assert.True(await WaitFor(() =>
                client.LatestTelemetry is { Session.RunWarningsOff: true } && !client.TelemetryViaTcp));

            await client.RunWarnAsync(true);
            Assert.True(server.RunWarnings);
            Assert.True(await WaitFor(() =>
                client.LatestTelemetry is { Session.RunWarningsOff: false } && !client.TelemetryViaTcp));
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(255)]
    public async Task RunWarnRefusesAByteThatIsNoneOfTheThree(byte value)
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.RequestAsync(Opcode.RunWarn, new[] { value }));
            Assert.Equal(Status.BadArg, refused.Status);
            Assert.True(server.RunWarnings);
        }
    }

    [Fact]
    public async Task AnOlderModuleHasNoSwitch()
    {
        var (server, client) = await ConnectAsync(reports: false);
        using (server)
        using (client)
        {
            var session = (await client.GetStateAsync()).Session;
            Assert.False(session.HasRunWarnings);
            Assert.False(session.RunWarningsOff);
            Assert.False(QwarkSettings.ShowsCheatWarnings(session));
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.RunWarnAsync(false));
            Assert.Equal(Status.UnknownOp, refused.Status);
        }
    }

    [Fact]
    public void Flags3IsByte31AndRoundTrips()
    {
        var bytes = new byte[SessionInfo.Size];
        bytes[0] = 1;
        bytes[31] = 0x03;
        var info = SessionInfo.Parse(bytes);
        Assert.True(info.HasRunWarnings);
        Assert.True(info.RunWarningsOff);
        Assert.Equal(SessionFlags3.RunWarnings | SessionFlags3.RunWarningsOff, info.Flags3);

        // Bits 2 to 4 are the telemetry rate's since qwark revision 1.32, and bits 5 to 7 revision
        // 1.33's: its settings, cheats only and a boot's question.
        bytes[31] = 0xE0;
        var high = SessionInfo.Parse(bytes);
        Assert.Equal(SessionFlags3.Settings133 | SessionFlags3.RunWarningsCheats | SessionFlags3.BootAsk, high.Flags3);
        Assert.True(high.HasSettings133 && high.RunWarningsCheatsOnly && high.BootAskPending);
        Assert.Equal(0xE0, high.ToBytes()[31]);
        bytes[31] = 0xFC;
        Assert.Equal(SessionFlags3.TelemetryRate | SessionFlags3.TelemetryRateMask | SessionFlags3.Settings133
                     | SessionFlags3.RunWarningsCheats | SessionFlags3.BootAsk, SessionInfo.Parse(bytes).Flags3);

        var again = SessionInfo.Parse(info.ToBytes());
        Assert.Equal(info.Flags3, again.Flags3);
    }

    [Fact]
    public void RunWarningsIsASettingsWrite()
    {
        Assert.True(StatusToast.IsSettingsWrite(Opcode.RunWarn));
    }

    // ------------------------------------------------------------------ the Cheat warnings dropdown (1.33)

    /// <summary>
    /// Bit1 still means off and nothing else, so an older client reads cheats only as on; the new
    /// client reads all three from bits 1 and 6, off winning should a module ever set both.
    /// </summary>
    [Theory]
    [InlineData(0x01, RunWarningMode.ModsAndCheats)]
    [InlineData(0x03, RunWarningMode.Off)]
    [InlineData(0x41, RunWarningMode.CheatsOnly)]
    [InlineData(0x43, RunWarningMode.Off)]
    public void TheModeIsReadFromBitsOneAndSix(byte flags3, RunWarningMode want)
    {
        var bytes = new byte[SessionInfo.Size];
        bytes[0] = 1;
        bytes[31] = flags3;
        Assert.Equal(want, SessionInfo.Parse(bytes).RunWarnings);
    }

    [Fact]
    public async Task CheatsOnlyIsSentAsTwoAndComesBackInBitSix()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var session = (await client.GetStateAsync()).Session;
            Assert.True(session.HasSettings133);
            Assert.Equal(new[] { RunWarningMode.ModsAndCheats, RunWarningMode.CheatsOnly, RunWarningMode.Off },
                QwarkSettings.CheatWarningChoices(session).Select(c => c.Mode));

            await client.RunWarnAsync(RunWarningMode.CheatsOnly);
            Assert.Equal(RunWarningMode.CheatsOnly, server.RunWarningMode);
            session = (await client.GetStateAsync()).Session;
            Assert.True(session.RunWarningsCheatsOnly);
            Assert.False(session.RunWarningsOff);
            Assert.Equal(RunWarningMode.CheatsOnly, session.RunWarnings);
            Assert.Equal("warns about cheats only", QwarkSettings.CheatWarningsHint(session.RunWarnings));
        }
    }

    /// <summary>
    /// A module before revision 1.33 takes 0 and 1 and refuses 2: the dropdown leaves Cheats only out
    /// for it, and the panel's own path never sends it there.
    /// </summary>
    [Fact]
    public async Task AModuleWithoutTheThirdValueIsNeverOfferedNorSentCheatsOnly()
    {
        using var server = new FakeQwarkServer { ReportsSettings133 = false };
        server.Start();
        using var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);
        Assert.True(await PumpAsync(state, () => state.Session.QwarkVersion != 0));

        Assert.False(state.Session.HasSettings133);
        Assert.Equal(new[] { RunWarningMode.ModsAndCheats, RunWarningMode.Off },
            QwarkSettings.CheatWarningChoices(state.Session).Select(c => c.Mode));
        Assert.False(QwarkSettings.SetCheatWarnings(state, RunWarningMode.CheatsOnly));
        await PumpForAsync(state, 200);
        Assert.Equal(RunWarningMode.ModsAndCheats, server.RunWarningMode);
        Assert.DoesNotContain(Opcode.RunWarn, server.RequestLog());

        // What the module would have answered.
        var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => state.Client.RunWarnAsync(RunWarningMode.CheatsOnly));
        Assert.Equal(Status.BadArg, refused.Status);

        // Off it does take, through the dropdown's own path.
        Assert.True(QwarkSettings.SetCheatWarnings(state, RunWarningMode.Off));
        Assert.True(await PumpAsync(state, () => state.Session.RunWarningsOff));
        Assert.Equal(RunWarningMode.Off, server.RunWarningMode);
    }

    [Fact]
    public async Task TheDropdownsPickSendsOneRequestAndTheSessionFollows()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);
        Assert.True(await PumpAsync(state, () => state.Session.HasSettings133));

        Assert.True(QwarkSettings.SetCheatWarnings(state, RunWarningMode.CheatsOnly));
        Assert.True(await PumpAsync(state, () => state.Session.RunWarnings == RunWarningMode.CheatsOnly));
        Assert.Equal(1, server.RequestLog().Count(op => op == Opcode.RunWarn));
        Assert.Empty(state.Toasts.Where(t => t.Kind == ToastKind.Error));
    }

    [Fact]
    public void WithoutAConnectionTheDropdownSendsNothing()
    {
        using var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        Assert.False(QwarkSettings.SetCheatWarnings(state, RunWarningMode.Off));
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

    private static Task PumpForAsync(AppState state, int milliseconds) => PumpAsync(state, () => false, milliseconds);

    // ------------------------------------------------------------------ #- legal:

    [Theory]
    [InlineData("true", ModLegality.Legal)]
    [InlineData(" TRUE ", ModLegality.Legal)]
    [InlineData("false", ModLegality.Illegal)]
    [InlineData("False", ModLegality.Illegal)]
    [InlineData("", ModLegality.Unknown)]
    [InlineData("yes", ModLegality.Unknown)]
    [InlineData("1", ModLegality.Unknown)]
    [InlineData("maybe", ModLegality.Unknown)]
    [InlineData(null, ModLegality.Unknown)]
    public void LegalValuesReadAsQwarkReadsThem(string? value, ModLegality want)
    {
        Assert.Equal(want, ModLegalityText.Parse(value));
    }

    [Theory]
    [InlineData("#- legal: true\n", ModLegality.Legal)]
    [InlineData("#- Legal: false\n", ModLegality.Illegal)]
    [InlineData("", ModLegality.Unknown)]
    [InlineData("#- legal: garbage\n", ModLegality.Unknown)]
    [InlineData("#- legal: true\n#- legal: nope\n", ModLegality.Unknown)]
    public void TheLibraryReadsTheLegalLine(string header, ModLegality want)
    {
        string dir = Path.Combine(Path.GetTempPath(), "racman-legal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "patch.txt"), "#- name: Test\n" + header + "0x00100000: 0x60000000\n");
            var mod = ModLibrary.Read(dir);
            Assert.NotNull(mod);
            Assert.Equal(want, mod!.Legality);
            Assert.Equal(1, mod.PatchWordCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0x00, ModLegality.Unknown)]
    [InlineData(0x40, ModLegality.Legal)]
    [InlineData(0x80, ModLegality.Illegal)]
    [InlineData(0xC0, ModLegality.Unknown)]
    [InlineData(0x41, ModLegality.Legal)]
    public void ModListCarriesLegalityInBits6And7(byte flags, ModLegality want)
    {
        var row = new byte[ModEntry.Size];
        row[1] = flags;
        var entry = ModEntry.Parse(row);
        Assert.Equal(want, entry.Legality);
    }

    // ------------------------------------------------------------------ the legal line

    private static LocalMod Local(ModLegality legality) => new()
    {
        Directory = "x",
        DirName = "lib-mod",
        Name = "Library mod",
        Legality = legality,
    };

    private static ModEntry ConsoleRow(ModFlags flags) => new(0, flags, 0, "console-mod", "Console mod", "1", "me");

    [Fact]
    public void OnlyALegalModGetsTheLegalLine()
    {
        Assert.Equal(ModTable.LegalLine, ModTable.LegalityLine(new ModRow(Local(ModLegality.Legal), null, null, true)));
        Assert.Null(ModTable.LegalityLine(new ModRow(Local(ModLegality.Illegal), null, null, true)));
        Assert.Null(ModTable.LegalityLine(new ModRow(Local(ModLegality.Unknown), null, null, true)));
    }

    [Fact]
    public void TheLibraryCopyWinsAndAConsoleOnlyModUsesModList()
    {
        var both = new ModRow(Local(ModLegality.Legal), ConsoleRow(ModFlags.Illegal), null, true);
        Assert.Equal(ModLegality.Legal, both.Legality);

        Assert.Equal(ModTable.LegalLine, ModTable.LegalityLine(both));

        var consoleOnly = new ModRow(null, ConsoleRow(ModFlags.Illegal | ModFlags.Loaded), "console-mod", true);
        Assert.Null(ModTable.LegalityLine(consoleOnly));

        var consoleLegal = new ModRow(null, ConsoleRow(ModFlags.Legal | ModFlags.Loaded), "console-mod", true);
        Assert.Equal(ModTable.LegalLine, ModTable.LegalityLine(consoleLegal));

        var olderModule = new ModRow(null, ConsoleRow(ModFlags.Loaded), "console-mod", true);
        Assert.Null(ModTable.LegalityLine(olderModule));
    }
}
