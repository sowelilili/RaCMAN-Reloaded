using System.Diagnostics;
using System.Text;

using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Mods on the console's USB drive (protocol revision 1.24, qwark build 56) on the client's side:
/// qwark's version rule, the MOD_USB_LIST row and flags2, the rows the Mods panel draws for them,
/// Load keeping a newer console copy over the library's older one, and the whole round trip against
/// the fake console: a USB-only mod listed and loaded, an update taken, one that waits, the copies
/// the console leaves aside, auto-load on, and no list where there is none.
/// </summary>
public class UsbModTests
{
    private const string Title = "NPEA00385";

    private static UsbModEntry Usb(string dir, UsbModState state, string version = "1.0", UsbModFlags flags = UsbModFlags.None,
        UsbModProblem problem = UsbModProblem.None, byte port = 0) =>
        new(port, state, flags, problem, 0xCAFEF00D, dir, "USB " + dir, version, "someone");

    private static ModEntry Row(string dir, string version = "1.0", ModFlags flags = ModFlags.None) =>
        new(0, flags, 0x12345678, dir, dir, version, "someone");

    private static LocalMod Local(string dir, string version = "1.0") =>
        new() { Directory = Path.Combine("x", dir), DirName = dir, Name = "Mod " + dir, Version = version, Hash = 0x12345678 };

    // ================================================================ the version rule

    [Theory]
    [InlineData("1.0", "1.00", 0)]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("v2", "1.9.9", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("0.6", "0.10", -1)]
    [InlineData(" 3.8 ", "3.8", 0)]
    public void VersionsCompareAsDottedNumbers(string a, string b, int order)
    {
        Assert.Equal(order, ModVersion.Compare(a, b));
        Assert.Equal(-order, ModVersion.Compare(b, a));
    }

