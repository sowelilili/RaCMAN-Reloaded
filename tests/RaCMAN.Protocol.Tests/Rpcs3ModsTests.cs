using System.Diagnostics;
using System.Text;
using RaCMAN.App;
using RaCMAN.Protocol.Testing;
using YamlDotNet.RepresentationModel;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Mods as RPCS3 patches (qwark build 49, protocol revision 1.16): MOD_PATCH and the new MOD_LIST
/// flag, the patch file holding the savefile helper and any number of mods side by side, switching
/// one entry on and off in patch_config.yml, dependencies, overlaps, and what the Mods panel says
/// about each mod. Every RPCS3 folder here is a temporary one and every machine a made-up
/// <see cref="Rpcs3Environment"/>, so nothing ever looks at the RPCS3 on the PC the tests run on.
/// </summary>
public class Rpcs3ModsTests : IDisposable
{
    private const string Title = "NPEA00385";

    private const string Hash = "PPU-ec77eaf73a4f55d1c4ece532c3be6db0011e49ca";

    private const string OtherHash = "PPU-0123456789abcdef0123456789abcdef01234567";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "racman-rpcs3mods-" + Guid.NewGuid().ToString("N"));

    public Rpcs3ModsTests() => Directory.CreateDirectory(_root);

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

    private static Rpcs3Environment Windows() => new(true, false, _ => null, @"C:\Users\nobody", Array.Empty<string>);

    private string MakeRpcs3()
    {
        string folder = Path.Combine(_root, "rpcs3-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(folder, "config"));
        Directory.CreateDirectory(Path.Combine(folder, "patches"));
        Directory.CreateDirectory(Path.Combine(folder, "log"));
        File.WriteAllText(Path.Combine(folder, "rpcs3.exe"), "not really a program");
        return folder;
    }

    private static string BootLog(string hash = Hash, params string[] applied)
    {
        var text = new StringBuilder();
        text.Append("RPCS3 v0.0.42-19958-54014a7d Alpha | master\n");
        text.Append("·! 0:00:10.524533 SYS: Serial: ").Append(Title).Append('\n');
        text.Append("·W 0:00:10.803170 ppu_loader: PPU executable hash: ").Append(hash).Append('\n');
        foreach (var description in applied)
        {
            text.Append("·S 0:00:10.900000 PAT: Applied patch (hash='").Append(hash).Append("', description='")
                .Append(description).Append("', author='someone', patch_version='1.0', file_version='1.2') (<- 2)\n");
        }

        return text.ToString();
    }

    private static Rpcs3Folder FolderAt(string root) => Rpcs3Patches.Build(root, Windows(), "test");

    // ================================================================ the wire

    [Fact]
    public void ModPatchIsOpcode0x0066AndCheckingIsBit5()
    {
        Assert.Equal(0x0066, (ushort)Opcode.ModPatch);
        Assert.Equal(0x20, (byte)ModFlags.Checking);

        var row = new ModEntry(3, ModFlags.Checking | ModFlags.Previous, 0xCAFEF00D, "dir", "Name", "1.0", "me");
        var parsed = ModEntry.Parse(row.ToBytes());
        Assert.True(parsed.Checking);
        Assert.True(parsed.Previous);
        Assert.False(parsed.Loaded);
    }

    [Fact]
    public void AModPatchReplyIsLaidOutAsTheSavefileHelpersAndATruncatedOneIsAProtocolError()
    {
        byte[] payload =
        {
            0x00, 0x02, 0x00, 0x00,             // n = 2, pad
            0x0B, 0xAD, 0xF0, 0x0D,             // stamp
            0x00, 0x66, 0x20, 0x00, 0x94, 0x21, 0xFF, 0x80,
            0x00, 0x70, 0x71, 0x9C, 0x4B, 0xF5, 0xAF, 0xC9,
            0x00, 0x01, 0x00, 0x00,             // nb = 1, pad
            0x00, 0x66, 0x20, 0x04, 0xAB, 0x00, 0x00, 0x00,
        };

        var patch = PatchReply.Parse(payload, "MOD_PATCH");
        Assert.Equal(0x0BADF00Du, patch.Stamp);
        Assert.Equal(new[] { new PatchWord(0x00662000, 0x9421FF80), new PatchWord(0x0070719C, 0x4BF5AFC9) }, patch.Words);
        Assert.Equal(new[] { new PatchByte(0x00662004, 0xAB) }, patch.Bytes);

        foreach (int cut in new[] { 0, 7, 20, 24, 27, payload.Length - 1 })
        {
            var refused = Assert.Throws<ProtocolException>(() => PatchReply.Parse(payload.AsSpan(0, cut), "MOD_PATCH"));
            Assert.Contains("MOD_PATCH", refused.Message);
        }
    }

    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync(bool rpcs3 = true)
    {
        var server = new FakeQwarkServer { Emulator = rpcs3, NoCodePatches = rpcs3 };
        server.Start();
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    /// <summary>A mod as the client uploads it: patch.txt, its cave, and qwark.sum.</summary>
    private static void Upload(FakeQwarkServer server, string dir, string patch, params (string Name, byte[] Data)[] bins)
    {
        string folder = $"{ModLibrary.ConsoleRoot}/{Title}/{dir}";
        server.Files[$"{folder}/patch.txt"] = Encoding.UTF8.GetBytes(patch);
        foreach (var (name, data) in bins) server.Files[$"{folder}/{name}"] = data;
        server.Files[$"{folder}/qwark.sum"] = Encoding.ASCII.GetBytes("12345678");
    }

    [Fact]
    public async Task TheFakeHandsOutAnUploadedModsCavesAsWordsThenItsWordsThenTheTailBytes()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            Upload(server, "bot", "#- name: Bot Info\n0x662000: rack.bin\n0x70719c: 0x4bf5afc9\n",
                ("rack.bin", new byte[] { 0x94, 0x21, 0xFF, 0x80, 0xAB, 0xCD }));
            await client.ModRescanAsync();

            var row = (await client.ModListAsync()).Single(m => m.DirName == "bot");
            Assert.Equal("Bot Info", row.Name);
            Assert.Equal(0x12345678u, row.Hash);

            var patch = await client.ModPatchAsync(row.Index);
            Assert.Equal(new byte[] { row.Index, 0, 0, 0 }, server.LastPayload(Opcode.ModPatch));
            Assert.Equal(new[] { new PatchWord(0x00662000, 0x9421FF80), new PatchWord(0x0070719C, 0x4BF5AFC9) }, patch.Words);
            Assert.Equal(new[] { new PatchByte(0x00662004, 0xAB), new PatchByte(0x00662005, 0xCD) }, patch.Bytes);
            Assert.Equal(PatchReply.StampFor(patch.Words, patch.Bytes), patch.Stamp);
            Assert.Equal(Crc32.Compute(patch.ToBytes().AsSpan(PatchReply.HeaderSize)), patch.Stamp);
        }
    }

    [Fact]
    public async Task ModPatchRefusesWhatQwarkRefuses()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            async Task<Status> Refusal(byte index)
            {
                var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.ModPatchAsync(index));
                return refused.Status;
            }

            // An index MOD_LIST does not have, as MOD_LOAD answers a folder it does not have.
            Assert.Equal(Status.NotFound, await Refusal(200));

            // "flight" is a Lua mod.
            Assert.Equal(Status.Unsupported, await Refusal(1));

            Upload(server, "broken", "#- name: Broken\n0x662000: missing.bin\n");
            await client.ModRescanAsync();
            byte broken = (await client.ModListAsync()).Single(m => m.DirName == "broken").Index;
            Assert.Equal(Status.IoError, await Refusal(broken));

            server.ModPatchStatuses["crash-patch"] = Status.Full;
            Assert.Equal(Status.Full, await Refusal(0));

            // It only reads the console's copy, so the XMB answers too; a boot is BUSY, as for everything.
            server.ModPatchStatuses.Clear();
            server.Session = server.Session with { State = SessionState.Xmb };
            Assert.NotEmpty((await client.ModPatchAsync(0)).Words);

            server.Booting = true;
            Assert.Equal(Status.Busy, await Refusal(0));
        }
    }

    [Fact]
    public async Task UnderRpcs3ModListCarriesLoadedAndCheckingFromTheGameAndOnAConsoleItDoesNot()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.SetModPresence("crash-patch", loaded: true, checking: false);
            server.SetModPresence("flight", loaded: false, checking: true);

            var rows = await client.ModListAsync();
            Assert.True(rows[0].Loaded);
            Assert.False(rows[0].Checking);
            Assert.True(rows[1].Checking);
            Assert.False(rows[1].Loaded);

            server.NoCodePatches = false;
            rows = await client.ModListAsync();
            Assert.False(rows[0].Loaded);
            Assert.False(rows[1].Checking);
        }
    }

    [Fact]
    public async Task AHeadlessRunStartsWithTheLibraryOnTheFakeAndItsScriptSaysWhatIsInTheGame()
    {
        AddToLibrary("lock_rng", "#- name: Lock RNG\n0x5C8318: rand.bin\n", ("rand.bin", new byte[] { 1, 2, 3, 4 }));
        var library = new ModLibrary(Path.Combine(_root, "library")).Scan(Title);

        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.PreloadMods(library);
            var row = (await client.ModListAsync()).Single(m => m.DirName == "lock_rng");
            Assert.Equal(library[0].Hash, row.Hash);
            Assert.Equal(new[] { new PatchWord(0x005C8318, 0x01020304) }, (await client.ModPatchAsync(row.Index)).Words);

            FakeScript.Apply(server, "modscheck");
            Assert.All(await client.ModListAsync(), m => Assert.True(m.Checking));

            FakeScript.Apply(server, "modsin:lock_rng");
            var rows = await client.ModListAsync();
            Assert.True(rows.Single(m => m.DirName == "lock_rng") is { Loaded: true, Checking: false });
            Assert.True(rows.Single(m => m.DirName == "crash-patch") is { Loaded: false, Checking: true });

            FakeScript.Apply(server, "modsout");
            Assert.All(await client.ModListAsync(), m => Assert.False(m.Loaded || m.Checking));
        }
    }

    // ================================================================ the patch file

    private static readonly PatchWord[] HelperWords = { new(0x000F0000, 0x9421FFF0), new(0x0004A2C8, 0x480A5D39) };

    private static readonly PatchByte[] HelperBytes = { new(0x010CD71D, 0x00) };

    private static PatchFileEntry Helper(string hash = Hash, PatchWord[]? words = null) =>
        new(hash, "RaC1", words ?? HelperWords, HelperBytes, Rpcs3Patches.NotesFor(47, 0x1A2B3C4D));

    private static PatchFileEntry LockRng(string hash = Hash, uint libraryHash = 0x11111111) =>
        new(hash, "RaC1", new[] { new PatchWord(0x005C8318, 0x806D9000), new PatchWord(0x005C831C, 0x4E800020) },
            Array.Empty<PatchByte>(), Rpcs3Patches.NotesForMod(libraryHash, 49, 0x22222222),
            Rpcs3Patches.ModDescription("Lock RNG", "lock_rng"), Rpcs3Patches.ModAuthorFallback);

    private static PatchFileEntry BotInfo(string hash = Hash) =>
        new(hash, "RaC1", new[] { new PatchWord(0x00662000, 0x9421FF80), new PatchWord(0x0070719C, 0x4BF5AFC9) },
            new[] { new PatchByte(0x00662004, 0xAB) }, Rpcs3Patches.NotesForMod(0x33333333, 49, 0x44444444),
            Rpcs3Patches.ModDescription("Bot Info", "bot-display"), "robo");

    private const string HelperBlock =
        "  \"qwark savefile helper\":\n"
        + "    Games:\n"
        + "      \"RaC1\":\n"
        + "        NPEA00385: [ All ]\n"
        + "    Author: \"qwark\"\n"
        + "    Notes: \"qwark build 47, helper stamp 0x1a2b3c4d\"\n"
        + "    Patch Version: 1.0\n"
        + "    Patch:\n"
        + "      - [ be32, 0x000f0000, 0x9421fff0 ]\n"
        + "      - [ be32, 0x0004a2c8, 0x480a5d39 ]\n"
        + "      - [ byte, 0x010cd71d, 0x00 ]\n";

    private const string LockRngBlock =
        "  \"RaCMAN mod: Lock RNG [lock_rng]\":\n"
        + "    Games:\n"
        + "      \"RaC1\":\n"
        + "        NPEA00385: [ All ]\n"
        + "    Author: \"RaCMAN Reloaded\"\n"
        + "    Notes: \"library hash 11111111, qwark build 49, patch stamp 0x22222222\"\n"
        + "    Patch Version: 1.0\n"
        + "    Patch:\n"
        + "      - [ be32, 0x005c8318, 0x806d9000 ]\n"
        + "      - [ be32, 0x005c831c, 0x4e800020 ]\n";

    private const string BotInfoBlock =
        "  \"RaCMAN mod: Bot Info [bot-display]\":\n"
        + "    Games:\n"
        + "      \"RaC1\":\n"
        + "        NPEA00385: [ All ]\n"
        + "    Author: \"robo\"\n"
        + "    Notes: \"library hash 33333333, qwark build 49, patch stamp 0x44444444\"\n"
        + "    Patch Version: 1.0\n"
        + "    Patch:\n"
        + "      - [ be32, 0x00662000, 0x9421ff80 ]\n"
        + "      - [ be32, 0x0070719c, 0x4bf5afc9 ]\n"
        + "      - [ byte, 0x00662004, 0xab ]\n";

    private const string FileHead =
        "# RaCMAN Reloaded writes this file: qwark's savefile helper and the mods enabled in RaCMAN. Edits made here are lost.\n"
        + "Version: 1.2\n";

    [Fact]
    public void TheHelperAndTwoModsShareOneHashInTheFile()
    {
        string text = Rpcs3Patches.BuildPatchFile(Title, new[] { Helper(), LockRng(), BotInfo() });

        Assert.Equal(FileHead + "\n" + Hash + ":\n" + HelperBlock + LockRngBlock + BotInfoBlock, text);
    }

    [Fact]
    public void EveryHashIsOneKeyWhereverItsEntriesAre()
    {
        string text = Rpcs3Patches.BuildPatchFile(Title, new[] { Helper(), LockRng(OtherHash), BotInfo() });

        Assert.Equal(FileHead + "\n" + Hash + ":\n" + HelperBlock + BotInfoBlock
                     + "\n" + OtherHash + ":\n" + LockRngBlock, text);

        // RPCS3 rejects a file with a key twice; YamlDotNet does too.
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        Assert.Equal(2, ((YamlMappingNode)root[Hash]).Children.Count);
        Assert.Single(((YamlMappingNode)root[OtherHash]).Children);
    }

    [Fact]
    public void EveryEntryReadsBackWithEverythingItWasWrittenWith()
    {
        var written = new[] { Helper(), LockRng(), BotInfo(), LockRng(OtherHash) };
        var state = Rpcs3Patches.ParsePatchFile(Rpcs3Patches.BuildPatchFile(Title, written), "x");

        Assert.Equal(PatchFileKind.Ours, state.Kind);
        Assert.Equal(4, state.Entries.Count);
        for (int i = 0; i < written.Length; i++)
        {
            var back = state.Entries[i];
            Assert.Equal(written[i].Hash, back.Hash);
            Assert.Equal(written[i].Description, back.Description);
            Assert.Equal(written[i].Game, back.Game);
            Assert.Equal(written[i].Author, back.Author);
            Assert.Equal(written[i].PatchVersion, back.PatchVersion);
            Assert.Equal(written[i].Notes, back.Notes);
            Assert.Equal(written[i].Words, back.Words);
            Assert.Equal(written[i].Bytes, back.Bytes);
        }

        Assert.Equal("bot-display", state.ModEntryFor(Hash, "BOT-DISPLAY")!.ModDir);
        Assert.Equal(Rpcs3Patches.Description, state.EntryFor(Hash)!.Description);
        Assert.Null(state.ModEntryFor(OtherHash, "bot-display"));

        // The same text again: nothing about an entry is lost on the way through.
        Assert.Equal(Rpcs3Patches.BuildPatchFile(Title, written), Rpcs3Patches.BuildPatchFile(Title, state.Entries));
    }

    [Fact]
    public void AFileFromTheBuildThatOnlyWroteTheHelperIsStillOurs()
    {
        string old = "# RaCMAN Reloaded writes this file and rewrites it whenever qwark's savefile helper changes. Edits made here are lost.\n"
                     + "Version: 1.2\n\n" + Hash + ":\n" + HelperBlock;

        var state = Rpcs3Patches.ParsePatchFile(old, "x");
        Assert.Equal(PatchFileKind.Ours, state.Kind);
        Assert.Equal(HelperWords, Assert.Single(state.Entries).Words);
    }

    [Fact]
    public void AnEntryUnderSomeoneElsesDescriptionIsNotRaCMANs()
    {
        string text = FileHead + "\n" + Hash + ":\n" + HelperBlock
                      + "  \"Somebody's cheat\":\n    Games:\n      \"RaC1\":\n        NPEA00385: [ All ]\n"
                      + "    Patch:\n      - [ be16, 0x00100000, 0x6000 ]\n";

        var state = Rpcs3Patches.ParsePatchFile(text, "x");
        Assert.Equal(PatchFileKind.Ours, state.Kind);
        Assert.Equal(Rpcs3Patches.Description, Assert.Single(state.Entries).Description);
    }

    [Fact]
    public void ModDescriptionsNameTheFolderAndReadBack()
    {
        string description = Rpcs3Patches.ModDescription("Bot Info", "bot-display");
        Assert.Equal("RaCMAN mod: Bot Info [bot-display]", description);
        Assert.Equal("bot-display", Rpcs3Patches.ModDirOf(description));
        Assert.Equal("Bot Info", Rpcs3Patches.NameOf(description));

        // A name with brackets of its own: the folder is the last pair.
        string odd = Rpcs3Patches.ModDescription("Timer [beta]", "timer");
        Assert.Equal("timer", Rpcs3Patches.ModDirOf(odd));
        Assert.Equal("Timer [beta]", Rpcs3Patches.NameOf(odd));

        Assert.Null(Rpcs3Patches.ModDirOf(Rpcs3Patches.Description));
        Assert.Null(Rpcs3Patches.ModDirOf("Infinite Ammo"));
        Assert.Equal("qwark's savefile helper", Rpcs3Patches.NameOf(Rpcs3Patches.Description));

        // A mod with no name is called by its folder, as qwark calls it.
        Assert.Equal("RaCMAN mod: dir [dir]", Rpcs3Patches.ModDescription("  ", "dir"));
    }

    [Fact]
    public void AModsNotesCarryItsLibraryHash()
    {
        string notes = Rpcs3Patches.NotesForMod(0x0BADF00D, 49, 0xDEADBEEF);

        Assert.Equal("library hash 0badf00d, qwark build 49, patch stamp 0xdeadbeef", notes);
        Assert.Equal(0x0BADF00Du, Rpcs3Patches.LibraryHashIn(notes));
        Assert.Null(Rpcs3Patches.LibraryHashIn(Rpcs3Patches.NotesFor(47, 1)));
        Assert.Null(Rpcs3Patches.LibraryHashIn("library hash 12"));
    }

    [Fact]
    public void WritingAModReplacesItsOwnEntryAndKeepsEveryOtherForEveryHash()
    {
        var existing = new[] { Helper(), LockRng(), BotInfo(), LockRng(OtherHash), Helper(OtherHash) };
        var newer = LockRng(libraryHash: 0x55555555);

        var merged = Rpcs3Patches.MergeEntries(existing, newer);

        Assert.Equal(5, merged.Count);
        Assert.Same(existing[0], merged[0]);
        Assert.Same(newer, merged[1]);
        Assert.Same(existing[2], merged[2]);

        // The same mod for another executable is that executable's, and the helper there is too.
        Assert.Same(existing[3], merged[3]);
        Assert.Same(existing[4], merged[4]);
    }

    [Fact]
    public void AModRenamedSinceItWasWrittenIsStillOneEntry()
    {
        var renamed = LockRng() with { Description = Rpcs3Patches.ModDescription("RNG Lock", "lock_rng") };

        var merged = Rpcs3Patches.MergeEntries(new[] { Helper(), LockRng() }, renamed);

        Assert.Equal(new[] { Rpcs3Patches.Description, renamed.Description }, merged.Select(e => e.Description));
    }

    [Fact]
    public void TheHelperStillSpreadsToTheGamesOtherExecutablesButNeverOverAMod()
    {
        var newWords = new[] { new PatchWord(0x000F0000, 0x60000000) };
        var merged = Rpcs3Patches.MergeEntries(new[] { Helper(OtherHash), LockRng(OtherHash) }, Helper(words: newWords));

        Assert.Equal(new[] { OtherHash, OtherHash, Hash }, merged.Select(e => e.Hash));
        Assert.Equal(newWords, merged[0].Words);
        Assert.Equal(LockRng(OtherHash).Description, merged[1].Description);
        Assert.Equal(LockRng(OtherHash).Words, merged[1].Words);
    }

    // ================================================================ patch_config.yml

    /// <summary>patch_config.yml with somebody's own switches, the helper on and two mods, one on and one off.</summary>
    private const string Config =
        "PPU-c14042df6304d3e420a9917e6f8e5fc05cc38b4c:\n"
        + "  Infinite Ammo:\n"
        + "    \"Ratchet & Clank Future: Tools of Destruction\":\n"
        + "      BCUS98127:\n"
        + "        All:\n"
        + "          Enabled: true\n"
        + "PPU-ec77eaf73a4f55d1c4ece532c3be6db0011e49ca:\n"
        + "  qwark savefile helper:\n"
        + "    RaC1:\n"
        + "      NPEA00385:\n"
        + "        All:\n"
        + "          Enabled: true\n"
        + "  'RaCMAN mod: Lock RNG [lock_rng]':\n"
        + "    RaC1:\n"
        + "      NPEA00385:\n"
        + "        All:\n"
        + "          Enabled: true\n";

    private static PatchConfigKey Key(PatchFileEntry entry) => entry.Key(Title);

    [Fact]
    public void SwitchingOneModOnTouchesNothingElse()
    {
        string on = Rpcs3Patches.EnablePatches(Config, new[] { Key(BotInfo()) }, "x");

        Assert.Equal(Config
                     + "  'RaCMAN mod: Bot Info [bot-display]':\n"
                     + "    RaC1:\n"
                     + "      NPEA00385:\n"
                     + "        All:\n"
                     + "          Enabled: true\n", on);
        Assert.True(Rpcs3Patches.IsEnabled(on, Key(BotInfo()), "x"));
        Assert.True(Rpcs3Patches.IsEnabled(on, Key(LockRng()), "x"));
        Assert.True(Rpcs3Patches.IsEnabled(on, Key(Helper()), "x"));
    }

    [Fact]
    public void SwitchingOneModOffRemovesItsSwitchTheWayRpcs3WritesOneAndTouchesNothingElse()
    {
        string off = Rpcs3Patches.DisablePatches(Config, new[] { Key(LockRng()) }, "x");

        // RPCS3's save_config writes nothing for a patch that is off and has no values of its own,
        // so the whole branch down to it goes; the hash stays for the helper under it.
        Assert.Equal(Config.Replace(
            "  'RaCMAN mod: Lock RNG [lock_rng]':\n    RaC1:\n      NPEA00385:\n        All:\n          Enabled: true\n",
            string.Empty, StringComparison.Ordinal), off);
        Assert.False(Rpcs3Patches.IsEnabled(off, Key(LockRng()), "x"));
        Assert.True(Rpcs3Patches.IsEnabled(off, Key(Helper()), "x"));
    }

    [Fact]
    public void TheLastSwitchUnderAHashTakesTheHashWithIt()
    {
        string onlyMod = Rpcs3Patches.EnablePatches(null, new[] { Key(LockRng()) }, "x");
        Assert.Equal("{}\n", Rpcs3Patches.DisablePatches(onlyMod, new[] { Key(LockRng()) }, "x"));

        string users = "PPU-c14042df6304d3e420a9917e6f8e5fc05cc38b4c:\n  Infinite Ammo:\n    Game:\n      BCUS98127:\n        All:\n          Enabled: true\n";
        Assert.Equal(users, Rpcs3Patches.DisablePatches(users + onlyMod, new[] { Key(LockRng()) }, "x"));
    }

    [Fact]
    public void AnAppVersionWithValuesOfItsOwnKeepsThemWhenItIsSwitchedOff()
    {
        string withValues = "PPU-ec77eaf73a4f55d1c4ece532c3be6db0011e49ca:\n"
                            + "  'RaCMAN mod: Lock RNG [lock_rng]':\n"
                            + "    RaC1:\n"
                            + "      NPEA00385:\n"
                            + "        All:\n"
                            + "          Enabled: true\n"
                            + "          Configurable Values:\n"
                            + "            Seed: 7\n";

        string off = Rpcs3Patches.DisablePatches(withValues, new[] { Key(LockRng()) }, "x");

        Assert.Equal(withValues.Replace("          Enabled: true\n", string.Empty, StringComparison.Ordinal), off);
    }

    [Fact]
    public void SwitchingOffWhatIsNotThereChangesNothing()
    {
        Assert.Equal(Config, Rpcs3Patches.DisablePatches(Config, new[] { Key(BotInfo()) }, "x"));
    }

    // ================================================================ the writes, both ways

    private Rpcs3Folder Folder(string? patchFile = null, string? config = null, string log = "")
    {
        string root = MakeRpcs3();
        var folder = FolderAt(root);
        if (patchFile is not null) File.WriteAllText(folder.PatchFile(Title), patchFile);
        if (config is not null) File.WriteAllText(folder.PatchConfigFile, config);
        File.WriteAllText(Path.Combine(root, "log", Rpcs3Patches.LogName), log.Length > 0 ? log : BootLog());
        return folder;
    }

    [Fact]
    public void InstallingTheHelperKeepsEveryModEntryAndEveryModSwitch()
    {
        // Lock RNG on, Bot Info in the file and off, and another executable's Lock RNG.
        var folder = Folder(Rpcs3Patches.BuildPatchFile(Title, new[] { Helper(), LockRng(), BotInfo(), LockRng(OtherHash) }),
            Config.Replace("  qwark savefile helper:\n    RaC1:\n      NPEA00385:\n        All:\n          Enabled: true\n",
                string.Empty, StringComparison.Ordinal));

        var newWords = new[] { new PatchWord(0x000F0000, 0x60000000) };
        Rpcs3Patches.Write(Rpcs3Patches.Plan(folder, Title, "RaC1", Hash, new PatchReply(0x77777777, newWords, HelperBytes), 49),
            new HashSet<string>());

        string text = File.ReadAllText(folder.PatchFile(Title));
        Assert.Contains(LockRngBlock, text);
        Assert.Contains(BotInfoBlock, text);
        Assert.Contains("\n" + OtherHash + ":\n" + LockRngBlock, text);
        Assert.Equal(newWords, Rpcs3Patches.ReadPatchFile(folder.PatchFile(Title)).EntryFor(Hash)!.Words);

        string config = File.ReadAllText(folder.PatchConfigFile);
        Assert.True(Rpcs3Patches.IsEnabled(config, Key(Helper()), "x"));
        Assert.True(Rpcs3Patches.IsEnabled(config, Key(LockRng()), "x"));
        Assert.False(Rpcs3Patches.IsEnabled(config, Key(BotInfo()), "x"));
        Assert.False(Rpcs3Patches.IsEnabled(config, Key(LockRng(OtherHash)), "x"));
    }

    [Fact]
    public void WritingAModKeepsTheHelperAndItsSwitchWhateverItIs()
    {
        // The helper is in the file and switched off: it stays exactly so.
        var folder = Folder(Rpcs3Patches.BuildPatchFile(Title, new[] { Helper(), Helper(OtherHash) }), config: null);

        Rpcs3Patches.Write(Rpcs3Patches.PlanMods(folder, Title, Hash, new[] { LockRng() }), new HashSet<string>());

        string text = File.ReadAllText(folder.PatchFile(Title));
        Assert.Equal(FileHead + "\n" + Hash + ":\n" + HelperBlock + LockRngBlock + "\n" + OtherHash + ":\n" + HelperBlock, text);

        string config = File.ReadAllText(folder.PatchConfigFile);
        Assert.True(Rpcs3Patches.IsEnabled(config, Key(LockRng()), "x"));
        Assert.False(Rpcs3Patches.IsEnabled(config, Key(Helper()), "x"));
        Assert.False(Rpcs3Patches.IsEnabled(config, Key(Helper(OtherHash)), "x"));
    }

    [Fact]
    public void SwitchingAModOffLeavesThePatchFileAndEveryOtherSwitchAlone()
    {
        string file = Rpcs3Patches.BuildPatchFile(Title, new[] { Helper(), LockRng() });
        var folder = Folder(file, Config);
        var backedUp = new HashSet<string>();

        var plan = Rpcs3Patches.PlanDisable(folder, Title, Hash, new[] { "lock_rng" });
        Assert.NotNull(plan);
        Assert.Null(plan!.PatchText);
        Rpcs3Patches.Write(plan, backedUp);

        Assert.Equal(file, File.ReadAllText(folder.PatchFile(Title)));
        string config = File.ReadAllText(folder.PatchConfigFile);
        Assert.False(Rpcs3Patches.IsEnabled(config, Key(LockRng()), "x"));
        Assert.True(Rpcs3Patches.IsEnabled(config, Key(Helper()), "x"));
        Assert.StartsWith(Config[..Config.IndexOf("  'RaCMAN mod", StringComparison.Ordinal)], config);
        Assert.Equal(Config, File.ReadAllText(folder.PatchConfigFile + Rpcs3Patches.BackupSuffix));

        // Nothing to switch off is nothing written.
        Assert.Null(Rpcs3Patches.PlanDisable(folder, Title, Hash, new[] { "bot-display" }));
    }

    [Fact]
    public void ARenamedModsOldSwitchGoesWhenItsNewEntryIsWritten()
    {
        var folder = Folder(Rpcs3Patches.BuildPatchFile(Title, new[] { Helper(), LockRng() }), Config);
        var renamed = LockRng() with { Description = Rpcs3Patches.ModDescription("RNG Lock", "lock_rng") };

        Rpcs3Patches.Write(Rpcs3Patches.PlanMods(folder, Title, Hash, new[] { renamed }), new HashSet<string>());

        string config = File.ReadAllText(folder.PatchConfigFile);
        Assert.False(Rpcs3Patches.IsEnabled(config, Key(LockRng()), "x"));
        Assert.True(Rpcs3Patches.IsEnabled(config, Key(renamed), "x"));
        Assert.DoesNotContain("Lock RNG", File.ReadAllText(folder.PatchFile(Title)));
    }

    [Fact]
    public void AModIsNeverWrittenIntoSomebodyElsesFileOrWithNoWords()
    {
        const string theirs = "Version: 1.2\n\nPPU-aaaa:\n  \"Their cheat\":\n    Patch: []\n";
        var folder = Folder(theirs, Config);

        var foreign = Assert.Throws<Rpcs3PatchException>(() => Rpcs3Patches.PlanMods(folder, Title, Hash, new[] { LockRng() }));
        Assert.Contains("was not written by RaCMAN", foreign.Message);
        Assert.Throws<Rpcs3PatchException>(() => Rpcs3Patches.PlanDisable(folder, Title, Hash, new[] { "lock_rng" }));
        Assert.Equal(theirs, File.ReadAllText(folder.PatchFile(Title)));

        var empty = LockRng() with { Words = Array.Empty<PatchWord>() };
        var mine = Folder();
        Assert.Throws<Rpcs3PatchException>(() => Rpcs3Patches.PlanMods(mine, Title, Hash, new[] { empty }));
        Assert.Throws<Rpcs3PatchException>(() => Rpcs3Patches.PlanMods(mine, Title, Hash, new[] { Helper() }));
        Assert.Throws<Rpcs3PatchException>(() => Rpcs3Patches.PlanMods(mine, Title, Hash, new[] { LockRng(OtherHash) }));
    }

    [Fact]
    public async Task TheWriterWorksOutEachPlanFromTheFilesAsTheyAreThen()
    {
        var folder = Folder();
        var writer = new Rpcs3PatchWriter();

        // Two writes started from the same empty folder: the second one plans after the first wrote.
        await Task.WhenAll(
            Task.Run(() => writer.Commit(() => Rpcs3Patches.PlanMods(folder, Title, Hash, new[] { LockRng() }))),
            Task.Run(() => writer.Commit(() => Rpcs3Patches.Plan(folder, Title, "RaC1", Hash,
                new PatchReply(1, HelperWords, HelperBytes), 47))));

        var file = Rpcs3Patches.ReadPatchFile(folder.PatchFile(Title));
        Assert.NotNull(file.EntryFor(Hash));
        Assert.NotNull(file.ModEntryFor(Hash, "lock_rng"));
        string config = File.ReadAllText(folder.PatchConfigFile);
        Assert.True(Rpcs3Patches.IsEnabled(config, Key(Helper()), "x"));
        Assert.True(Rpcs3Patches.IsEnabled(config, Key(LockRng()), "x"));
        Assert.Null(writer.Commit(() => null));
    }

    // ================================================================ overlaps

    private static IReadOnlyList<PatchRange> Ranges(PatchWord[] words, PatchByte[]? bytes = null) =>
        PatchRanges.Of(words, bytes ?? Array.Empty<PatchByte>());

    /// <summary>A cave of <paramref name="length"/> bytes at <paramref name="address"/>, as MOD_PATCH hands one out.</summary>
    private static (PatchWord[] Words, PatchByte[] Bytes) Cave(uint address, int length)
    {
        var words = Enumerable.Range(0, length / 4).Select(i => new PatchWord(address + (uint)(i * 4), 0x60000000)).ToArray();
        var bytes = Enumerable.Range(length / 4 * 4, length % 4).Select(i => new PatchByte(address + (uint)i, 0)).ToArray();
        return (words, bytes);
    }

    [Fact]
    public void ACaveIsOneStretchAndTouchingStretchesAreJoined()
    {
        var (words, bytes) = Cave(0x00662000, 10);
        var ranges = Ranges(words.Append(new PatchWord(0x0070719C, 1)).ToArray(), bytes);

        Assert.Equal(new[] { new PatchRange(0x00662000, 0x0066200A), new PatchRange(0x0070719C, 0x007071A0) }, ranges);
    }

    [Fact]
    public void TwoCavesOverTheSameAddressesOverlap()
    {
        // NPEA00423's Bot Info and IL HUD Display both put a cave at 0x662000 (996 and 920 bytes).
        var bot = Cave(0x00662000, 996);
        var il = Cave(0x00662000, 920);

        Assert.Equal(0x00662000u, PatchRanges.FirstShared(Ranges(bot.Words, bot.Bytes), Ranges(il.Words, il.Bytes)));
    }

    [Fact]
    public void AWordInsideAnotherModsCaveOverlaps()
    {
        var cave = Cave(0x00662000, 64);
        var word = new[] { new PatchWord(0x00662010, 0x4E800020) };

        Assert.Equal(0x00662010u, PatchRanges.FirstShared(Ranges(cave.Words, cave.Bytes), Ranges(word)));
    }

    [Fact]
    public void ATailByteCountsAndTheByteAfterItDoesNot()
    {
        var cave = Cave(0x00662000, 6);                  // words up to 0x662003, bytes 0x662004 and 0x662005
        var onTail = new[] { new PatchWord(0x00662004, 1) };
        var after = new[] { new PatchWord(0x00662006, 1) };

        Assert.Equal(0x00662004u, PatchRanges.FirstShared(Ranges(cave.Words, cave.Bytes), Ranges(onTail)));
        Assert.Null(PatchRanges.FirstShared(Ranges(cave.Words, cave.Bytes), Ranges(after)));
        Assert.Null(PatchRanges.FirstShared(Ranges(HelperWords), Ranges(after)));
    }

    [Fact]
    public void AModOverTheHelperIsRefusedAndSaysSo()
    {
        var folder = Folder(Rpcs3Patches.BuildPatchFile(Title, new[] { Helper() }), config: null);
        var clash = LockRng() with { Words = new[] { new PatchWord(0x000F0000, 0x60000000) } };

        var refused = Assert.Throws<Rpcs3PatchException>(() => Rpcs3Patches.PlanMods(folder, Title, Hash, new[] { clash }));

        Assert.Contains("Lock RNG cannot be enabled", refused.Message);
        Assert.Contains("qwark's savefile helper", refused.Message);
        Assert.Contains("0x000f0000", refused.Message);
    }

    [Fact]
    public void AModOverAnEnabledModIsRefusedNamingItAndOverADisabledOneIsNot()
    {
        var bot = Cave(0x00662000, 996);
        var il = Cave(0x00662000, 920);
        var botEntry = BotInfo() with { Words = bot.Words.Append(new PatchWord(0x0070719C, 0x4BF5AFC9)).ToArray(), Bytes = bot.Bytes };
        var ilEntry = new PatchFileEntry(Hash, "Deadlocked", il.Words.Append(new PatchWord(0x0070719C, 0x4BF5B0CD)).ToArray(), il.Bytes,
            Rpcs3Patches.NotesForMod(1, 49, 2), Rpcs3Patches.ModDescription("IL HUD Display", "il-display"), "robo");

        var folder = Folder(Rpcs3Patches.BuildPatchFile(Title, new[] { ilEntry }), config: null);

        // IL HUD Display is in the file but switched off: nothing is in the way.
        Rpcs3Patches.Write(Rpcs3Patches.PlanMods(folder, Title, Hash, new[] { botEntry }), new HashSet<string>());

        // Now Bot Info is on, and IL HUD Display is refused over it.
        var refused = Assert.Throws<Rpcs3PatchException>(() => Rpcs3Patches.PlanMods(folder, Title, Hash, new[] { ilEntry }));
        Assert.Contains("IL HUD Display cannot be enabled", refused.Message);
        Assert.Contains("Bot Info", refused.Message);
        Assert.Contains("Disable Bot Info first", refused.Message);

        // And two new ones over each other in one write are refused too.
        var both = Folder();
        var together = Assert.Throws<Rpcs3PatchException>(() => Rpcs3Patches.PlanMods(both, Title, Hash, new[] { botEntry, ilEntry }));
        Assert.Contains("Bot Info", together.Message);
        Assert.False(File.Exists(both.PatchFile(Title)));
    }

    [Fact]
    public void UpdatingAnEnabledModIsNotInItsOwnWay()
    {
        var folder = Folder(Rpcs3Patches.BuildPatchFile(Title, new[] { LockRng() }), Config);

        Rpcs3Patches.Write(Rpcs3Patches.PlanMods(folder, Title, Hash, new[] { LockRng(libraryHash: 0x99999999) }), new HashSet<string>());

        var entry = Rpcs3Patches.ReadPatchFile(folder.PatchFile(Title)).ModEntryFor(Hash, "lock_rng")!;
        Assert.Equal(0x99999999u, Rpcs3Patches.LibraryHashIn(entry.Notes));
    }

    // ================================================================ the log

    [Fact]
    public void TheLogSaysWhichOfTheExecutablesPatchesRpcs3AppliedAtThisBoot()
    {
        string lockRng = Rpcs3Patches.ModDescription("Lock RNG", "lock_rng");
        string log = BootLog(Hash, Rpcs3Patches.Description, lockRng)
                     + "·S 0:00:10.900000 PAT: Applied patch (hash='" + OtherHash + "', description='Elsewhere', author='x') (<- 1)\n";

        var lookup = Rpcs3Patches.FindExecutableHash(new StringReader(log), Title);

        Assert.True(lookup.PatchApplied);
        Assert.True(lookup.AppliedAtBoot(lockRng));
        Assert.True(lookup.AppliedAtBoot(Rpcs3Patches.Description));
        Assert.False(lookup.AppliedAtBoot("Elsewhere"));
        Assert.False(lookup.AppliedAtBoot(Rpcs3Patches.ModDescription("Bot Info", "bot-display")));

        // A new boot starts again from nothing.
        var again = Rpcs3Patches.FindExecutableHash(new StringReader(log + BootLog()), Title);
        Assert.False(again.AppliedAtBoot(lockRng));
    }

    [Fact]
    public void TheDescriptionRunsUpToTheAuthorEvenWithAQuoteInIt()
    {
        Assert.Equal("RaCMAN mod: Ratchet's timer [t]",
            Rpcs3Patches.AppliedDescription(
                $"PAT: Applied patch (hash='{Hash}', description='RaCMAN mod: Ratchet's timer [t]', author='a', patch_version='1.0')",
                Hash));
        Assert.Null(Rpcs3Patches.AppliedDescription($"PAT: Applied patch (hash='{OtherHash}', description='x', author='a')", Hash));
        Assert.Null(Rpcs3Patches.AppliedDescription("PAT: Loading patch config file", Hash));
    }

    // ================================================================ dependencies

    private static LocalMod Mod(string dir, string name, string? depends = null, uint hash = 0x11111111, bool lua = false,
        int words = 1)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (depends is not null) variables["depends"] = depends;
        return new LocalMod
        {
            Directory = dir, DirName = dir, Name = name, Variables = variables, Hash = hash, NeedsLua = lua, PatchWordCount = words,
        };
    }

    [Fact]
    public void DependenciesComeFirstInTheOrderTheLineListsThem()
    {
        var timer = Mod("il-timer", "IL Decimal Timer");
        var hud = Mod("il-hud", "IL HUD", depends: "IL Decimal Timer");
        var ghost = Mod("il-ghost", "IL Ghost", depends: " IL HUD , IL Decimal Timer,");
        var library = new[] { ghost, hud, timer };

        var (order, problem) = ModDependencies.Resolve(ghost, library);

        Assert.Equal(string.Empty, problem);
        Assert.Equal(new[] { timer, hud, ghost }, order);
        Assert.Equal(new[] { "IL HUD", "IL Decimal Timer" }, ModDependencies.NamesIn(ghost));
    }

    [Fact]
    public void AMissingDependencyOrALoopIsARefusalWithTheReason()
    {
        // NPEA00423's IL RTA names a mod the library does not have.
        var rta = Mod("il-igt", "IL RTA", depends: "IL Decimal Timer");
        var (none, missing) = ModDependencies.Resolve(rta, new[] { rta });
        Assert.Empty(none);
        Assert.Equal("IL RTA needs IL Decimal Timer, which is not in the mod library for this game.", missing);

        // Names are compared exactly, as qwark compares them.
        var (_, cased) = ModDependencies.Resolve(rta, new[] { rta, Mod("t", "il decimal timer") });
        Assert.Contains("not in the mod library", cased);

        var a = Mod("a", "A", depends: "B");
        var b = Mod("b", "B", depends: "A");
        var (_, loop) = ModDependencies.Resolve(a, new[] { a, b });
        Assert.Contains("need each other", loop);
    }

    [Fact]
    public void TheDependantsOfAModAreTheEnabledModsThatNeedItDirectlyOrNot()
    {
        var timer = Mod("il-timer", "IL Decimal Timer");
        var hud = Mod("il-hud", "IL HUD", depends: "IL Decimal Timer");
        var ghost = Mod("il-ghost", "IL Ghost", depends: "IL HUD");
        var rta = Mod("il-igt", "IL RTA", depends: "IL Decimal Timer");
        var library = new[] { timer, hud, ghost, rta };

        var enabled = new HashSet<LocalMod> { timer, hud, ghost };
        Assert.Equal(new[] { hud, ghost }, ModDependencies.Dependants(timer, library, enabled.Contains));
        Assert.Empty(ModDependencies.Dependants(ghost, library, enabled.Contains));
    }

    [Fact]
    public void TheQuestionsNameTheMods()
    {
        var x = Mod("x", "X");
        var y = Mod("y", "Y");
        var z = Mod("z", "Z");

        Assert.Equal("X needs Y. Enable both?", ModDependencies.EnableQuestion(x, new[] { y }));
        Assert.Equal("X needs Y and Z. Enable all 3?", ModDependencies.EnableQuestion(x, new[] { y, z }));
        Assert.Equal("Y needs X. Disable both?", ModDependencies.DisableQuestion(x, new[] { y }));
        Assert.Equal("Y and Z need X. Disable all 3?", ModDependencies.DisableQuestion(x, new[] { y, z }));
    }

    // ================================================================ the status column

    private static readonly LocalMod Library = Mod("lock_rng", "Lock RNG", hash: 0x11111111);

    private static Rpcs3PatchDisk Disk(PatchFileEntry? entry = null, bool enabled = false, string[]? applied = null,
        PatchFileKind kind = PatchFileKind.Ours, string problem = "")
    {
        var folder = Rpcs3Patches.Build(@"C:\rpcs3", Windows(), "test");
        var file = new PatchFileState("x", kind, entry is null ? Array.Empty<PatchFileEntry>() : new[] { entry },
            kind == PatchFileKind.Foreign ? "not written by RaCMAN" : string.Empty);
        return new Rpcs3PatchDisk(Title, new Rpcs3FolderLookup(folder, string.Empty),
            new ExecutableHashLookup(Hash, string.Empty, false, null, applied ?? Array.Empty<string>()),
            file, false, problem, enabled && entry is not null ? new[] { entry.Description } : Array.Empty<string>());
    }

    private static ModEntry Row(ModFlags flags = ModFlags.None) => new(0, flags, 0x11111111, "lock_rng", "Lock RNG", "", "");

    private static ModRpcs3Status Decide(Rpcs3PatchDisk? disk, ModEntry? console = null, LocalMod? mod = null,
        ModPatchRefusal refusal = ModPatchRefusal.None, bool rewritten = false, bool connected = true, bool answering = true) =>
        Rpcs3ModsController.Decide(new ModRpcs3Facts(mod ?? Library, console, disk, refusal, rewritten, connected, answering));

    [Fact]
    public void BeforeTheFolderIsLookedAtThereIsOnlyWaiting()
    {
        var status = Decide(null, Row(ModFlags.Loaded));
        Assert.Equal(ModRpcs3State.Waiting, status.State);
        Assert.False(status.CanToggle);
    }

    [Fact]
    public void AFolderThatCannotBeReadIsUnknownUnlessQwarkFindsTheModInTheGame()
    {
        var noFolder = new Rpcs3PatchDisk(Title, new Rpcs3FolderLookup(null, "RPCS3 is not running"), null, null, false, string.Empty);
        var unknown = Decide(noFolder, Row());
        Assert.Equal(ModRpcs3State.Unknown, unknown.State);
        Assert.Equal("RPCS3 is not running", unknown.ToggleReason);
        Assert.False(unknown.CanToggle);

        Assert.Equal(ModRpcs3State.Loaded, Decide(noFolder, Row(ModFlags.Loaded)).State);

        var foreign = Decide(Disk(kind: PatchFileKind.Foreign), Row());
        Assert.Equal(ModRpcs3State.Unknown, foreign.State);
        Assert.Contains("not written by RaCMAN", foreign.Tooltip);

        Assert.Equal(ModRpcs3State.Unknown, Decide(Disk(problem: "patch_config.yml is not a YAML map"), Row()).State);
    }

    [Fact]
    public void AModWithNoEntryOrASwitchedOffOneIsDisabled()
    {
        var none = Decide(Disk(), Row());
        Assert.Equal(ModRpcs3State.Disabled, none.State);
        Assert.Equal("Disabled", none.Text);
        Assert.Equal(ModRpcs3Tone.Quiet, none.Tone);
        Assert.False(none.Enabled);
        Assert.True(none.CanToggle);

        Assert.Equal(ModRpcs3State.Disabled, Decide(Disk(LockRng(), enabled: false), Row()).State);

        // Not uploaded, or still being looked for, is not in the game either.
        Assert.Equal(ModRpcs3State.Disabled, Decide(Disk(LockRng(), enabled: false), null).State);
        Assert.Equal(ModRpcs3State.Disabled, Decide(Disk(LockRng(), enabled: false), Row(ModFlags.Checking)).State);

        // Another executable's entry is not this one's.
        Assert.Equal(ModRpcs3State.Disabled, Decide(Disk(LockRng(OtherHash), enabled: true), Row()).State);
    }

    [Fact]
    public void ASwitchedOffModQwarkStillFindsInTheGameIsDisabledAndWaitsForTheRestart()
    {
        var stillIn = Decide(Disk(LockRng(), enabled: false), Row(ModFlags.Loaded));
        Assert.Equal(ModRpcs3State.DisabledRestartNeeded, stillIn.State);
        Assert.Equal("Disabled - restart needed", stillIn.Text);
        Assert.Equal(ModRpcs3Tone.Pending, stillIn.Tone);
        Assert.Contains("stays in the game until the game is restarted in RPCS3", stillIn.Tooltip);
        Assert.Contains("when the game boots", stillIn.Tooltip);
        Assert.False(stillIn.Enabled);
        Assert.False(stillIn.CanUpdate);

        // It can be ticked again as any disabled mod can, and for the same reasons not.
        Assert.True(stillIn.CanToggle);
        var offline = Decide(Disk(LockRng(), enabled: false), Row(ModFlags.Loaded), connected: false);
        Assert.Equal(ModRpcs3State.DisabledRestartNeeded, offline.State);
        Assert.False(offline.CanToggle);
        Assert.Contains("Connect", offline.ToggleReason);

        // With no entry left for it at all, it is just as much still in the game.
        Assert.Equal(ModRpcs3State.DisabledRestartNeeded, Decide(Disk(), Row(ModFlags.Loaded)).State);

        // What makes a mod impossible to enable still comes first.
        Assert.Equal(ModRpcs3State.ParseError, Decide(Disk(), Row(ModFlags.Loaded | ModFlags.ParseError)).State);
    }

    [Fact]
    public void EnablingNeedsQwarkRpcs3AndARunningGameAndSaysWhy()
    {
        var offline = Decide(Disk(), null, connected: false);
        Assert.False(offline.CanToggle);
        Assert.Contains("Connect", offline.ToggleReason);

        // qwark-rpcs3 answers MOD_PATCH at the XMB, but not while a game is starting or stopping.
        var booting = Decide(Disk(), Row(), answering: false);
        Assert.False(booting.CanToggle);
        Assert.Contains("starting or stopping", booting.ToggleReason);

        // Switching one off only takes the files.
        Assert.True(Decide(Disk(LockRng(), enabled: true), Row(), connected: false).CanToggle);
    }

    [Fact]
    public void LuaParseErrorsAndSizeMakeAModImpossibleToEnable()
    {
        var lua = Decide(Disk(), null, Mod("lock_rng", "Lock RNG", lua: true));
        Assert.Equal(ModRpcs3State.NeedsLua, lua.State);
        Assert.False(lua.CanToggle);
        Assert.Contains("Lua", lua.ToggleReason);

        Assert.Equal(ModRpcs3State.NeedsLua, Decide(Disk(), Row(ModFlags.NeedsLua)).State);
        Assert.Equal(ModRpcs3State.NeedsLua, Decide(Disk(), Row(), refusal: ModPatchRefusal.NeedsLua).State);

        var parse = Decide(Disk(), Row(ModFlags.ParseError));
        Assert.Equal(ModRpcs3State.ParseError, parse.State);
        Assert.Equal(ModRpcs3Tone.Bad, parse.Tone);
        Assert.False(parse.CanToggle);
        Assert.Equal(ModRpcs3State.ParseError, Decide(Disk(), Row(), refusal: ModPatchRefusal.ParseError).State);

        var large = Decide(Disk(), Row(), refusal: ModPatchRefusal.TooLarge);
        Assert.Equal(ModRpcs3State.TooLarge, large.State);
        Assert.Equal("Too large for RPCS3", large.Text);
        Assert.False(large.CanToggle);
        Assert.Contains("one reply", large.ToggleReason);

        // One that is somehow enabled all the same can still be switched off.
        var stuck = Decide(Disk(LockRng(), enabled: true), Row(ModFlags.ParseError));
        Assert.True(stuck.Enabled);
        Assert.True(stuck.CanToggle);
    }

    [Fact]
    public void AModWithNoWordsAndNoCavesHasNothingToPatch()
    {
        // qwark-rpcs3 never looks for one, so it would sit on "Checking..." or "Restart" for ever.
        var empty = Decide(Disk(), Row(), Mod("lock_rng", "Lock RNG", words: 0));
        Assert.Equal(ModRpcs3State.NothingToPatch, empty.State);
        Assert.False(empty.CanToggle);

        var caveOnly = new LocalMod { Directory = "c", DirName = "lock_rng", Name = "Lock RNG", BinFiles = new[] { "rack.bin" } };
        Assert.Equal(ModRpcs3State.Disabled, Decide(Disk(), Row(), caveOnly).State);

        Assert.Equal(ModRpcs3State.NothingToPatch, Decide(Disk(), Row(), refusal: ModPatchRefusal.NothingToPatch).State);
    }

    [Fact]
    public void AnEnabledModSaysWhatQwarkFindsInTheGame()
    {
        var disk = Disk(LockRng(), enabled: true);

        var loaded = Decide(disk, Row(ModFlags.Loaded));
        Assert.Equal(ModRpcs3State.Loaded, loaded.State);
        Assert.Equal(ModRpcs3Tone.Good, loaded.Tone);
        Assert.True(loaded.Enabled);

        var checking = Decide(disk, Row(ModFlags.Checking));
        Assert.Equal(ModRpcs3State.Checking, checking.State);
        Assert.Equal("Checking...", checking.Text);

        var restart = Decide(disk, Row());
        Assert.Equal(ModRpcs3State.EnabledRestartNeeded, restart.State);
        Assert.Equal("Enabled - restart needed", restart.Text);
        Assert.Equal(ModRpcs3Tone.Pending, restart.Tone);
        Assert.True(restart.Enabled);
        Assert.Contains("restart the game in RPCS3", restart.Tooltip);

        var notUploaded = Decide(disk, null);
        Assert.Equal(ModRpcs3State.NotUploaded, notUploaded.State);
        Assert.Equal(ModRpcs3Tone.Quiet, notUploaded.Tone);
    }

    [Fact]
    public void AnEnabledModRpcs3AppliedThatQwarkDoesNotFindIsNotInGameMemory()
    {
        var applied = Disk(LockRng(), enabled: true, applied: new[] { LockRng().Description });

        var missing = Decide(applied, Row());
        Assert.Equal(ModRpcs3State.NotInGameMemory, missing.State);
        Assert.Equal(ModRpcs3Tone.Bad, missing.Tone);
        Assert.Contains("wrote over", missing.Tooltip);

        Assert.Equal(ModRpcs3State.Loaded, Decide(applied, Row(ModFlags.Loaded)).State);
        Assert.Equal(ModRpcs3State.Checking, Decide(applied, Row(ModFlags.Checking)).State);
    }

    [Fact]
    public void AnEntryWrittenThisSessionWaitsForTheRestartWhateverQwarkSays()
    {
        var status = Decide(Disk(LockRng(), enabled: true), Row(ModFlags.Loaded), rewritten: true);

        Assert.Equal(ModRpcs3State.EnabledRestartNeeded, status.State);
        Assert.Equal("Enabled - restart needed", status.Text);
        Assert.Contains("written this session", status.Tooltip);
    }

    [Fact]
    public void ANewerCopyInTheLibraryOffersTheUpdate()
    {
        var status = Decide(Disk(LockRng(libraryHash: 0x0BADF00D), enabled: true), Row(ModFlags.Loaded));

        Assert.Equal(ModRpcs3State.UpdateAvailable, status.State);
        Assert.True(status.CanUpdate);
        Assert.True(status.Enabled);

        // A switched-off one is simply rewritten when it is enabled again.
        Assert.Equal(ModRpcs3State.Disabled, Decide(Disk(LockRng(libraryHash: 0x0BADF00D)), Row()).State);
    }

    [Fact]
    public void EveryStateHasItsWordsItsColourAndItsTick()
    {
        var on = Disk(LockRng(), enabled: true);
        var off = Disk(LockRng(), enabled: false);
        var noFolder = new Rpcs3PatchDisk(Title, new Rpcs3FolderLookup(null, "RPCS3 is not running"), null, null, false, string.Empty);

        var cases = new (ModRpcs3Status Status, ModRpcs3State State, string Text, ModRpcs3Tone Tone, bool Ticked)[]
        {
            (Decide(null, Row()), ModRpcs3State.Waiting, "...", ModRpcs3Tone.Quiet, false),
            (Decide(noFolder, Row()), ModRpcs3State.Unknown, "Unknown", ModRpcs3Tone.Quiet, false),
            (Decide(on, Row(ModFlags.Loaded)), ModRpcs3State.Loaded, "Loaded", ModRpcs3Tone.Good, true),
            (Decide(on, Row(ModFlags.Checking)), ModRpcs3State.Checking, "Checking...", ModRpcs3Tone.Pending, true),
            (Decide(on, Row()), ModRpcs3State.EnabledRestartNeeded, "Enabled - restart needed", ModRpcs3Tone.Pending, true),
            (Decide(Disk(LockRng(libraryHash: 0x0BADF00D), enabled: true), Row()), ModRpcs3State.UpdateAvailable,
                "Update available", ModRpcs3Tone.Pending, true),
            (Decide(Disk(LockRng(), enabled: true, applied: new[] { LockRng().Description }), Row()), ModRpcs3State.NotInGameMemory,
                "Not in game memory", ModRpcs3Tone.Bad, true),
            (Decide(on, null), ModRpcs3State.NotUploaded, "Not uploaded", ModRpcs3Tone.Quiet, true),
            (Decide(off, Row()), ModRpcs3State.Disabled, "Disabled", ModRpcs3Tone.Quiet, false),
            (Decide(off, Row(ModFlags.Loaded)), ModRpcs3State.DisabledRestartNeeded, "Disabled - restart needed", ModRpcs3Tone.Pending, false),
            (Decide(Disk(), Row(ModFlags.NeedsLua)), ModRpcs3State.NeedsLua, "Needs Lua", ModRpcs3Tone.Quiet, false),
            (Decide(Disk(), Row(ModFlags.ParseError)), ModRpcs3State.ParseError, "Parse error", ModRpcs3Tone.Bad, false),
            (Decide(Disk(), Row(), refusal: ModPatchRefusal.TooLarge), ModRpcs3State.TooLarge, "Too large for RPCS3", ModRpcs3Tone.Quiet, false),
            (Decide(Disk(), Row(), refusal: ModPatchRefusal.NothingToPatch), ModRpcs3State.NothingToPatch,
                "Nothing to patch", ModRpcs3Tone.Quiet, false),
        };

        foreach (var (status, state, text, tone, ticked) in cases)
        {
            Assert.Equal(state, status.State);
            Assert.Equal(text, status.Text);
            Assert.Equal(tone, status.Tone);
            Assert.Equal(ticked, status.Enabled);
            Assert.False(string.IsNullOrWhiteSpace(status.Tooltip));
        }

        // Both states waiting on a restart say that it is RPCS3 that applies the change when the game boots.
        Assert.All(cases.Where(c => c.Text.EndsWith("restart needed", StringComparison.Ordinal)),
            c => Assert.Contains("when the game boots", c.Status.Tooltip));

        // And there is no state the Status column can be in that is not one of the above.
        Assert.Equal(Enum.GetValues<ModRpcs3State>().OrderBy(s => s), cases.Select(c => c.State).OrderBy(s => s));
    }

    // ================================================================ end to end

    private async Task<bool> PumpAsync(AppState state, Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            state.Tick(1f / 60f);
            state.Rpcs3Mods.Update();
            if (condition()) return true;
            await Task.Delay(16);
        }

        state.Tick(1f / 60f);
        return condition();
    }

    /// <summary>A mod in the PC library: patch.txt and any caves.</summary>
    private string AddToLibrary(string dir, string patch, params (string Name, byte[] Data)[] bins)
    {
        string folder = Path.Combine(_root, "library", Title, dir);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "patch.txt"), patch);
        foreach (var (name, data) in bins) File.WriteAllBytes(Path.Combine(folder, name), data);
        return folder;
    }

    private async Task<(FakeQwarkServer Server, AppState State, Rpcs3Folder Folder)> StartAsync(string log = "")
    {
        string root = MakeRpcs3();
        File.WriteAllText(Path.Combine(root, "log", Rpcs3Patches.LogName), log.Length > 0 ? log : BootLog());

        var server = new FakeQwarkServer { Emulator = true, NoCodePatches = true };
        server.Start();

        // The folder is set, so the search never looks at this PC's own RPCS3.
        var settings = new Settings
        {
            AutoReconnect = false,
            Rpcs3Target = true,
            Rpcs3Folder = root,
            ModsPath = Path.Combine(_root, "library"),
        };

        var state = new AppState(settings);
        state.Rpcs3Mods.Environment = Windows();
        state.Rpcs3Patch.Environment = Windows();
        await state.Client.ConnectAsync("127.0.0.1", server.Port);
        return (server, state, FolderAt(root));
    }

    private static LocalMod Local(AppState state, string dir) => state.LocalMods.Single(m => m.DirName == dir);

    [Fact]
    public async Task TickingAModUploadsItWritesItsWordsAndSwitchesItOn()
    {
        AddToLibrary("lock_rng", "#- name: Lock RNG\n#- author: someone\n0x5C8318: rack.bin\n0x5C8400: 0x4E800020\n",
            ("rack.bin", new byte[] { 0x80, 0x6D, 0x90, 0x00, 0x11 }));

        var (server, state, folder) = await StartAsync();
        using (server)
        using (state)
        {
            var mods = state.Rpcs3Mods;
            Assert.True(await PumpAsync(state, () => state.LocalMods.Count == 1 && mods.Disk is not null));
            var mod = Local(state, "lock_rng");
            Assert.Equal(ModRpcs3State.Disabled, mods.StatusFor(mod).State);

            mods.Toggle(mod, true);
            Assert.Null(mods.Pending);
            Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Kind != ToastKind.Info && t.Text.Contains("Lock RNG"))));
            Assert.Contains(state.Toasts, t => t.Kind == ToastKind.Success
                                               && t.Text == "Enabled Lock RNG. Restart the game in RPCS3 to load it.");

            // Uploaded, parsed by qwark, and written as the cave's words, the word and the tail byte.
            Assert.True(server.Files.ContainsKey($"{ModLibrary.ConsoleRoot}/{Title}/lock_rng/patch.txt"));
            var entry = Rpcs3Patches.ReadPatchFile(folder.PatchFile(Title)).ModEntryFor(Hash, "lock_rng")!;
            Assert.Equal("RaCMAN mod: Lock RNG [lock_rng]", entry.Description);
            Assert.Equal("someone", entry.Author);
            Assert.Equal(new[] { new PatchWord(0x005C8318, 0x806D9000), new PatchWord(0x005C8400, 0x4E800020) }, entry.Words);
            Assert.Equal(new[] { new PatchByte(0x005C831C, 0x11) }, entry.Bytes);
            Assert.Equal(mod.Hash, Rpcs3Patches.LibraryHashIn(entry.Notes));
            Assert.True(Rpcs3Patches.IsEnabled(File.ReadAllText(folder.PatchConfigFile), entry.Key(Title), "x"));

            Assert.True(await PumpAsync(state, () => mods.StatusFor(mod).State == ModRpcs3State.EnabledRestartNeeded));
            Assert.True(mods.StatusFor(mod).Enabled);

            // The game restarts in RPCS3: qwark-rpcs3 looks, and then finds it.
            server.SetModPresence("lock_rng", loaded: false, checking: true);
            server.Session = server.Session with { Generation = server.Session.Generation + 1 };
            Assert.True(await PumpAsync(state, () => mods.StatusFor(mod).State == ModRpcs3State.Checking));

            server.SetModPresence("lock_rng", loaded: true, checking: false);
            Assert.True(await PumpAsync(state, () => mods.StatusFor(mod).State == ModRpcs3State.Loaded));

            // Unticking switches it off and leaves the words in the file. The game keeps them until
            // it is restarted, and the row says so.
            mods.Toggle(mod, false);
            Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Text.StartsWith("Disabled Lock RNG", StringComparison.Ordinal))));
            Assert.True(await PumpAsync(state, () => mods.StatusFor(mod).State == ModRpcs3State.DisabledRestartNeeded));
            Assert.False(mods.StatusFor(mod).Enabled);
            Assert.False(Rpcs3Patches.IsEnabled(File.ReadAllText(folder.PatchConfigFile), entry.Key(Title), "x"));
            Assert.NotNull(Rpcs3Patches.ReadPatchFile(folder.PatchFile(Title)).ModEntryFor(Hash, "lock_rng"));

            // The game restarts in RPCS3 without it.
            server.SetModPresence("lock_rng", loaded: false, checking: false);
            server.Session = server.Session with { Generation = server.Session.Generation + 1 };
            Assert.True(await PumpAsync(state, () => mods.StatusFor(mod).State == ModRpcs3State.Disabled));
        }
    }

    [Fact]
    public async Task AModCanBeEnabledBetweenTwoBootsOfTheGame()
    {
        AddToLibrary("lock_rng", "#- name: Lock RNG\n0x5C8318: 0x806D9000\n");

        var (server, state, folder) = await StartAsync();
        using (server)
        using (state)
        {
            var mods = state.Rpcs3Mods;
            Assert.True(await PumpAsync(state, () => state.LocalMods.Count == 1 && mods.Disk is not null && state.DescribedGame == GameId.Rac1));

            // The game is stopped in RPCS3: qwark-rpcs3 is at the XMB and still holds its mods.
            server.Session = server.Session with { State = SessionState.Xmb, Game = GameId.None, TitleId = string.Empty };
            Assert.True(await PumpAsync(state, () => !state.Ingame && state.Session.State == SessionState.Xmb));

            var mod = Local(state, "lock_rng");
            Assert.True(mods.StatusFor(mod).CanToggle);
            mods.Toggle(mod, true);
            Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Text.StartsWith("Enabled Lock RNG", StringComparison.Ordinal))));

            var entry = Rpcs3Patches.ReadPatchFile(folder.PatchFile(Title)).ModEntryFor(Hash, "lock_rng")!;
            Assert.Equal("RaC1", entry.Game);
            Assert.Equal(new[] { new PatchWord(0x005C8318, 0x806D9000) }, entry.Words);
            Assert.True(Rpcs3Patches.IsEnabled(File.ReadAllText(folder.PatchConfigFile), entry.Key(Title), "x"));
        }
    }

    [Fact]
    public async Task ADependencyIsEnabledOnlyWhenTheUserSaysYes()
    {
        AddToLibrary("timer", "#- name: IL Decimal Timer\n0x100000: 0x60000000\n");
        AddToLibrary("ghost", "#- name: IL Ghost\n#- depends: IL Decimal Timer\n0x200000: 0x60000000\n");

        var (server, state, folder) = await StartAsync();
        using (server)
        using (state)
        {
            var mods = state.Rpcs3Mods;
            Assert.True(await PumpAsync(state, () => state.LocalMods.Count == 2 && mods.Disk is not null));
            var ghost = Local(state, "ghost");
            var timer = Local(state, "timer");

            // Cancel: nothing is asked of qwark and nothing is written.
            mods.Toggle(ghost, true);
            Assert.Equal("IL Ghost needs IL Decimal Timer. Enable both?", mods.Pending!.Message);
            mods.Cancel();
            Assert.Null(mods.Pending);
            await Task.Delay(100);
            state.Tick(1f / 60f);
            Assert.DoesNotContain(Opcode.ModPatch, server.RequestLog());
            Assert.False(File.Exists(folder.PatchFile(Title)));

            // Yes: both, the dependency first, in one write.
            mods.Toggle(ghost, true);
            Assert.Equal(new[] { timer, ghost }, mods.Pending!.Mods);
            mods.Confirm();
            Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Text.StartsWith("Enabled", StringComparison.Ordinal))));
            Assert.Contains(state.Toasts, t => t.Kind == ToastKind.Success
                                               && t.Text == "Enabled IL Decimal Timer and IL Ghost. Restart the game in RPCS3 to load them.");
            Assert.True(await PumpAsync(state, () => mods.IsEnabled(ghost) && mods.IsEnabled(timer)));

            // Switching the dependency off asks about the mod that needs it; no leaves both on.
            mods.Toggle(timer, false);
            Assert.Equal("IL Ghost needs IL Decimal Timer. Disable both?", mods.Pending!.Message);
            mods.Cancel();
            await Task.Delay(100);
            Assert.True(await PumpAsync(state, () => mods.IsEnabled(ghost) && mods.IsEnabled(timer)));

            mods.Toggle(timer, false);
            mods.Confirm();
            Assert.True(await PumpAsync(state, () => !mods.IsEnabled(ghost) && !mods.IsEnabled(timer)));
        }
    }

    [Fact]
    public async Task AModOverAnEnabledOneIsRefusedWithAToastNamingIt()
    {
        AddToLibrary("lock_rng", "#- name: Lock RNG\n0x5C8318: 0x806D9000\n0x5C831C: 0x4E800020\n");
        AddToLibrary("incremental_rng", "#- name: Incremental RNG\n0x5C8318: rand.bin\n", ("rand.bin", new byte[72]));

        var (server, state, folder) = await StartAsync();
        using (server)
        using (state)
        {
            var mods = state.Rpcs3Mods;
            Assert.True(await PumpAsync(state, () => state.LocalMods.Count == 2 && mods.Disk is not null));

            mods.Toggle(Local(state, "lock_rng"), true);
            Assert.True(await PumpAsync(state, () => mods.IsEnabled(Local(state, "lock_rng"))));

            mods.Toggle(Local(state, "incremental_rng"), true);
            Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Kind == ToastKind.Error)));

            var toast = state.Toasts.Last(t => t.Kind == ToastKind.Error);
            Assert.StartsWith("Incremental RNG was not enabled: Incremental RNG cannot be enabled", toast.Text);
            Assert.Contains("Lock RNG", toast.Text);
            Assert.Null(Rpcs3Patches.ReadPatchFile(folder.PatchFile(Title)).ModEntryFor(Hash, "incremental_rng"));
        }
    }

    [Fact]
    public async Task AModQwarkCannotHandOverIsRememberedAndItsBoxGreyedOut()
    {
        AddToLibrary("big", "#- name: Big\n0x100000: 0x60000000\n");

        var (server, state, folder) = await StartAsync();
        using (server)
        using (state)
        {
            var mods = state.Rpcs3Mods;
            Assert.True(await PumpAsync(state, () => state.LocalMods.Count == 1 && mods.Disk is not null));
            server.ModPatchStatuses["big"] = Status.Full;

            mods.Toggle(Local(state, "big"), true);
            Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Kind == ToastKind.Error)));
            Assert.Equal("Big was not enabled: Big is more than qwark-rpcs3 can hand over in one reply.",
                state.Toasts.Last(t => t.Kind == ToastKind.Error).Text);

            var status = mods.StatusFor(Local(state, "big"));
            Assert.Equal(ModRpcs3State.TooLarge, status.State);
            Assert.False(status.CanToggle);
            Assert.False(File.Exists(folder.PatchFile(Title)));
        }
    }

    [Fact]
    public async Task EveryReasonAWriteCannotHappenIsSaid()
    {
        AddToLibrary("lock_rng", "#- name: Lock RNG\n0x5C8318: 0x806D9000\n");

        // RPCS3's log says another game booted last, so there is no executable to write for.
        var (server, state, folder) = await StartAsync(log: BootLog().Replace(Title, "NPEA00386", StringComparison.Ordinal));
        using (server)
        using (state)
        {
            var mods = state.Rpcs3Mods;
            Assert.True(await PumpAsync(state, () => state.LocalMods.Count == 1 && mods.Disk is not null));
            Assert.Contains("NPEA00386", mods.DiskProblem);
            Assert.Equal(ModRpcs3State.Unknown, mods.StatusFor(Local(state, "lock_rng")).State);

            // The panel greys the box out; asked anyway, the write says why rather than guessing.
            mods.Toggle(Local(state, "lock_rng"), true);
            Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Kind == ToastKind.Error)));
            Assert.Contains("NPEA00386", state.Toasts.Last(t => t.Kind == ToastKind.Error).Text);
            Assert.False(File.Exists(folder.PatchFile(Title)));
        }
    }

    [Fact]
    public async Task AnUpdateAsksFirstAndThenRewritesTheEntry()
    {
        string dir = AddToLibrary("lock_rng", "#- name: Lock RNG\n#- version: 1.0\n0x5C8318: 0x806D9000\n");

        var (server, state, folder) = await StartAsync();
        using (server)
        using (state)
        {
            var mods = state.Rpcs3Mods;
            Assert.True(await PumpAsync(state, () => state.LocalMods.Count == 1 && mods.Disk is not null));
            mods.Toggle(Local(state, "lock_rng"), true);
            Assert.True(await PumpAsync(state, () => mods.IsEnabled(Local(state, "lock_rng"))));

            // A newer copy lands in the library.
            File.WriteAllText(Path.Combine(dir, "patch.txt"), "#- name: Lock RNG\n#- version: 1.1\n0x5C8318: 0x806D9001\n");
            state.RescanLocalMods();
            var newer = Local(state, "lock_rng");

            // A new session, so the first write is no longer this session's.
            server.SetModPresence("lock_rng", loaded: true, checking: false);
            server.Session = server.Session with { Generation = server.Session.Generation + 1 };
            Assert.True(await PumpAsync(state, () => mods.StatusFor(newer).State == ModRpcs3State.UpdateAvailable));

            mods.RequestUpdate(newer);
            Assert.StartsWith("Rewrite Lock RNG's RPCS3 patch from the copy in the library (version 1.1)?", mods.Pending!.Message);
            mods.Confirm();
            Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Text == "Updated Lock RNG. Restart the game in RPCS3 to load it.")));

            var entry = Rpcs3Patches.ReadPatchFile(folder.PatchFile(Title)).ModEntryFor(Hash, "lock_rng")!;
            Assert.Equal(new[] { new PatchWord(0x005C8318, 0x806D9001) }, entry.Words);
            Assert.True(await PumpAsync(state, () => mods.StatusFor(newer).State == ModRpcs3State.EnabledRestartNeeded));
        }
    }

    [Fact]
    public async Task TheConnectionPanelsHelperInstallKeepsTheModsAndTheModsPanelKeepsTheHelper()
    {
        AddToLibrary("lock_rng", "#- name: Lock RNG\n0x5C8318: 0x806D9000\n");
        AddToLibrary("bot", "#- name: Bot Info\n0x662000: 0x60000000\n");

        var (server, state, folder) = await StartAsync();
        using (server)
        using (state)
        {
            var mods = state.Rpcs3Mods;
            var helper = state.Rpcs3Patch;
            Assert.True(await PumpAsync(state, () => state.LocalMods.Count == 2 && mods.Disk is not null));

            mods.Toggle(Local(state, "lock_rng"), true);
            Assert.True(await PumpAsync(state, () => mods.IsEnabled(Local(state, "lock_rng"))));

            // The helper goes in the way the Connection panel puts it in.
            Assert.True(await PumpAsync(state, () => helper.Update().State == SaveFilePatchState.Install));
            helper.Install(helper.Folder!);
            Assert.True(await PumpAsync(state, () => !helper.Installing && helper.Update().State == SaveFilePatchState.RestartGame));

            var file = Rpcs3Patches.ReadPatchFile(folder.PatchFile(Title));
            Assert.NotNull(file.EntryFor(Hash));
            Assert.NotNull(file.ModEntryFor(Hash, "lock_rng"));
            mods.Recheck();
            Assert.True(await PumpAsync(state, () => mods.IsEnabled(Local(state, "lock_rng"))));

            // And a mod written after it leaves the helper in and switched on.
            mods.Toggle(Local(state, "bot"), true);
            Assert.True(await PumpAsync(state, () => mods.IsEnabled(Local(state, "bot"))));

            file = Rpcs3Patches.ReadPatchFile(folder.PatchFile(Title));
            Assert.Equal(server.SaveFilePatchWords, file.EntryFor(Hash)!.Words);
            string config = File.ReadAllText(folder.PatchConfigFile);
            Assert.True(Rpcs3Patches.IsEnabled(config, file.EntryFor(Hash)!.Key(Title), "x"));
            Assert.True(Rpcs3Patches.IsEnabled(config, file.ModEntryFor(Hash, "lock_rng")!.Key(Title), "x"));
            Assert.True(Rpcs3Patches.IsEnabled(config, file.ModEntryFor(Hash, "bot")!.Key(Title), "x"));
            Assert.Equal(SaveFilePatchState.RestartGame, helper.Update().State);
        }
    }
}
