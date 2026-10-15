using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using RaCMAN.App;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The mods the console has and the library does not, taking a mod off the console and the PC,
/// and folder names qwark can use: MOD_LIST carries a folder name in <c>char dirname[32]</c>, so a
/// folder of more than 31 bytes is listed cut short and never loads. Every folder here is a
/// temporary one and every console the fake one.
/// </summary>
public class ModConsoleTests : IDisposable
{
    private const string Title = "NPEA00385";

    /// <summary>32 characters, the length of the temporary folder a root-level ZIP used to be installed under.</summary>
    private const string GuidFolder = "0f8fad5bd9cb469fa16570867728950e";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "racman-modconsole-" + Guid.NewGuid().ToString("N"));

    public ModConsoleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }

    private string UserRoot => Path.Combine(_root, "user-mods");

    private string ShippedRoot => Path.Combine(_root, "shipped-mods");

    private ModLibrary Library() => new(UserRoot, ShippedRoot);

    private static string Patch(string name, string version = "1.0") =>
        $"#- name: {name}\n#- version: {version}\n#- author: someone\n0x00100000: 0x60000000\n";

    private static LocalMod MakeMod(string parent, string dir, string name, string version = "1.0")
    {
        string folder = Path.Combine(parent, Title, dir);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "patch.txt"), Patch(name, version));
        return ModLibrary.Read(folder, shipped: parent.EndsWith("shipped-mods", StringComparison.Ordinal))!;
    }

    private static ModEntry Row(string dir, string name = "", ModFlags flags = ModFlags.None, byte index = 0) =>
        new(index, flags, 0x12345678, dir, name.Length == 0 ? dir : name, "1.0", "someone");

    private static LocalMod Local(string dir, string name = "Mod", bool shipped = false) =>
        new() { Directory = Path.Combine("x", dir), DirName = dir, Name = name, Shipped = shipped };

    // ================================================================ folder names

    [Fact]
    public void AFolderNameIsLowercaseLettersDigitsDashesAndUnderscores()
    {
        Assert.Equal("last-ground-display-v1-2", ModFolderName.Sanitise("Last Ground Display (v1.2)"));
        Assert.Equal("emile-s_mod", ModFolderName.Sanitise("  Émile's_Mod!!  "));
        Assert.Equal("a-b", ModFolderName.Sanitise("a -- b"));
        Assert.Equal(string.Empty, ModFolderName.Sanitise("!!! ???"));
        Assert.Equal(string.Empty, ModFolderName.Sanitise("__"));
        Assert.Equal(string.Empty, ModFolderName.Sanitise(null));

        string trimmed = ModFolderName.Sanitise(new string('x', 20) + " " + new string('y', 40));
        Assert.Equal(ModFolderName.MaxBytes, trimmed.Length);
        Assert.False(trimmed.EndsWith('-'));
    }

    [Fact]
    public void AListedNameIsTheFirst31BytesAsTheWireDecodesThem()
    {
        Assert.Equal("short", ModFolderName.AsListed("short"));
        Assert.Equal(GuidFolder[..31], ModFolderName.AsListed(GuidFolder));
        Assert.True(ModFolderName.Fits(GuidFolder[..31]));
        Assert.False(ModFolderName.Fits(GuidFolder));

        // A character cut in half reads the way the row's own bytes read.
        string straddling = new string('a', 30) + "é";
        var wire = new byte[32];
        new SpanWriter(wire).WriteFixedString(straddling, 31);
        Assert.Equal(SpanReader.DecodeFixed(wire), ModFolderName.AsListed(straddling));
    }

    [Fact]
    public void AUniqueNameCountsUpWithinTheLimit()
    {
        var taken = new HashSet<string> { "my-mod", "my-mod-2" };
        Assert.Equal("fresh", ModFolderName.Unique("fresh", taken.Contains));
        Assert.Equal("my-mod-3", ModFolderName.Unique("my-mod", taken.Contains));

        string full = new string('z', 31);
        var name = ModFolderName.Unique(full, n => n == full);
        Assert.Equal(new string('z', 29) + "-2", name);
        Assert.True(ModFolderName.Fits(name));
    }

    // ================================================================ matching MOD_LIST with DIR_LIST

    [Fact]
    public void AFolderQwarkCutShortIsPairedWithItsRowByPrefix()
    {
        var rows = new[] { Row("lock_rng"), Row(GuidFolder[..31], "Last Ground Display") };
        var pairs = ConsoleModMatch.Match(new[] { GuidFolder, "lock_rng", "no-row" }, rows);

        Assert.Equal(GuidFolder, pairs[0].Folder);
        Assert.Equal("Last Ground Display", pairs[0].Row!.Name);
        Assert.Equal("lock_rng", pairs[1].Row!.DirName);
        Assert.Null(pairs[2].Row);
        Assert.Equal(3, pairs.Count);
    }

    [Fact]
    public void AnExactFolderIsPairedBeforeALongerFolderSharingItsFirst31Bytes()
    {
        // Two rows of the same 31 bytes: one is the folder of exactly that name, the other the
        // longer folder qwark cut to it. Each row is claimed once, the exact folder first.
        string exact = GuidFolder[..31];
        var rows = new[] { Row(exact, "First row", index: 0), Row(exact, "Second row", index: 1) };
        var pairs = ConsoleModMatch.Match(new[] { GuidFolder, exact }, rows);

        Assert.Equal("First row", pairs.Single(p => p.Folder == exact).Row!.Name);
        Assert.Equal("Second row", pairs.Single(p => p.Folder == GuidFolder).Row!.Name);

        // And with only the exact folder there, the longer one's row is left over rather than taken for it.
        var alone = ConsoleModMatch.Match(new[] { exact }, rows);
        Assert.Equal("First row", alone.Single(p => p.Folder == exact).Row!.Name);
        Assert.Contains(alone, p => p.Folder is null && p.Row!.Name == "Second row");
    }

    [Fact]
    public void RowsWithoutAFolderAndFoldersWithoutARowAreKept()
    {
        var pairs = ConsoleModMatch.Match(new[] { "only-folder" }, new[] { Row("only-row") });
        Assert.Contains(pairs, p => p.Folder == "only-folder" && p.Row is null);
        Assert.Contains(pairs, p => p.Folder is null && p.Row!.DirName == "only-row");
    }

    [Fact]
    public void DirListsDotEntriesAndFilesAreNotModFolders()
    {
        var entries = new[]
        {
            new DirEntry(true, 0, "."), new DirEntry(true, 0, ".."), new DirEntry(false, 3, "readme.txt"),
            new DirEntry(true, 0, "lock_rng"),
        };

        Assert.Equal(new[] { "lock_rng" }, ConsoleModMatch.FoldersIn(entries));
    }

    // ================================================================ the rows

    [Fact]
    public void ConsoleOnlyModsComeAfterTheLibraryInTheirOwnOrder()
    {
        var locals = new[] { Local("lock_rng", "Lock RNG") };
        var rows = new[]
        {
            Row("lock_rng", "Lock RNG"),
            Row("sfhelper-old", "Savefile helper", ModFlags.Auto),
            Row(GuidFolder[..31], "Last Ground Display"),
        };

        var table = ModTable.Build(locals, rows, new[] { "lock_rng", "sfhelper-old", GuidFolder });

        Assert.Equal(3, table.Count);
        Assert.False(table[0].ConsoleOnly);
        Assert.Equal("lock_rng", table[0].Console!.DirName);

        Assert.Equal(new[] { "Last Ground Display", "Savefile helper" }, table.Skip(1).Select(r => r.Name));
        Assert.All(table.Skip(1), row => Assert.True(row.ConsoleOnly));

        // The truncated one: its folder's whole name, its row's name, and why qwark cannot load it.
        var cut = table[1];
        Assert.Equal(GuidFolder, cut.FolderName);
        Assert.Equal(GuidFolder[..31], cut.ListedName);
        Assert.True(cut.FolderNameTooLong);
        Assert.Equal("Console only, folder name too long", ModTable.ConsoleOnlyStatus(cut).Text);
        Assert.Equal(ModTone.Bad, ModTable.ConsoleOnlyStatus(cut).Tone);

        var auto = table[2];
        Assert.Equal("Console only", ModTable.ConsoleOnlyStatus(auto).Text);
        Assert.Equal("Console only, auto", ModTable.ConsoleOnlyStatus(auto, withAuto: true).Text);
        Assert.Equal("console:sfhelper-old", auto.Key);
    }

    [Fact]
    public void AConsoleOnlyModQwarkCouldNotParseIsNamedAfterItsWholeFolder()
    {
        var table = ModTable.Build(Array.Empty<LocalMod>(), new[] { Row(GuidFolder[..31], GuidFolder[..31], ModFlags.ParseError) },
            new[] { GuidFolder });

        var row = Assert.Single(table);
        Assert.Equal(GuidFolder, row.Name);
        Assert.Equal("Console only, folder name too long, parse error", ModTable.ConsoleOnlyStatus(row).Text);
    }

    [Fact]
    public void AFolderQwarkDoesNotListAndARowWhoseFolderIsGoneSaySo()
    {
        var table = ModTable.Build(Array.Empty<LocalMod>(), new[] { Row("stale", "Stale", ModFlags.Loaded) }, new[] { "fresh" });

        Assert.Equal("Console only, not listed by qwark", ModTable.ConsoleOnlyStatus(table.Single(r => r.FolderName == "fresh")).Text);

        var gone = table.Single(r => r.FolderName == "stale");
        Assert.Equal("Console only, loaded, folder gone", ModTable.ConsoleOnlyStatus(gone).Text);
        Assert.Equal(ModTone.Good, ModTable.ConsoleOnlyStatus(gone).Tone);
    }

    [Fact]
    public void ALibraryModWithATooLongFolderIsPairedWithItsCutRowAndFlagged()
    {
        var locals = new[] { Local(GuidFolder, "Last Ground Display") };
        var rows = new[] { Row(GuidFolder[..31], "Last Ground Display", ModFlags.Auto) };

        foreach (var folders in new IReadOnlyList<string>?[] { new[] { GuidFolder }, null })
        {
            var table = ModTable.Build(locals, rows, folders);
            var row = Assert.Single(table);
            Assert.False(row.ConsoleOnly);
            Assert.Equal(GuidFolder[..31], row.ListedName);
            Assert.Equal(("folder name too long", ModTone.Bad), ModTable.ConsoleStatus(row));
        }
    }

    [Fact]
    public void BeforeDirListIsReadTheConsolesModsAreMOD_LISTsAlone()
    {
        var table = ModTable.Build(new[] { Local("lock_rng") }, new[] { Row("lock_rng"), Row("extra") }, null);

        Assert.Equal(2, table.Count);
        Assert.All(table, row => Assert.False(row.FoldersListed));
        Assert.Equal("extra", table[1].FolderName);
        Assert.Equal("Reading the console's mod folder...", ModTable.ActionsFor(table[1], false, true, false).UninstallReason);
    }

    [Fact]
    public void ALibraryModsConsoleCellIsTheOneItWas()
    {
        var mod = new LocalMod { Directory = "x", DirName = "lock_rng", Name = "Lock RNG", Hash = 0x12345678 };
        Assert.Equal(("not uploaded", ModTone.Quiet), ModTable.ConsoleStatus(new ModRow(mod, null, null, true)));
        Assert.Equal(("ready", ModTone.Quiet), ModTable.ConsoleStatus(new ModRow(mod, Row("lock_rng"), "lock_rng", true)));
        Assert.Equal(("loaded", ModTone.Good), ModTable.ConsoleStatus(new ModRow(mod, Row("lock_rng", flags: ModFlags.Loaded), "lock_rng", true)));
        Assert.Equal(("stale", ModTone.Quiet),
            ModTable.ConsoleStatus(new ModRow(mod, Row("lock_rng") with { Hash = 1 }, "lock_rng", true)));
    }

    // ================================================================ what each row offers

    [Fact]
    public void OnAConsoleEachRowOffersWhatItsStateAllows()
    {
        var notLoaded = new ModRow(Local("lock_rng"), Row("lock_rng"), "lock_rng", true);
        var actions = ModTable.ActionsFor(notLoaded, rpcs3: false, connected: true, busy: false);
        Assert.True(actions.Load && actions.LoadEnabled);
        Assert.False(actions.Unload);
        Assert.True(actions.AutoEnabled);
        Assert.True(actions.UninstallEnabled);
        Assert.False(actions.Rename);

        var loaded = notLoaded with { Console = Row("lock_rng", flags: ModFlags.Loaded) };
        actions = ModTable.ActionsFor(loaded, false, true, false);
        Assert.False(actions.Load);
        Assert.True(actions.Unload);

        // Not uploaded yet: Load uploads it, and there is no auto flag to set.
        actions = ModTable.ActionsFor(new ModRow(Local("lock_rng"), null, null, true), false, true, false);
        Assert.True(actions.LoadEnabled);
        Assert.False(actions.AutoEnabled);

        // A user mod whose folder is too long: no load, a rename. A shipped one: no rename.
        var tooLong = new ModRow(Local(GuidFolder), null, null, true);
        actions = ModTable.ActionsFor(tooLong, false, true, false);
        Assert.False(actions.LoadEnabled);
        Assert.Contains("31 bytes", actions.LoadReason);
        Assert.Contains("Rename folder", actions.LoadReason);
        Assert.True(actions.Rename);
        Assert.False(ModTable.ActionsFor(tooLong with { Local = Local(GuidFolder, shipped: true) }, false, true, false).Rename);
    }

    [Fact]
    public void AConsoleOnlyModLoadsOnlyWhenQwarkCanAndUnloadsWhenLoaded()
    {
        var fits = new ModRow(null, Row("sfhelper-old"), "sfhelper-old", true);
        var actions = ModTable.ActionsFor(fits, false, true, false);
        Assert.True(actions.Load && actions.LoadEnabled);
        Assert.True(actions.AutoEnabled);
        Assert.True(actions.UninstallEnabled);
        Assert.False(actions.Rename);

        actions = ModTable.ActionsFor(fits with { Console = Row("sfhelper-old", flags: ModFlags.Loaded) }, false, true, false);
        Assert.True(actions.Unload);
        Assert.False(actions.Load);

        var cut = new ModRow(null, Row(GuidFolder[..31]), GuidFolder, true);
        actions = ModTable.ActionsFor(cut, false, true, false);
        Assert.True(actions.Load);
        Assert.False(actions.LoadEnabled);
        Assert.Contains("31 bytes", actions.LoadReason);
        Assert.DoesNotContain("Rename", actions.LoadReason);
        Assert.True(actions.UninstallEnabled);

        var unlisted = new ModRow(null, null, "fresh", true);
        actions = ModTable.ActionsFor(unlisted, false, true, false);
        Assert.False(actions.LoadEnabled);
        Assert.Contains("Rescan console folder", actions.LoadReason);
        Assert.False(actions.AutoEnabled);

        var broken = new ModRow(null, Row("broken", flags: ModFlags.ParseError), "broken", true);
        Assert.False(ModTable.ActionsFor(broken, false, true, false).LoadEnabled);
    }

    [Fact]
    public void UnderRpcs3ThereIsOnlyUninstallAndNothingWithoutAConnection()
    {
        foreach (var row in new[]
                 {
                     new ModRow(Local("lock_rng"), Row("lock_rng", flags: ModFlags.Loaded | ModFlags.Auto), "lock_rng", true),
                     new ModRow(null, Row("sfhelper-old", flags: ModFlags.Loaded), "sfhelper-old", true),
                 })
        {
            var actions = ModTable.ActionsFor(row, rpcs3: true, connected: true, busy: false);
            Assert.False(actions.Load || actions.Unload || actions.AutoEnabled);
            Assert.True(actions.UninstallEnabled);

            var offline = ModTable.ActionsFor(row, rpcs3: true, connected: false, busy: false);
            Assert.False(offline.UninstallEnabled);
            Assert.Equal("Connect to qwark-rpcs3 first.", offline.UninstallReason);

            Assert.False(ModTable.ActionsFor(row, rpcs3: true, connected: true, busy: true).UninstallEnabled);
        }

        var ps3Offline = ModTable.ActionsFor(new ModRow(Local("lock_rng"), null, null, true), false, false, false);
        Assert.False(ps3Offline.LoadEnabled || ps3Offline.UninstallEnabled || ps3Offline.AutoEnabled);
        Assert.Equal("Connect to the console first.", ps3Offline.LoadReason);
    }

    [Fact]
    public void ATooLongFolderCannotBeEnabledUnderRpcs3()
    {
        var mod = new LocalMod { Directory = "x", DirName = GuidFolder, Name = "Last Ground Display", PatchWordCount = 1 };
        var disk = new Rpcs3PatchDisk(Title, new Rpcs3FolderLookup(Rpcs3Patches.Build(_root, Windows(), "test"), string.Empty),
            new ExecutableHashLookup(Hash, string.Empty),
            new PatchFileState("x", PatchFileKind.Missing, Array.Empty<PatchFileEntry>(), string.Empty), false, string.Empty);
        var status = Rpcs3ModsController.Decide(new ModRpcs3Facts(mod, null, disk));

        Assert.Equal(ModRpcs3State.FolderNameTooLong, status.State);
        Assert.False(status.CanToggle);
        Assert.Contains("Rename folder", status.Tooltip);
    }

    // ================================================================ uninstall: the plan

    [Fact]
    public void ALoadedAutoUserModIsUnloadedTurnedOffAndDeletedInThatOrder()
    {
        var mod = new LocalMod { Directory = Path.Combine(UserRoot, Title, "bolt"), DirName = "bolt", Name = "Bolt mod" };
        var row = new ModRow(mod, Row("bolt", flags: ModFlags.Loaded | ModFlags.Auto), "bolt", true);

        var plan = ModUninstall.Plan(row, Title, rpcs3: false);

        Assert.Equal(
            new[] { UninstallStepKind.Unload, UninstallStepKind.AutoOff, UninstallStepKind.DeleteConsoleFolder, UninstallStepKind.DeleteLibraryFolder },
            plan.Steps.Select(s => s.Kind));
        Assert.Contains("/dev_hdd0/qwark/mods/NPEA00385/bolt", plan.Steps[2].Text);
        Assert.Contains(mod.Directory, plan.Steps[3].Text);
        Assert.Empty(plan.Notes);

        // Under RPCS3 LOADED means in game memory, which no unload changes.
        Assert.DoesNotContain(ModUninstall.Plan(row, Title, rpcs3: true).Steps, s => s.Kind == UninstallStepKind.Unload);
    }

    [Fact]
    public void AShippedModLosesOnlyItsConsoleCopyAndTheQuestionSaysWhy()
    {
        var mod = new LocalMod { Directory = "x", DirName = "flight", Name = "Flight", Shipped = true };
        var plan = ModUninstall.Plan(new ModRow(mod, Row("flight"), "flight", true), Title, rpcs3: false);

        Assert.Equal(new[] { UninstallStepKind.DeleteConsoleFolder }, plan.Steps.Select(s => s.Kind));
        Assert.Contains(plan.Notes, n => n.Contains("ships with RaCMAN Reloaded") && n.Contains("not uploaded"));

        // And one that is not on the console: nothing to do at all.
        var nothing = ModUninstall.Plan(new ModRow(mod, null, null, true), Title, rpcs3: false);
        Assert.True(nothing.NothingToDo);
    }

    [Fact]
    public void AConsoleOnlyModsFolderIsDeletedByItsWholeName()
    {
        var row = new ModRow(null, Row(GuidFolder[..31], "Last Ground Display", ModFlags.Auto), GuidFolder, true);
        var plan = ModUninstall.Plan(row, Title, rpcs3: false);

        Assert.Equal(new[] { UninstallStepKind.AutoOff, UninstallStepKind.DeleteConsoleFolder }, plan.Steps.Select(s => s.Kind));
        Assert.Contains($"/dev_hdd0/qwark/mods/{Title}/{GuidFolder}", plan.Steps[1].Text);
    }

    // ================================================================ uninstall: running it

    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync(bool rpcs3 = false)
    {
        var server = new FakeQwarkServer { Emulator = rpcs3, NoCodePatches = rpcs3 };
        server.Start();
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    /// <summary>The table the panel would draw now: the library, MOD_LIST and DIR_LIST, read afresh.</summary>
    private async Task<IReadOnlyList<ModRow>> TableAsync(QwarkClient client, ModLibrary library)
    {
        IReadOnlyList<string> folders;
        try
        {
            folders = ConsoleModMatch.FoldersIn(await client.DirListAsync(ModLibrary.ConsoleTitleFolder(Title)));
        }
        catch (QwarkStatusException ex) when (ex.Status == Status.NotFound)
        {
            folders = Array.Empty<string>();
        }

        return ModTable.Build(library.Scan(Title), await client.ModListAsync(), folders);
    }

    private static string ConsolePath(string folder) => ModLibrary.ConsoleFolder(Title, folder);

    [Fact]
    public async Task UninstallingALoadedUserModRemovesItFromTheConsoleAndThePc()
    {
        var library = Library();
        var mod = MakeMod(UserRoot, "bolt", "Bolt mod");

        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await library.EnsureUploadedAsync(client, Title, mod, 0);
            await client.ModLoadAsync("bolt");
            await client.ModSetAutoAsync("bolt", true);

            var row = (await TableAsync(client, library)).Single(r => r.FolderName == "bolt");
            Assert.True(row.Loaded);
            var plan = ModUninstall.Plan(row, Title, rpcs3: false);

            server.ClearRequestLog();
            int rescans = server.ModRescanCount;
            var outcome = await ModUninstall.RunAsync(plan, client, library, new Rpcs3PatchWriter());

            Assert.True(outcome.Succeeded, outcome.Failure);
            Assert.Null(outcome.RescanProblem);
            Assert.Equal(new[] { "unloaded", "auto flag off", "deleted from the console", "deleted from your mods folder" }, outcome.Done);
            Assert.Equal("Bolt mod uninstalled: unloaded, auto flag off, deleted from the console, deleted from your mods folder.",
                ModUninstall.Describe(plan, outcome));

            // In that order, the unload before the folder goes, and the rescan last.
            var sent = server.RequestLog().Where(op => op is Opcode.ModUnload or Opcode.ModSetAuto or Opcode.DirDelete or Opcode.ModRescan);
            Assert.Equal(new[] { Opcode.ModUnload, Opcode.ModSetAuto, Opcode.DirDelete, Opcode.ModRescan }, sent);
            Assert.Equal(rescans + 1, server.ModRescanCount);

            Assert.DoesNotContain(server.Files.Keys, k => k.StartsWith(ConsolePath("bolt") + "/", StringComparison.Ordinal));
            Assert.DoesNotContain(server.Mods, m => m.DirName == "bolt");
            Assert.False(Directory.Exists(mod.Directory));
            Assert.DoesNotContain(await TableAsync(client, library), r => r.FolderName == "bolt");
        }
    }

    [Fact]
    public async Task UninstallingAShippedModLeavesItInTheListAsNotUploaded()
    {
        var library = Library();
        var mod = MakeMod(ShippedRoot, "flight", "Flight");
        Assert.True(mod.Shipped);

        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await library.EnsureUploadedAsync(client, Title, mod, 0);
            var row = (await TableAsync(client, library)).Single(r => r.FolderName == "flight");
            Assert.Equal("flight", row.ConsoleFolder);

            var plan = ModUninstall.Plan(row, Title, rpcs3: false);
            var outcome = await ModUninstall.RunAsync(plan, client, library, new Rpcs3PatchWriter());

            Assert.True(outcome.Succeeded, outcome.Failure);
            Assert.True(Directory.Exists(mod.Directory));
            var after = (await TableAsync(client, library)).Single(r => r.FolderName == "flight");
            Assert.False(after.ConsoleOnly);
            Assert.Equal("not uploaded", ModTable.ConsoleStatus(after).Text);
        }
    }

    [Fact]
    public async Task UninstallingAConsoleOnlyModWithAFolderNameQwarkCutShortDeletesTheWholeFolder()
    {
        var library = Library();
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.AddConsoleMod(GuidFolder, "Last Ground Display", auto: true);
            server.AddConsoleMod("sfhelper-old", "Savefile helper", auto: true);

            // qwark lists the long one cut short, and cannot load it: its files are not under that name.
            var listed = await client.ModListAsync();
            Assert.Contains(listed, m => m.DirName == GuidFolder[..31]);
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.ModLoadAsync(GuidFolder[..31]));
            Assert.Equal(Status.IoError, refused.Status);

            var table = await TableAsync(client, library);
            var cut = table.Single(r => r.FolderName == GuidFolder);
            Assert.True(cut.ConsoleOnly);
            Assert.True(cut.FolderNameTooLong);
            Assert.Equal("Last Ground Display", cut.Name);

            var outcome = await ModUninstall.RunAsync(ModUninstall.Plan(cut, Title, rpcs3: false), client, library, new Rpcs3PatchWriter());
            Assert.True(outcome.Succeeded, outcome.Failure);
            Assert.Equal(new[] { "auto flag off", "deleted from the console" }, outcome.Done);

            Assert.DoesNotContain(server.Files.Keys, k => k.StartsWith(ConsolePath(GuidFolder), StringComparison.Ordinal));
            Assert.DoesNotContain(server.Mods, m => m.DirName == GuidFolder[..31]);

            // The other one is untouched, and still auto.
            var other = (await TableAsync(client, library)).Single(r => r.FolderName == "sfhelper-old");
            Assert.True(other.Console!.Auto);
        }
    }

    [Fact]
    public async Task ALoadedConsoleOnlyModIsUnloadedBeforeItsFolderGoes()
    {
        var library = Library();
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.AddConsoleMod("sfhelper-old", "Savefile helper", auto: true);
            await client.ModLoadAsync("sfhelper-old");

            var row = (await TableAsync(client, library)).Single(r => r.FolderName == "sfhelper-old");
            Assert.True(row.ConsoleOnly && row.Loaded);
            Assert.True(ModTable.ActionsFor(row, false, true, false).Unload);

            var plan = ModUninstall.Plan(row, Title, rpcs3: false);
            Assert.Equal(new[] { UninstallStepKind.Unload, UninstallStepKind.AutoOff, UninstallStepKind.DeleteConsoleFolder },
                plan.Steps.Select(s => s.Kind));

            var outcome = await ModUninstall.RunAsync(plan, client, library, new Rpcs3PatchWriter());
            Assert.True(outcome.Succeeded, outcome.Failure);
            Assert.Empty((await TableAsync(client, library)).Where(r => r.FolderName == "sfhelper-old"));
        }
    }

    [Fact]
    public async Task TheFirstStepThatFailsStopsTheRestAndSaysWhatTheConsoleAnswered()
    {
        var library = Library();
        var mod = MakeMod(UserRoot, "bolt", "Bolt mod");

        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await library.EnsureUploadedAsync(client, Title, mod, 0);
            await client.ModLoadAsync("bolt");
            await client.ModSetAutoAsync("bolt", true);
            var plan = ModUninstall.Plan((await TableAsync(client, library)).Single(r => r.FolderName == "bolt"), Title, rpcs3: false);

            server.RefusedOps[Opcode.ModSetAuto] = Status.IoError;
            server.ClearRequestLog();
            var outcome = await ModUninstall.RunAsync(plan, client, library, new Rpcs3PatchWriter());

            Assert.False(outcome.Succeeded);
            Assert.Equal(new[] { "unloaded" }, outcome.Done);
            Assert.Contains("IoError", outcome.Failure);
            Assert.Contains("ModSetAuto", outcome.Failure);
            Assert.StartsWith("Bolt mod was not uninstalled: \"Turn its auto flag off", ModUninstall.Describe(plan, outcome));
            Assert.EndsWith("Done before that: unloaded.", ModUninstall.Describe(plan, outcome));

            // Nothing after it: the console's folder and the library's are both still there.
            Assert.DoesNotContain(Opcode.DirDelete, server.RequestLog());
            Assert.Contains(server.Files.Keys, k => k.StartsWith(ConsolePath("bolt") + "/", StringComparison.Ordinal));
            Assert.True(Directory.Exists(mod.Directory));
        }
    }

    [Fact]
    public async Task AFailedFirstStepChangesNothing()
    {
        var library = Library();
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.AddConsoleMod("sfhelper-old", "Savefile helper");
            var plan = ModUninstall.Plan((await TableAsync(client, library)).Single(r => r.FolderName == "sfhelper-old"), Title, rpcs3: false);
            Assert.Equal(new[] { UninstallStepKind.DeleteConsoleFolder }, plan.Steps.Select(s => s.Kind));

            server.RefusedOps[Opcode.DirDelete] = Status.IoError;
            var outcome = await ModUninstall.RunAsync(plan, client, library, new Rpcs3PatchWriter());

            Assert.False(outcome.Succeeded);
            Assert.EndsWith("Nothing was changed.", ModUninstall.Describe(plan, outcome));
            Assert.Contains(server.Mods, m => m.DirName == "sfhelper-old");
        }
    }

    [Fact]
    public async Task AFolderAlreadyGoneIsNotAFailure()
    {
        var library = Library();
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.AddConsoleMod("sfhelper-old", "Savefile helper");
            var plan = ModUninstall.Plan((await TableAsync(client, library)).Single(r => r.FolderName == "sfhelper-old"), Title, rpcs3: false);

            await client.DirDeleteAsync(ConsolePath("sfhelper-old"));
            var outcome = await ModUninstall.RunAsync(plan, client, library, new Rpcs3PatchWriter());

            Assert.True(outcome.Succeeded, outcome.Failure);
            Assert.Equal(new[] { "already gone from the console" }, outcome.Done);
        }
    }

    // ================================================================ uninstall under RPCS3

    private const string Hash = "PPU-ec77eaf73a4f55d1c4ece532c3be6db0011e49ca";

    private const string OtherHash = "PPU-0123456789abcdef0123456789abcdef01234567";

    private static Rpcs3Environment Windows() => new(true, false, _ => null, @"C:\Users\nobody", Array.Empty<string>);

    private static PatchFileEntry ModEntry(string hash, string dir, string name, uint at) =>
        new(hash, "RaC1", new[] { new PatchWord(at, 0x60000000) }, Array.Empty<PatchByte>(), "notes",
            Rpcs3Patches.ModDescription(name, dir), "someone");

    /// <summary>An RPCS3 folder whose RaCMAN patch file holds the helper, lock_rng for two executables and bot, all switched on.</summary>
    private Rpcs3Folder Rpcs3WithEntries(bool config = true)
    {
        string root = Path.Combine(_root, "rpcs3");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "patches"));
        var folder = Rpcs3Patches.Build(root, Windows(), "test");

        var helper = new PatchFileEntry(Hash, "RaC1", new[] { new PatchWord(0x10, 0x60000000) }, Array.Empty<PatchByte>(), "helper");
        var entries = new[]
        {
            helper,
            ModEntry(Hash, "lock_rng", "Lock RNG", 0x100),
            ModEntry(OtherHash, "lock_rng", "Lock RNG", 0x100),
            ModEntry(Hash, "bot", "Bot Info", 0x200),
        };

        File.WriteAllText(folder.PatchFile(Title), Rpcs3Patches.BuildPatchFile(Title, entries));
        if (config)
        {
            File.WriteAllText(folder.PatchConfigFile, Rpcs3Patches.EnablePatches(null, entries.Select(e => e.Key(Title)), folder.PatchConfigFile));
        }

        return folder;
    }

    [Fact]
    public async Task UnderRpcs3UninstallTakesTheModsEntriesAndSwitchesOutAndKeepsEveryOther()
    {
        var library = Library();
        var mod = MakeMod(UserRoot, "lock_rng", "Lock RNG");
        var folder = Rpcs3WithEntries();

        var (server, client) = await ConnectAsync(rpcs3: true);
        using (server)
        using (client)
        {
            await library.EnsureUploadedAsync(client, Title, mod, 0);
            var row = (await TableAsync(client, library)).Single(r => r.FolderName == "lock_rng");

            var look = new Rpcs3UninstallLook(folder, Rpcs3Patches.ReadPatchFile(folder.PatchFile(Title)), string.Empty);
            var plan = ModUninstall.Plan(row, Title, rpcs3: true, look);
            Assert.Equal(new[] { UninstallStepKind.RemoveRpcs3Patch, UninstallStepKind.DeleteConsoleFolder, UninstallStepKind.DeleteLibraryFolder },
                plan.Steps.Select(s => s.Kind));
            Assert.Contains("(2 entries)", plan.Steps[0].Text);
            Assert.Contains("qwark-rpcs3's folder", plan.Steps[1].Text);

            var outcome = await ModUninstall.RunAsync(plan, client, library, new Rpcs3PatchWriter());
            Assert.True(outcome.Succeeded, outcome.Failure);
            Assert.Equal(new[] { "RPCS3 patch removed", "deleted from qwark-rpcs3's folder", "deleted from your mods folder" }, outcome.Done);

            var file = Rpcs3Patches.ReadPatchFile(folder.PatchFile(Title));
            Assert.Equal(PatchFileKind.Ours, file.Kind);
            Assert.Empty(file.ModEntries("lock_rng"));
            Assert.NotNull(file.EntryFor(Hash));
            Assert.NotNull(file.ModEntryFor(Hash, "bot"));

            string config = File.ReadAllText(folder.PatchConfigFile);
            Assert.False(Rpcs3Patches.IsEnabled(config, ModEntry(Hash, "lock_rng", "Lock RNG", 0x100).Key(Title), "x"));
            Assert.False(Rpcs3Patches.IsEnabled(config, ModEntry(OtherHash, "lock_rng", "Lock RNG", 0x100).Key(Title), "x"));
            Assert.True(Rpcs3Patches.IsEnabled(config, file.EntryFor(Hash)!.Key(Title), "x"));
            Assert.True(Rpcs3Patches.IsEnabled(config, file.ModEntryFor(Hash, "bot")!.Key(Title), "x"));

            Assert.False(Directory.Exists(mod.Directory));
            Assert.DoesNotContain(server.Files.Keys, k => k.StartsWith(ConsolePath("lock_rng") + "/", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void RemovingAModFromRpcs3WithNoEntryWritesNothingAndWithNoConfigLeavesItAbsent()
    {
        var folder = Rpcs3WithEntries(config: false);
        Assert.Null(Rpcs3Patches.PlanRemoveMod(folder, Title, "never-enabled"));

        var plan = Rpcs3Patches.PlanRemoveMod(folder, Title, "lock_rng")!;
        Assert.Null(plan.ConfigText);
        new Rpcs3PatchWriter().Commit(() => plan);
        Assert.False(File.Exists(folder.PatchConfigFile));
        Assert.Empty(Rpcs3Patches.ReadPatchFile(folder.PatchFile(Title)).ModEntries("lock_rng"));
    }

    [Fact]
    public void AnRpcs3FolderThatCannotBeFoundIsSaidAndChangesNothingThere()
    {
        var row = new ModRow(null, Row("sfhelper-old"), "sfhelper-old", true);
        var plan = ModUninstall.Plan(row, Title, rpcs3: true, new Rpcs3UninstallLook(null, null, "RPCS3 is not running"));

        Assert.Equal(new[] { UninstallStepKind.DeleteConsoleFolder }, plan.Steps.Select(s => s.Kind));
        Assert.Contains(plan.Notes, n => n.Contains("RPCS3 is not running") && n.Contains("no RPCS3 patch is changed"));
    }

    // ================================================================ the panel's data, end to end

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
    public async Task AModOnlyTheConsoleHasShowsUpAndUninstallTakesItAway()
    {
        using var server = new FakeQwarkServer();
        server.AddConsoleMod("sfhelper-old", "Savefile helper", auto: true);
        server.AddConsoleMod(GuidFolder, "Last Ground Display");
        server.Start();

        using var state = new AppState(new Settings { Ps3 = { AutoReconnect = false }, ModsPath = UserRoot });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.ConsoleFolders is not null
                                                 && state.ModRows().Count(r => r.ConsoleOnly && r.ConsoleFolder is not null) == 2));

        var rows = state.ModRows();
        var helper = rows.Single(r => r.FolderName == "sfhelper-old");
        Assert.Equal("Savefile helper", helper.Name);
        Assert.True(helper.Console!.Auto);

        var cut = rows.Single(r => r.FolderName == GuidFolder);
        Assert.Equal("Last Ground Display", cut.Name);
        Assert.Contains("folder name too long", ModTable.ConsoleOnlyStatus(cut).Text);

        ModUninstall.Start(state, ModUninstall.Plan(helper, Title, rpcs3: false));
        Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Text.StartsWith("Savefile helper", StringComparison.Ordinal))));
        Assert.Contains(state.Toasts, t => t.Kind == ToastKind.Success
                                           && t.Text == "Savefile helper uninstalled: auto flag off, deleted from the console.");

        Assert.True(await PumpAsync(state, () => state.ModRows().All(r => r.FolderName != "sfhelper-old")));
        Assert.Contains(state.ModRows(), r => r.FolderName == GuidFolder);
    }

    // ================================================================ the fake, as qwark

    [Fact]
    public async Task TheFakeListsFoldersAsQwarkDoes()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var missing = await Assert.ThrowsAsync<QwarkStatusException>(() => client.DirListAsync(ModLibrary.ConsoleTitleFolder(Title)));
            Assert.Equal(Status.NotFound, missing.Status);
            var gone = await Assert.ThrowsAsync<QwarkStatusException>(() => client.DirDeleteAsync(ConsolePath("nothing")));
            Assert.Equal(Status.NotFound, gone.Status);

            server.AddConsoleMod(GuidFolder, "Last Ground Display");
            Assert.Equal(new[] { GuidFolder },
                ConsoleModMatch.FoldersIn(await client.DirListAsync(ModLibrary.ConsoleTitleFolder(Title))));
            Assert.Contains(await client.ModListAsync(), m => m.DirName == GuidFolder[..31] && m.Name == "Last Ground Display");

            var unknown = await Assert.ThrowsAsync<QwarkStatusException>(() => client.ModSetAutoAsync("not-a-mod", true));
            Assert.Equal(Status.NotFound, unknown.Status);
        }
    }

    // ================================================================ ZIP installs and folder names

    /// <summary>A ZIP named <paramref name="zipName"/> with patch.txt at its root, as some mods are handed out.</summary>
    private string RootZip(string zipName, string modName, string version = "1.0")
    {
        string staging = Path.Combine(_root, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "patch.txt"), Patch(modName, version));
        File.WriteAllBytes(Path.Combine(staging, "cave.bin"), new byte[] { 1, 2, 3, 4 });

        string folder = Path.Combine(_root, "zips", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string zip = Path.Combine(folder, zipName);
        ZipFile.CreateFromDirectory(staging, zip);
        return zip;
    }

    [Fact]
    public void ARootLevelZipIsNamedAfterTheZipFileAndNeverAfterItsTemporaryFolder()
    {
        var library = Library();
        using var candidate = library.OpenZip(RootZip("Last Ground Display (v2).zip", "Last Ground Display"), Title);

        Assert.Equal("last-ground-display-v2", candidate.Mod.DirName);
        Assert.NotEqual(Path.GetFileName(candidate.TempRoot), candidate.Mod.DirName);
        Assert.Equal(ZipInstallKind.New, candidate.Kind);

        var installed = library.CommitZip(candidate, Title);
        Assert.Equal("last-ground-display-v2", installed.DirName);
        Assert.True(File.Exists(Path.Combine(UserRoot, Title, "last-ground-display-v2", "cave.bin")));
        Assert.False(installed.FolderNameTooLong);
    }

    [Fact]
    public void ALongZipNameIsTrimmedTo31Bytes()
    {
        var library = Library();
        using var candidate = library.OpenZip(RootZip($"{GuidFolder}-{GuidFolder}.zip", "Whatever"), Title);

        Assert.Equal(GuidFolder[..31], candidate.Mod.DirName);
        Assert.True(ModFolderName.Fits(candidate.Mod.DirName));
    }

    [Fact]
    public void AZipNameWithNothingUsableFallsBackToTheModsName()
    {
        var library = Library();
        using var candidate = library.OpenZip(RootZip("!!!.zip", "Bolt Mod"), Title);
        Assert.Equal("bolt-mod", candidate.Mod.DirName);
    }

    [Fact]
    public void AFolderAnotherModHasGetsTheNextFreeSuffixInEitherLibrary()
    {
        var library = Library();
        MakeMod(UserRoot, "my-mod", "Somebody else's mod");
        MakeMod(ShippedRoot, "my-mod-2", "A shipped mod");

        using var candidate = library.OpenZip(RootZip("My Mod.zip", "My Mod"), Title);
        Assert.Equal("my-mod-3", candidate.Mod.DirName);
        Assert.Equal(ZipInstallKind.New, candidate.Kind);
    }

    [Fact]
    public void TheSameModInstalledAgainGoesBackInItsOwnFolderAsAReplace()
    {
        var library = Library();
        using (var first = library.OpenZip(RootZip("my-mod.zip", "My Mod", "1.0"), Title))
        {
            library.CommitZip(first, Title);
        }

        using var again = library.OpenZip(RootZip("my-mod.zip", "My Mod", "1.0"), Title);
        Assert.Equal("my-mod", again.Mod.DirName);
        Assert.Equal(ZipInstallKind.Replace, again.Kind);

        using var newer = library.OpenZip(RootZip("my-mod.zip", "My Mod", "2.0"), Title);
        Assert.Equal(ZipInstallKind.Upgrade, newer.Kind);
    }

    [Fact]
    public void AFolderInAZipWithANameTooLongIsRenamedOnTheWayIn()
    {
        var library = Library();
        string staging = Path.Combine(_root, "staging-long");
        string inner = Path.Combine(staging, GuidFolder + "-extra");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "patch.txt"), Patch("Long Mod"));
        string zip = Path.Combine(_root, "long.zip");
        ZipFile.CreateFromDirectory(staging, zip);

        using var candidate = library.OpenZip(zip, Title);
        Assert.True(ModFolderName.Fits(candidate.Mod.DirName));
        Assert.Equal(GuidFolder[..31], candidate.Mod.DirName);
    }

    // ================================================================ a folder already too long

    [Fact]
    public async Task AModWithATooLongFolderIsFlaggedAndItsUploadRefusedBeforeAnythingIsWritten()
    {
        var library = Library();
        var mod = MakeMod(UserRoot, GuidFolder, "Last Ground Display");
        Assert.True(library.Scan(Title).Single().FolderNameTooLong);

        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.ClearRequestLog();
            var refused = await Assert.ThrowsAsync<ModFolderNameException>(() => library.EnsureUploadedAsync(client, Title, mod, 0));
            Assert.Contains("31 bytes", refused.Message);
            Assert.Contains("Rename folder", refused.Message);
            Assert.DoesNotContain(server.RequestLog(), op => op is Opcode.DirCreate or Opcode.FileOpen or Opcode.FileWrite or Opcode.ModRescan);
            Assert.DoesNotContain(server.Files.Keys, k => k.Contains(GuidFolder[..31], StringComparison.Ordinal));
        }
    }

    [Fact]
    public void RenameGivesTheFolderAUsableUniqueName()
    {
        var library = Library();
        var mod = MakeMod(UserRoot, GuidFolder, "Last Ground Display");
        MakeMod(UserRoot, "last-ground-display", "Another copy");

        string name = library.SuggestFolderName(Title, mod);
        Assert.Equal("last-ground-display-2", name);

        var renamed = library.RenameFolder(Title, mod, name);
        Assert.Equal(name, renamed.DirName);
        Assert.False(renamed.FolderNameTooLong);
        Assert.False(Directory.Exists(mod.Directory));
        Assert.Contains(library.Scan(Title), m => m.DirName == name && m.Name == "Last Ground Display");

        // Not onto a name that is taken, not to one qwark could not use, and never a shipped mod.
        Assert.Throws<IOException>(() => library.RenameFolder(Title, renamed, "last-ground-display"));
        Assert.Throws<IOException>(() => library.RenameFolder(Title, renamed, GuidFolder + "x"));
        var shipped = MakeMod(ShippedRoot, GuidFolder, "Shipped long one");
        Assert.Throws<IOException>(() => library.RenameFolder(Title, shipped, "fine"));
    }

    [Fact]
    public void DeletingAUserFolderRefusesAnythingButAModFolderOfTheUsersLibrary()
    {
        var library = Library();
        var outside = new LocalMod { Directory = _root, DirName = "x", Name = "Not a mod folder" };
        Assert.Throws<IOException>(() => library.DeleteUserFolder(Title, outside));
        Assert.True(Directory.Exists(_root));

        var shipped = MakeMod(ShippedRoot, "flight", "Flight");
        Assert.Throws<IOException>(() => library.DeleteUserFolder(Title, shipped));
        Assert.True(Directory.Exists(shipped.Directory));
    }
}