    [Theory]
    [InlineData("1.0-beta")]
    [InlineData("")]
    [InlineData("final")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1..2")]
    [InlineData(null)]
    public void AnythingElseIsNoVersionToCompare(string? version)
    {
        Assert.Null(ModVersion.Compare(version, "1.0"));
        Assert.False(ModVersion.IsNewer(version, "1.0"));
        Assert.False(ModVersion.IsNewer("9.9", version));
    }

    // ================================================================ the wire

    [Fact]
    public void TheOpcodeAndTheRowAreRevision124s()
    {
        Assert.Equal(0x0067, (ushort)Opcode.ModUsbList);
        Assert.Equal(UsbModEntry.Size, ModEntry.Size);

        var row = Usb("lock_rng", UsbModState.Newer, "1.2", UsbModFlags.Waiting | UsbModFlags.RootMods, port: 6);
        byte[] bytes = row.ToBytes();
        Assert.Equal(120, bytes.Length);
        Assert.Equal(new byte[] { 6, 2, 3, 0, 0xCA, 0xFE, 0xF0, 0x0D }, bytes[..8]);
        Assert.Equal("lock_rng", Encoding.ASCII.GetString(bytes, 8, 8));
        Assert.Equal("1.2", Encoding.ASCII.GetString(bytes, 72, 3));
        Assert.Equal(row, UsbModEntry.Parse(bytes));

        var list = new byte[1 + 120];
        list[0] = 1;
        bytes.CopyTo(list, 1);
        Assert.Equal(row, Assert.Single(UsbModEntry.ParseList(list)));
        Assert.Empty(UsbModEntry.ParseList(Array.Empty<byte>()));

        Assert.True(row.Waiting);
        Assert.Equal("USB006", row.DriveName);
        Assert.Equal("/dev_usb006/mods/NPEA00385/lock_rng", row.PathFor(Title));
        Assert.Equal("/dev_usb000/qwark/mods/NPEA00385/x", Usb("x", UsbModState.Available).PathFor(Title));
    }

    [Fact]
    public void Flags2IsByte29AndAnOlderModuleSendsZero()
    {
        var info = SessionInfo.Empty with { Flags = SessionFlags.UiPakBad, Flags2 = SessionFlags2.UsbMods };
        byte[] bytes = info.ToBytes();
        Assert.Equal(0x01, bytes[29]);
        Assert.Equal(0, bytes[30]);
        Assert.Equal(0x80, bytes[24]);

        var parsed = SessionInfo.Parse(bytes);
        Assert.True(parsed.UsbMods);
        Assert.True(parsed.UiPakBad);

        Assert.False(SessionInfo.Parse(SessionInfo.Empty.ToBytes()).UsbMods);

        // The per-user settings share the byte, in bits 1 to 3, and neither reads the other's bits.
        var both = SessionInfo.Empty with
        {
            Flags2 = SessionFlags2.UsbMods,
            ProfileFlags = ProfileFlags.Supported | ProfileFlags.On,
            ProfileSeq = 9,
        };
        bytes = both.ToBytes();
        Assert.Equal(0x07, bytes[29]);
        Assert.Equal(9, bytes[30]);
        parsed = SessionInfo.Parse(bytes);
        Assert.True(parsed.UsbMods && parsed.ProfilesSupported && parsed.ProfilesOn && !parsed.ProfileActive);
        Assert.Equal(SessionFlags2.UsbMods, parsed.Flags2);
        Assert.Equal(ProfileFlags.Supported | ProfileFlags.On, parsed.ProfileFlags);
    }

    // ================================================================ the table

    [Fact]
    public void AModOnlyTheUsbDriveHasIsARowOfItsOwnAfterTheConsoles()
    {
        var table = ModTable.Build(new[] { Local("lock_rng") }, new[] { Row("lock_rng"), Row("old-one") },
            new[] { "lock_rng", "old-one" }, new[] { Usb("zz-usb", UsbModState.Available), Usb("aa-usb", UsbModState.Available) });

        Assert.Equal(new[] { "lock_rng", "old-one", "aa-usb", "zz-usb" }, table.Select(r => r.FolderName));

        var usb = table[2];
        Assert.True(usb.UsbOnly);
        Assert.False(usb.ConsoleOnly);
        Assert.Equal("usb:aa-usb", usb.Key);
        Assert.Equal("USB aa-usb", usb.Name);
        Assert.Equal("1.0", usb.Version);
        Assert.Equal("aa-usb", usb.AutoName);
        Assert.Null(usb.ListedName);
        Assert.Equal(("On USB000", ModTone.Quiet), ModTable.ConsoleStatus(usb));

        var actions = ModTable.ActionsFor(usb, rpcs3: false, connected: true, busy: false);
        Assert.True(actions.Load && actions.LoadEnabled);
        Assert.True(actions.AutoEnabled);
        Assert.False(actions.Unload);
        Assert.False(actions.UninstallEnabled);
        Assert.Contains("USB drive", actions.UninstallReason);
        Assert.Contains("Only on the console's USB drive", ModsPanel.TooltipFor(usb));
        Assert.Contains("Load copies it onto the console", ModsPanel.UsbTooltip(usb, Title));

        Assert.False(table[1].UsbOnly);
        Assert.True(table[1].ConsoleOnly);
    }

    [Fact]
    public void AUsbCopyTheConsoleCannotTakeIsSaidAndNotLoaded()
    {
        var row = Assert.Single(ModTable.Build(Array.Empty<LocalMod>(), Array.Empty<ModEntry>(), Array.Empty<string>(),
            new[] { Usb("broken", UsbModState.Broken, problem: UsbModProblem.MissingBin, port: 1) }));

        Assert.Equal(("On USB001, unusable", ModTone.Bad), ModTable.ConsoleStatus(row));
        var actions = ModTable.ActionsFor(row, false, true, false);
        Assert.False(actions.LoadEnabled);
        Assert.Contains("missing", actions.LoadReason);
        Assert.False(actions.AutoEnabled);
    }

    [Fact]
    public void AUsbCopyGoesOntoTheRowOfItsFolderAndSaysWhatItIs()
    {
        static (string, ModTone) Status(UsbModEntry usb, ModFlags flags = ModFlags.None) =>
            ModTable.ConsoleStatus(Assert.Single(ModTable.Build(new[] { Local("lock_rng") }, new[] { Row("lock_rng", flags: flags) },
                new[] { "lock_rng" }, new[] { usb })));

        Assert.Equal(("update ready", ModTone.Update), Status(Usb("lock_rng", UsbModState.Newer, "1.2")));
        Assert.Equal(("loaded, update", ModTone.Good),
            Status(Usb("lock_rng", UsbModState.Newer, "1.2", UsbModFlags.Waiting), ModFlags.Loaded));
        Assert.Equal(("ready", ModTone.Quiet), Status(Usb("lock_rng", UsbModState.Conflict)));
        Assert.Equal(("ready", ModTone.Quiet), Status(Usb("lock_rng", UsbModState.Same)));
        Assert.Equal(("ready", ModTone.Quiet), Status(Usb("lock_rng", UsbModState.Older, "0.9")));

        // A library mod the console does not have yet, and the drive does.
        var notUploaded = Assert.Single(ModTable.Build(new[] { Local("lock_rng") }, Array.Empty<ModEntry>(), Array.Empty<string>(),
            new[] { Usb("lock_rng", UsbModState.Available) }));
        Assert.Equal(("on USB", ModTone.Quiet), ModTable.ConsoleStatus(notUploaded));
        Assert.False(notUploaded.UsbOnly);

        // A console-only mod with an update on the drive.
        var consoleOnly = Assert.Single(ModTable.Build(Array.Empty<LocalMod>(), new[] { Row("old-one") }, new[] { "old-one" },
            new[] { Usb("old-one", UsbModState.Newer, "2.0") }));
        Assert.Equal(("Console only, update ready", ModTone.Update), ModTable.ConsoleOnlyStatus(consoleOnly));
    }

    [Fact]
    public void LoadKeepsANewerConsoleOrUsbCopyRatherThanUploadingTheLibrarysOlderOne()
    {
        var local = Local("lock_rng", "1.1");
        ModRow RowWith(ModEntry? console, UsbModEntry? usb) => new ModRow(local, console, console is null ? null : "lock_rng", true) { Usb = usb };

        Assert.False(ModTable.LoadKeepsNewerConsoleCopy(RowWith(Row("lock_rng", "1.1") with { Hash = 1 }, null)));
        Assert.True(ModTable.LoadKeepsNewerConsoleCopy(RowWith(null, Usb("lock_rng", UsbModState.Available, "1.2"))));
        Assert.True(ModTable.LoadKeepsNewerConsoleCopy(RowWith(Row("lock_rng", "2.0") with { Hash = 1 }, null)));
        Assert.False(ModTable.LoadKeepsNewerConsoleCopy(RowWith(null, Usb("lock_rng", UsbModState.Broken, "9.0", problem: UsbModProblem.TooBig))));
        Assert.False(ModTable.LoadKeepsNewerConsoleCopy(RowWith(null, Usb("lock_rng", UsbModState.Available, "1.1-beta"))));
        Assert.False(ModTable.LoadKeepsNewerConsoleCopy(RowWith(Row("lock_rng", "1.0") with { Hash = 1 }, Usb("lock_rng", UsbModState.Older, "1.0"))));

        // And the console cell says the console's copy is newer rather than stale.
        Assert.Equal("newer on console", ModTable.ConsoleStatus(RowWith(Row("lock_rng", "2.0") with { Hash = 1 }, null)).Text);
        Assert.Equal("stale", ModTable.ConsoleStatus(RowWith(Row("lock_rng", "1.0") with { Hash = 1 }, null)).Text);

        // A console copy that is the drive's (state SAME) is just ready, even with the library behind it.
        Assert.Equal("ready", ModTable.ConsoleStatus(RowWith(Row("lock_rng", "2.0") with { Hash = 1 }, Usb("lock_rng", UsbModState.Same, "2.0"))).Text);
        Assert.Equal("ready", ModTable.ConsoleStatus(RowWith(Row("lock_rng", "1.1"), Usb("lock_rng", UsbModState.Same, "1.1"))).Text);
    }

    [Fact]
    public void TheCellIsShortAndTheTooltipHasTheDetail()
    {
        var local = Local("lock_rng", "1.0");
        ModRow RowWith(UsbModEntry usb, bool loaded = false) =>
            new(local, Row("lock_rng", "1.0", loaded ? ModFlags.Loaded : ModFlags.None), "lock_rng", true) { Usb = usb };

        // Every cell fits the 130-pixel column: nothing longer than a few words.
        Assert.Equal(("ready, USB bad", ModTone.Quiet),
            ModTable.ConsoleStatus(RowWith(Usb("lock_rng", UsbModState.Broken, problem: UsbModProblem.TooBig))));

        var update = RowWith(Usb("lock_rng", UsbModState.Newer, "1.1", port: 2));
        string tip = ModsPanel.UsbTooltip(update, Title);
        Assert.Contains("Update ready", tip);
        Assert.Contains("USB version 1.1", tip);
        Assert.Contains("console version 1.0", tip);
        Assert.Contains("/dev_usb002/", tip);
        Assert.Contains("Loading the mod takes the update", tip);

        var waiting = RowWith(Usb("lock_rng", UsbModState.Newer, "1.1", UsbModFlags.Waiting), loaded: true);
        Assert.Contains("next time it loads", ModsPanel.UsbTooltip(waiting, Title));

        string warning = ModsPanel.UsbTooltip(RowWith(Usb("lock_rng", UsbModState.Conflict)), Title);
        Assert.StartsWith("Warning:", warning);
        Assert.Contains("different contents", warning);
        Assert.Contains("uses its own copy", warning);
    }

    // ================================================================ against the fake console

    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync(bool rpcs3 = false, bool usb = true)
    {
        var server = new FakeQwarkServer { Emulator = rpcs3, NoCodePatches = rpcs3, ReportsUsbMods = usb };
        server.Start();
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    [Fact]
    public async Task AUsbOnlyModIsListedAndALoadCopiesItIn()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            Assert.True((await client.HelloAsync()).UsbMods);

            server.AddUsbMod(0, "usb-only", "USB only", "1.0");
            var row = Assert.Single(await client.ModUsbListAsync());
            Assert.Equal(UsbModState.Available, row.State);
            Assert.Equal("USB only", row.Name);
            Assert.DoesNotContain(await client.ModListAsync(), m => m.DirName == "usb-only");

            await client.ModLoadAsync("usb-only");
            Assert.Equal(1, server.UsbTakes);
            var installed = Assert.Single(await client.ModListAsync(), m => m.DirName == "usb-only");
            Assert.True(installed.Loaded);
            Assert.Equal(row.Hash, installed.Hash);
            Assert.True(server.Files.ContainsKey($"{ModLibrary.ConsoleFolder(Title, "usb-only")}/qwark.sum"));
            Assert.Equal(UsbModState.Same, Assert.Single(await client.ModUsbListAsync()).State);
        }
    }

