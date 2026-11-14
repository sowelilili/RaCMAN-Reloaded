using System.Diagnostics;

using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Qwark revision 1.30: the run-start warnings. RUN_WARN and SessionInfo flags3 (the Combos panel's
/// Run warnings box, hidden since build 57), a mod's <c>#- legal:</c> line as the library and
/// MOD_LIST carry it, and the green line a run-legal mod's details end with.
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

    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync(bool reports = true)
    {
        var server = new FakeQwarkServer { ReportsRunWarnings = reports };
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
            // After build 57 the box is hidden, though the module has the switch.
            Assert.False(QwarkSettings.ShowsRunWarnings(session));
            Assert.Equal("warns about cheats and mods as a run starts", QwarkSettings.RunWarningsStatus(session));

            await client.RunWarnAsync(false);
            Assert.False(server.RunWarnings);
            session = (await client.GetStateAsync()).Session;
            Assert.True(session.RunWarningsOff);
            Assert.Equal("no warnings", QwarkSettings.RunWarningsStatus(session));
            Assert.True(await WaitFor(() =>
                client.LatestTelemetry is { Session.RunWarningsOff: true } && !client.TelemetryViaTcp));

            await client.RunWarnAsync(true);
            Assert.True(server.RunWarnings);
            Assert.True(await WaitFor(() =>
                client.LatestTelemetry is { Session.RunWarningsOff: false } && !client.TelemetryViaTcp));
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public async Task RunWarnRefusesAByteThatIsNeitherOffNorOn(byte value)
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
            Assert.False(QwarkSettings.ShowsRunWarnings(session));
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

        // Bits 2 to 4 are the telemetry rate's since qwark revision 1.32; bits 5 to 7 mean nothing yet.
        bytes[31] = 0xE0;
        Assert.Equal(SessionFlags3.None, SessionInfo.Parse(bytes).Flags3);
        bytes[31] = 0xFC;
        Assert.Equal(SessionFlags3.TelemetryRate | SessionFlags3.TelemetryRateMask, SessionInfo.Parse(bytes).Flags3);

        var again = SessionInfo.Parse(info.ToBytes());
        Assert.Equal(info.Flags3, again.Flags3);
    }

    [Fact]
    public void RunWarningsIsASettingsWrite()
    {
        Assert.True(StatusToast.IsSettingsWrite(Opcode.RunWarn));
    }

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