    [Fact]
    public async Task ANewerUsbCopyIsTakenAtTheNextLoadAndWaitsWhileTheModIsLoaded()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.AddConsoleMod("upd", "Upd", "1.0");
            server.AddUsbMod(1, "upd", "Upd", "1.1", word: 0x11111111);

            Assert.Equal(UsbModState.Newer, Assert.Single(await client.ModUsbListAsync()).State);
            await client.ModLoadAsync("upd");
            Assert.Equal("1.1", Assert.Single(await client.ModListAsync(), m => m.DirName == "upd").Version);

            server.AddUsbMod(1, "upd", "Upd", "1.2", word: 0x12121212);
            var waiting = Assert.Single(await client.ModUsbListAsync());
            Assert.Equal(UsbModState.Newer, waiting.State);
            Assert.True(waiting.Waiting);

            await client.ModLoadAsync("upd");
            Assert.Equal("1.1", Assert.Single(await client.ModListAsync(), m => m.DirName == "upd").Version);

            await client.ModUnloadAsync("upd");
            await client.ModLoadAsync("upd");
            Assert.Equal("1.2", Assert.Single(await client.ModListAsync(), m => m.DirName == "upd").Version);
        }
    }

    [Fact]
    public async Task OfSeveralUsbCopiesTheNewestIsListedAndEqualVersionsAreOneMod()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.AddUsbMod(0, "dup", "Dup", "1.0");
            server.AddUsbMod(0, "dup", "Dup", "1.2", rootMods: true);
            server.AddUsbMod(6, "dup", "Dup", "1.1", rootMods: true);
            server.AddUsbMod(1, "same", "Same", "2.0", word: 1);
            server.AddUsbMod(0, "same", "Same", "2.0", rootMods: true, word: 2);

            var rows = await client.ModUsbListAsync();
            var dup = Assert.Single(rows, r => r.DirName == "dup");
            Assert.Equal("1.2", dup.Version);
            Assert.Equal(UsbModFlags.RootMods, dup.Flags);

            var same = Assert.Single(rows, r => r.DirName == "same");
            Assert.Equal(0, same.Port);
            Assert.Equal(UsbModState.Available, same.State);
        }
    }

    [Fact]
    public async Task TheConsolesOwnCopyStaysForAnOlderOrConflictingUsbCopy()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.AddConsoleMod("older", "Older", "1.0");
            server.AddUsbMod(0, "older", "Older", "0.9");
            server.AddConsoleMod("conflict", "Conflict", "1.0");
            server.AddUsbMod(0, "conflict", "Conflict", "1.0", word: 0x22222222);
            server.AddConsoleMod("nover", "No version", "1.0");
            server.AddUsbMod(0, "nover", "No version", null, word: 0x33333333);

            var rows = await client.ModUsbListAsync();
            Assert.Equal(UsbModState.Older, rows.Single(r => r.DirName == "older").State);
            Assert.Equal(UsbModState.Conflict, rows.Single(r => r.DirName == "conflict").State);
            Assert.Equal(UsbModState.Conflict, rows.Single(r => r.DirName == "nover").State);

            foreach (var dir in new[] { "older", "conflict", "nover" }) await client.ModLoadAsync(dir);
            Assert.Equal(0, server.UsbTakes);
        }
    }

    [Fact]
    public async Task AutoLoadOnForAUsbOnlyModCopiesItIn()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.AddUsbMod(2, "autoon", "Auto on");
            await client.ModSetAutoAsync("autoon", true);

            var row = Assert.Single(await client.ModListAsync(), m => m.DirName == "autoon");
            Assert.True(row.Auto);
            Assert.False(row.Loaded);
            Assert.Equal(1, server.UsbTakes);
        }
    }

    [Fact]
    public async Task ThereIsNoListUnderRpcs3NorFromAnOlderModule()
    {
        var (server, client) = await ConnectAsync(rpcs3: true);
        using (server)
        using (client)
        {
            server.AddUsbMod(0, "usb-only", "USB only");
            Assert.False((await client.HelloAsync()).UsbMods);
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.ModUsbListAsync());
            Assert.Equal(Status.Unsupported, refused.Status);
        }

        (server, client) = await ConnectAsync(usb: false);
        using (server)
        using (client)
        {
            Assert.False((await client.HelloAsync()).UsbMods);
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.ModUsbListAsync());
            Assert.Equal(Status.UnknownOp, refused.Status);
        }
    }

    [Fact]
    public async Task TheAppReadsTheListOnlyWhileTheConsoleSaysItReadsItsDrives()
    {
        foreach (bool usb in new[] { true, false })
        {
            using var server = new FakeQwarkServer { ReportsUsbMods = usb };
            server.Start();
            server.AddUsbMod(0, "usb-only", "USB only");

            using var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
            state.Client.AutoReconnect = false;
            await state.Client.ConnectAsync("127.0.0.1", server.Port);

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000 && (!state.Connected || (usb && state.ConsoleUsbMods.Length == 0)))
            {
                state.Tick(1f / 60f);
                if (state.Connected && state.Session.UsbMods == usb) state.RefreshUsbMods();
                await Task.Delay(16);
            }

            state.Tick(1f / 60f);
            if (usb)
            {
                Assert.Equal("usb-only", Assert.Single(state.ConsoleUsbMods).DirName);
                Assert.Contains(state.ModRows(), row => row.UsbOnly && row.FolderName == "usb-only");
            }
            else
            {
                Assert.Empty(state.ConsoleUsbMods);
                Assert.DoesNotContain(server.RequestLog(), op => op == Opcode.ModUsbList);
            }

        }
    }
}
