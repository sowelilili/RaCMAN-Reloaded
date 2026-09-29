using System.Diagnostics;
using RaCMAN.App;
using RaCMAN.Protocol.Testing;
using YamlDotNet.RepresentationModel;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The savefile helper as an RPCS3 patch (qwark build 47, protocol revision 1.15): the reply,
/// the fake console's new answers, and everything the client does with RPCS3's files. Every RPCS3
/// folder here is a temporary one, and every machine is a made-up <see cref="Rpcs3Environment"/>,
/// so nothing ever looks at the RPCS3 on the PC the tests run on.
/// </summary>
public class Rpcs3PatchTests : IDisposable
{
    private const string Title = "NPEA00385";

    private const string Hash = "PPU-ec77eaf73a4f55d1c4ece532c3be6db0011e49ca";

    private const string OtherHash = "PPU-0123456789abcdef0123456789abcdef01234567";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "racman-rpcs3-" + Guid.NewGuid().ToString("N"));

    public Rpcs3PatchTests() => Directory.CreateDirectory(_root);

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

    private static readonly PatchWord[] Words =
    {
        new(0x000F0000, 0x9421FFF0),
        new(0x0004A2C8, 0x480A5D39),
    };

    /// <summary>A request byte cleared at load, as RaC2's have to be.</summary>
    private static readonly PatchByte[] Bytes = { new(0x010CD71D, 0x00) };

    private static PatchReply Patch(uint stamp = 0x1A2B3C4D, PatchWord[]? words = null, PatchByte[]? bytes = null) =>
        new(stamp, words ?? Words, bytes ?? Bytes);

    /// <summary>A Windows PC with these rpcs3.exe programs running and nothing in its environment.</summary>
    private static Rpcs3Environment Windows(params string[] running) =>
        new(true, false, _ => null, @"C:\Users\nobody", () => running);

    private static Rpcs3Environment Unix(string home, bool mac = false, Dictionary<string, string>? variables = null) =>
        new(false, mac, name => variables is not null && variables.TryGetValue(name, out var value) ? value : null,
            home, () => Array.Empty<string>());

    /// <summary>An RPCS3 folder as a Windows build lays it out, with its program and the three folders.</summary>
    private string MakeRpcs3(string name = "rpcs3")
    {
        string folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "config"));
        Directory.CreateDirectory(Path.Combine(folder, "patches"));
        Directory.CreateDirectory(Path.Combine(folder, "log"));
        File.WriteAllText(Path.Combine(folder, "rpcs3.exe"), "not really a program");
        return folder;
    }

    private static string BootLog(params (string Serial, string? Hash)[] boots)
    {
        var lines = new List<string>
        {
            "RPCS3 v0.0.42-19958-54014a7d Alpha | master",
            "·! 0:00:00.245776 SYS: Using VFS config:",
            "/dev_usb***/:",
            "  /dev_usb000:",
            "    Serial: \"\"",
        };

        int second = 10;
        foreach (var (serial, hash) in boots)
        {
            lines.Add($"·! 0:00:{second:00}.503599 SYS: Booting from gamelist per doubleclick...");
            lines.Add($"·! 0:00:{second:00}.524531 SYS: Title: Some game");
            lines.Add($"·! 0:00:{second:00}.524533 SYS: Serial: {serial}");
            lines.Add($"·! 0:00:{second:00}.524534 SYS: Category: HG");
            lines.Add($"·! 0:00:{second:00}.622891 PAT: Loading patch config file C:/rpcs3/config/patch_config.yml");
            if (hash is not null)
            {
                lines.Add($"·W 0:00:{second:00}.803170 ppu_loader: PPU executable hash: {hash}");
                lines.Add($"·W 0:00:{second:00}.816668 ppu_loader: SPU executable hash: SPU-66a3d167bb4cd65b8e3f081d26b43c1ebeb81f0d");
            }

            second += 5;
        }

        return string.Join("\n", lines) + "\n";
    }

    private static Rpcs3Folder FolderAt(string root) => Rpcs3Patches.Build(root, Windows(), "test");

    // ================================================================ the wire

    [Fact]
    public void SaveFilePatchIsOpcode0x00B8()
    {
        Assert.Equal(0x00B8, (ushort)Opcode.SaveFilePatch);
    }

    [Fact]
    public void TheReplyParsesBigEndianCountStampPairsAndBytes()
    {
        byte[] payload =
        {
            0x00, 0x02, 0x00, 0x00,             // n = 2, pad
            0x1A, 0x2B, 0x3C, 0x4D,             // stamp
            0x00, 0x0F, 0x00, 0x00, 0x94, 0x21, 0xFF, 0xF0,
            0x00, 0x04, 0xA2, 0xC8, 0x48, 0x0A, 0x5D, 0x39,
            0x00, 0x02, 0x00, 0x00,             // nb = 2, pad
            0x01, 0x0C, 0xD7, 0x1D, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x0C, 0xD7, 0x1E, 0xA4, 0x00, 0x00, 0x00,
        };

        var patch = PatchReply.Parse(payload);

        Assert.Equal(0x1A2B3C4Du, patch.Stamp);
        Assert.Equal(Words, patch.Words);
        Assert.Equal(new[] { new PatchByte(0x010CD71D, 0x00), new PatchByte(0x010CD71E, 0xA4) }, patch.Bytes);
        Assert.Equal(payload, patch.ToBytes());
    }

    [Fact]
    public void AReplyWithEmptyListsIsStillAReply()
    {
        var patch = PatchReply.Parse(new byte[] { 0, 0, 0, 0, 0, 0, 0, 7, 0, 0, 0, 0 });

        Assert.Empty(patch.Words);
        Assert.Empty(patch.Bytes);
        Assert.Equal(7u, patch.Stamp);
    }

    [Fact]
    public void AReplyThatStopsShortOfEitherListIsAProtocolError()
    {
        var whole = Patch().ToBytes();
        int wordsEnd = PatchReply.HeaderSize + Words.Length * PatchReply.WordSize;

        // Short of the header, of the words the header promises, of the byte list's own header,
        // and of the bytes that header promises.
        Assert.Throws<ProtocolException>(() => PatchReply.Parse(ReadOnlySpan<byte>.Empty));
        Assert.Throws<ProtocolException>(() => PatchReply.Parse(whole.AsSpan(0, 7)));
        Assert.Throws<ProtocolException>(() => PatchReply.Parse(whole.AsSpan(0, wordsEnd - 1)));
        Assert.Throws<ProtocolException>(() => PatchReply.Parse(whole.AsSpan(0, wordsEnd)));
        Assert.Throws<ProtocolException>(() => PatchReply.Parse(whole.AsSpan(0, wordsEnd + 3)));
        Assert.Throws<ProtocolException>(() => PatchReply.Parse(whole.AsSpan(0, whole.Length - 1)));

        // The whole of it parses, so the cuts above are what failed.
        Assert.Equal(Bytes, PatchReply.Parse(whole).Bytes);
    }

    [Fact]
    public void AnythingPastTheLastByteIsIgnored()
    {
        var bytes = Patch().ToBytes().Concat(new byte[] { 1, 2, 3, 4 }).ToArray();
        var patch = PatchReply.Parse(bytes);

        Assert.Equal(Words, patch.Words);
        Assert.Equal(Bytes, patch.Bytes);
    }

    [Fact]
    public void SameHelperComparesWordsAndBytesInOrderAndNotTheStamp()
    {
        var patch = Patch(stamp: 1);

        Assert.True(patch.SameWords(Words.ToList(), Bytes.ToList()));
        Assert.False(patch.SameWords(Words.Reverse().ToList(), Bytes));
        Assert.False(patch.SameWords(Words.Take(1).ToList(), Bytes));
        Assert.False(patch.SameWords(Words, Array.Empty<PatchByte>()));
        Assert.False(patch.SameWords(Words, new[] { new PatchByte(0x010CD71D, 0x80) }));
    }

    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync(bool rpcs3)
    {
        var server = new FakeQwarkServer { Emulator = rpcs3, NoCodePatches = rpcs3 };
        server.Start();
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    [Fact]
    public async Task TheFakeConsoleHandsOutItsWordsWithACrcStamp()
    {
        var (server, client) = await ConnectAsync(rpcs3: true);
        using (server)
        using (client)
        {
            var patch = await client.SaveFilePatchAsync();

            Assert.Equal(server.SaveFilePatchWords, patch.Words);
            Assert.Equal(server.SaveFilePatchBytes, patch.Bytes);

            // The stamp covers every byte after itself: the pairs, the byte list's header, the bytes.
            Assert.Equal(Crc32.Compute(patch.ToBytes().AsSpan(PatchReply.HeaderSize)), patch.Stamp);
            Assert.Equal(server.SaveFilePatchStamp, patch.Stamp);
        }
    }

    [Fact]
    public async Task SaveFilePatchIsRefusedOutsideAGameAndForAGameWithNoHelper()
    {
        var (server, client) = await ConnectAsync(rpcs3: true);
        using (server)
        using (client)
        {
            server.SaveFileSupported = false;
            var none = await Assert.ThrowsAsync<QwarkStatusException>(() => client.SaveFilePatchAsync());
            Assert.Equal(Status.Unsupported, none.Status);

            server.SaveFileSupported = true;
            server.Session = server.Session with { State = SessionState.Xmb };
            var xmb = await Assert.ThrowsAsync<QwarkStatusException>(() => client.SaveFilePatchAsync());
            Assert.Equal(Status.NotIngame, xmb.Status);

            // A module from before revision 1.15 has never heard of the op.
            server.SaveFileUnsupported = true;
            var old = await Assert.ThrowsAsync<QwarkStatusException>(() => client.SaveFilePatchAsync());
            Assert.Equal(Status.UnknownOp, old.Status);
        }
    }

    [Fact]
    public async Task UnderRpcs3InfoAnswersAndOnlyTheHelperOpsWaitForThePatch()
    {
        var (server, client) = await ConnectAsync(rpcs3: true);
        using (server)
        using (client)
        {
            // Revision 1.15: OK rather than UNSUPPORTED, with the helper not in.
            var info = await client.SaveFileInfoAsync();
            Assert.True(info.Supported);
            Assert.False(info.Installed);
            Assert.False(info.Running);

            var store = await Assert.ThrowsAsync<QwarkStatusException>(() => client.SaveFileStoreAsync("misc", "a.sav"));
            Assert.Equal(Status.Unsupported, store.Status);

            // The library itself is files on the console, which need no helper.
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "misc");
            Assert.Contains("misc", await client.SaveFileCategoriesAsync());

            // The game started again with the patch applied: every op works as on a console.
            server.SaveFileHelperInstalled = true;
            info = await client.SaveFileInfoAsync();
            Assert.True(info.Installed);
            Assert.True(info.Running);
            await client.SaveFileStoreAsync("misc", "a.sav");
        }
    }

    // ================================================================ the executable hash

    [Fact]
    public void TheHashIsTheOneLoggedAfterTheRunningTitlesSerial()
    {
        var lookup = Rpcs3Patches.FindExecutableHash(new StringReader(BootLog((Title, Hash))), Title);

        Assert.Equal(Hash, lookup.Hash);
        Assert.Equal(string.Empty, lookup.Problem);
        Assert.False(lookup.PatchApplied);
    }

    [Fact]
    public void TheLatestBootOfTheTitleWins()
    {
        var log = BootLog((Title, OtherHash), ("NPEA00386", "PPU-1111111111111111111111111111111111111111"), (Title, Hash));

        Assert.Equal(Hash, Rpcs3Patches.FindExecutableHash(new StringReader(log), Title).Hash);
    }

    [Fact]
    public void AnotherTitleBootedLastIsARefusal()
    {
        var log = BootLog((Title, Hash), ("NPEA00386", OtherHash));
        var lookup = Rpcs3Patches.FindExecutableHash(new StringReader(log), Title);

        Assert.Null(lookup.Hash);
        Assert.Contains("NPEA00386", lookup.Problem);
        Assert.Contains($"Boot {Title} in RPCS3 first", lookup.Problem);
    }

    [Fact]
    public void ALogWithNoBootOrNoHashYetIsARefusal()
    {
        var empty = Rpcs3Patches.FindExecutableHash(new StringReader(BootLog()), Title);
        Assert.Null(empty.Hash);
        Assert.Contains("no game booted", empty.Problem);

        // The serial is logged before the executable is loaded; a boot caught in between has no hash.
        var early = Rpcs3Patches.FindExecutableHash(new StringReader(BootLog((Title, Hash), (Title, null))), Title);
        Assert.Null(early.Hash);
        Assert.Contains("no executable hash", early.Problem);
    }

    [Fact]
    public void Rpcs3ApplyingThePatchAtThisBootIsNoticed()
    {
        var log = BootLog((Title, Hash))
                  + $"·S 0:00:10.900000 PAT: Applied patch (hash='{Hash}', description='{Rpcs3Patches.Description}', "
                  + "author='qwark', patch_version='1.0', file_version='1.2') (<- 2)\n";

        Assert.True(Rpcs3Patches.FindExecutableHash(new StringReader(log), Title).PatchApplied);

        // And a boot after it starts again from nothing.
        Assert.False(Rpcs3Patches.FindExecutableHash(new StringReader(log + BootLog((Title, Hash))), Title).PatchApplied);
    }

    [Fact]
    public void TheLogIsReadWhileRpcs3HoldsItOpen()
    {
        string root = MakeRpcs3();
        string log = Path.Combine(root, "log", Rpcs3Patches.LogName);

        // RPCS3 keeps writing its log for as long as it runs.
        using var writer = new FileStream(log, System.IO.FileMode.Create, FileAccess.Write, FileShare.Read);
        var bytes = System.Text.Encoding.UTF8.GetBytes(BootLog((Title, Hash)));
        writer.Write(bytes);
        writer.Flush();

        var lookup = Rpcs3Patches.FindExecutableHash(FolderAt(root), Title);
        Assert.Equal(Hash, lookup.Hash);
        Assert.Equal(log, lookup.Log);
    }

    [Fact]
    public void TheNewestLogCandidateIsTheOneRead()
    {
        string root = MakeRpcs3();
        string current = Path.Combine(root, "log", Rpcs3Patches.LogName);
        string old = Path.Combine(root, Rpcs3Patches.LogName);

        // An older build's log beside the program, left behind by an update.
        File.WriteAllText(old, BootLog(("NPEA00386", OtherHash)));
        File.WriteAllText(current, BootLog((Title, Hash)));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));

        Assert.Equal(current, FolderAt(root).NewestLog());
        Assert.Equal(Hash, Rpcs3Patches.FindExecutableHash(FolderAt(root), Title).Hash);
    }

    [Fact]
    public void NoLogAtAllIsARefusalThatNamesWhereItLooked()
    {
        string root = MakeRpcs3();
        var lookup = Rpcs3Patches.FindExecutableHash(FolderAt(root), Title);

        Assert.Null(lookup.Hash);
        Assert.Contains(Path.Combine(root, "log", Rpcs3Patches.LogName), lookup.Problem);
    }

    // ================================================================ finding RPCS3

    [Fact]
    public void OnWindowsTheRunningProgramsFolderIsRpcs3sFolder()
    {
        string root = MakeRpcs3();
        var found = Rpcs3Patches.Locate(null, Windows(Path.Combine(root, "rpcs3.exe")));

        Assert.NotNull(found.Folder);
        Assert.Equal(root, found.Folder!.Root);

        // fs::get_config_dir(true) is a config/ subfolder on Windows; the patches are not in it.
        Assert.Equal(Path.Combine(root, "config", "patch_config.yml"), found.Folder.PatchConfigFile);
        Assert.Equal(Path.Combine(root, "patches", $"{Title}_patch.yml"), found.Folder.PatchFile(Title));
        Assert.Equal(new[] { Path.Combine(root, "log", "RPCS3.log"), Path.Combine(root, "RPCS3.log") },
            found.Folder.LogCandidates);
    }

    [Fact]
    public void APortableFolderBesideTheProgramIsTheOneRpcs3Uses()
    {
        string root = MakeRpcs3();
        string portable = Path.Combine(root, "portable");
        Directory.CreateDirectory(portable);

        var found = Rpcs3Patches.Locate(null, Windows(Path.Combine(root, "rpcs3.exe")));
        Assert.Equal(portable, found.Folder!.Root);
    }

    [Fact]
    public void Rpcs3ConfigDirIsReadTheWayRpcs3ReadsIt()
    {
        string configured = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(configured);

        // Everything after the last separator is dropped, so only a trailing one names the folder.
        var withSlash = Windows() with { Variable = name => name == "RPCS3_CONFIG_DIR" ? configured + "\\" : null };
        Assert.Equal(configured, Rpcs3Patches.ConfigDirVariable(withSlash));

        var withoutSlash = Windows() with { Variable = name => name == "RPCS3_CONFIG_DIR" ? configured : null };
        Assert.Equal(_root, Rpcs3Patches.ConfigDirVariable(withoutSlash));

        // It comes after a portable folder and before the program's own folder.
        string root = MakeRpcs3();
        var running = withSlash with { RunningExecutables = () => new[] { Path.Combine(root, "rpcs3.exe") } };
        Assert.Equal(configured, Rpcs3Patches.Locate(null, running).Folder!.Root);
    }

    [Fact]
    public void WithRpcs3NotRunningOnWindowsTheFolderIsNotFound()
    {
        var found = Rpcs3Patches.Locate(null, Windows());

        Assert.Null(found.Folder);
        Assert.Contains("No running rpcs3.exe was found", found.Problem);
    }

    [Fact]
    public void TheFolderSetInTheSettingsWinsOverTheRunningProgram()
    {
        string running = MakeRpcs3("running");
        string chosen = MakeRpcs3("chosen");

        var found = Rpcs3Patches.Locate($"  \"{chosen}\\\" ", Windows(Path.Combine(running, "rpcs3.exe")));
        Assert.Equal(chosen, found.Folder!.Root);
        Assert.Equal("set below", found.Folder.FoundBy);
    }

    [Fact]
    public void AFolderSetInTheSettingsThatIsNotRpcs3IsRefused()
    {
        var missing = Rpcs3Patches.Locate(Path.Combine(_root, "nothing"), Windows());
        Assert.Null(missing.Folder);
        Assert.Contains("does not exist", missing.Problem);

        string documents = Path.Combine(_root, "Documents");
        Directory.CreateDirectory(documents);
        var wrong = Rpcs3Patches.Locate(documents, Windows());
        Assert.Null(wrong.Folder);
        Assert.Contains("does not look like RPCS3's folder", wrong.Problem);

        var relative = Rpcs3Patches.Locate("rpcs3", Windows());
        Assert.Null(relative.Folder);
        Assert.Contains("not a whole path", relative.Problem);
    }

    [Fact]
    public void OnLinuxTheConfigFolderAndTheCacheLogAreRpcs3s()
    {
        string home = Path.Combine(_root, "home");
        string config = Path.Combine(home, ".config", "rpcs3");
        Directory.CreateDirectory(config);

        var found = Rpcs3Patches.Locate(null, Unix(home));
        Assert.Equal(config, found.Folder!.Root);

        // fs::get_config_dir(true) is the folder itself off Windows, and the log is in the cache.
        Assert.Equal(Path.Combine(config, "patch_config.yml"), found.Folder.PatchConfigFile);
        Assert.Contains(Path.Combine(home, ".cache", "rpcs3", "RPCS3.log"), found.Folder.LogCandidates);
    }

    [Fact]
    public void OnLinuxTheFlatpakIsTakenWhenItsLogIsTheNewest()
    {
        string home = Path.Combine(_root, "home");
        string native = Path.Combine(home, ".config", "rpcs3");
        string sandbox = Path.Combine(home, ".var", "app", Rpcs3Patches.FlatpakId);
        string flatpak = Path.Combine(sandbox, "config", "rpcs3");
        Directory.CreateDirectory(native);
        Directory.CreateDirectory(flatpak);

        string nativeLog = Path.Combine(home, ".cache", "rpcs3", "RPCS3.log");
        string flatpakLog = Path.Combine(sandbox, "cache", "rpcs3", "RPCS3.log");
        Directory.CreateDirectory(Path.GetDirectoryName(nativeLog)!);
        Directory.CreateDirectory(Path.GetDirectoryName(flatpakLog)!);
        File.WriteAllText(nativeLog, "old");
        File.WriteAllText(flatpakLog, "new");
        File.SetLastWriteTimeUtc(nativeLog, DateTime.UtcNow.AddHours(-2));

        var found = Rpcs3Patches.Locate(null, Unix(home));
        Assert.Equal(flatpak, found.Folder!.Root);
        Assert.Equal(flatpakLog, found.Folder.NewestLog());

        // The other way round, the native build is the one running.
        File.SetLastWriteTimeUtc(nativeLog, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(flatpakLog, DateTime.UtcNow.AddHours(-2));
        Assert.Equal(native, Rpcs3Patches.Locate(null, Unix(home)).Folder!.Root);
    }

    [Fact]
    public void XdgConfigHomeMovesTheLinuxFolder()
    {
        string home = Path.Combine(_root, "home");
        string xdg = Path.Combine(_root, "xdg");
        Directory.CreateDirectory(Path.Combine(xdg, "rpcs3"));

        var environment = Unix(home, variables: new() { ["XDG_CONFIG_HOME"] = xdg });
        Assert.Equal(Path.Combine(xdg, "rpcs3"), Rpcs3Patches.Locate(null, environment).Folder!.Root);
    }

    [Fact]
    public void OnMacOSItIsApplicationSupportAndTheLogIsInCaches()
    {
        string home = Path.Combine(_root, "home");
        string support = Path.Combine(home, "Library", "Application Support", "rpcs3");
        Directory.CreateDirectory(support);

        var found = Rpcs3Patches.Locate(null, Unix(home, mac: true));
        Assert.Equal(support, found.Folder!.Root);
        Assert.Contains(Path.Combine(home, "Library", "Caches", "rpcs3", "RPCS3.log"), found.Folder.LogCandidates);
    }

    [Fact]
    public void NoLinuxFolderIsANamedRefusal()
    {
        var found = Rpcs3Patches.Locate(null, Unix(Path.Combine(_root, "home")));

        Assert.Null(found.Folder);
        Assert.Contains(Path.Combine(_root, "home", ".config", "rpcs3"), found.Problem);
    }

    [Fact]
    public void ATildeInTheSettingIsTheHomeFolder()
    {
        Assert.Equal(Path.Combine("/home/me", ".config/rpcs3"), Rpcs3Patches.CleanPath("~/.config/rpcs3/", "/home/me"));
        Assert.Equal(@"C:\RPCS3", Rpcs3Patches.CleanPath(@" ""C:\RPCS3\"" ", "/home/me"));
        Assert.Equal(string.Empty, Rpcs3Patches.CleanPath("   ", "/home/me"));
    }

    // ================================================================ the patch file

    private const string ExpectedPatchFile =
        "# RaCMAN Reloaded writes this file: qwark's savefile helper and the mods enabled in RaCMAN. Edits made here are lost.\n"
        + "Version: 1.2\n"
        + "\n"
        + "PPU-ec77eaf73a4f55d1c4ece532c3be6db0011e49ca:\n"
        + "  \"qwark savefile helper\":\n"
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

    private static PatchFileEntry Entry(string hash = Hash, string game = "RaC1", PatchWord[]? words = null,
        string? notes = null, PatchByte[]? bytes = null) =>
        new(hash, game, words ?? Words, bytes ?? Bytes, notes ?? Rpcs3Patches.NotesFor(47, 0x1A2B3C4D));

    [Fact]
    public void ThePatchFileIsExactlyThis()
    {
        Assert.Equal(ExpectedPatchFile, Rpcs3Patches.BuildPatchFile(Title, new[] { Entry() }));
    }

    [Fact]
    public void ThePatchFileHasTheShapeRpcs3Reads()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(ExpectedPatchFile));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;

        Assert.Equal("1.2", ((YamlScalarNode)root["Version"]).Value);

        var entry = (YamlMappingNode)root[Hash]["qwark savefile helper"];
        var serial = (YamlSequenceNode)entry["Games"]["RaC1"][Title];
        Assert.Equal("All", ((YamlScalarNode)serial.Children.Single()).Value);
        Assert.Equal("1.0", ((YamlScalarNode)entry["Patch Version"]).Value);

        var lines = ((YamlSequenceNode)entry["Patch"]).Children.Cast<YamlSequenceNode>().ToList();
        Assert.Equal(new[] { "be32", "0x000f0000", "0x9421fff0" },
            lines[0].Children.Select(node => ((YamlScalarNode)node).Value));

        // The single bytes come after every word.
        Assert.Equal(new[] { "byte", "0x010cd71d", "0x00" },
            lines[^1].Children.Select(node => ((YamlScalarNode)node).Value));
    }

    [Fact]
    public void EveryByteIsALineOfItsOwnAfterTheWords()
    {
        var bytes = new[] { new PatchByte(0x010CD71D, 0x00), new PatchByte(0x010CD71E, 0x00), new PatchByte(0x010CD71F, 0xA4) };
        string text = Rpcs3Patches.BuildPatchFile(Title, new[] { Entry(bytes: bytes) });

        Assert.EndsWith(
            "      - [ be32, 0x0004a2c8, 0x480a5d39 ]\n"
            + "      - [ byte, 0x010cd71d, 0x00 ]\n"
            + "      - [ byte, 0x010cd71e, 0x00 ]\n"
            + "      - [ byte, 0x010cd71f, 0xa4 ]\n",
            text);
        Assert.Equal(bytes, Rpcs3Patches.ParsePatchFile(text, "x").Entries.Single().Bytes);
    }

    [Fact]
    public void ThePatchFileReadsBackAsItsEntries()
    {
        var state = Rpcs3Patches.ParsePatchFile(ExpectedPatchFile, "x");

        Assert.Equal(PatchFileKind.Ours, state.Kind);
        var entry = Assert.Single(state.Entries);
        Assert.Equal(Hash, entry.Hash);
        Assert.Equal("RaC1", entry.Game);
        Assert.Equal(Words, entry.Words);
        Assert.Equal(Bytes, entry.Bytes);
        Assert.Equal("qwark build 47, helper stamp 0x1a2b3c4d", entry.Notes);
        Assert.True(entry.Holds(Patch(stamp: 0)));
        Assert.Same(entry, state.EntryFor(Hash.ToUpperInvariant()));
    }

    [Fact]
    public void AnEntryHoldsAHelperOnlyWithTheSameBytesToo()
    {
        Assert.False(Entry(bytes: Array.Empty<PatchByte>()).Holds(Patch()));
        Assert.False(Entry(bytes: new[] { new PatchByte(0x010CD71D, 0x80) }).Holds(Patch()));
        Assert.True(Entry().Holds(Patch()));
    }

    [Fact]
    public void AFileWithoutTheMarkerIsSomebodyElses()
    {
        var state = Rpcs3Patches.ParsePatchFile("Version: 1.2\n\n" + ExpectedPatchFile, "C:/rpcs3/patches/x_patch.yml");

        Assert.Equal(PatchFileKind.Foreign, state.Kind);
        Assert.Contains("was not written by RaCMAN", state.Problem);
    }

    [Fact]
    public void OurFileThatIsNotYamlIsBrokenNotForeign()
    {
        var state = Rpcs3Patches.ParsePatchFile(Rpcs3Patches.MarkerLine + "\nVersion: [1.2\n", "x");
        Assert.Equal(PatchFileKind.Broken, state.Kind);
        Assert.Contains("Delete it", state.Problem);
    }

    [Fact]
    public void ARewriteKeepsEveryExecutableOfTheGameAndBringsItUpToDate()
    {
        var oldWords = new[] { new PatchWord(0x000F0000, 0x60000000) };
        var existing = new[]
        {
            Entry(OtherHash, words: oldWords, notes: "qwark build 45, helper stamp 0x00000001", bytes: Array.Empty<PatchByte>()),
        };

        var merged = Rpcs3Patches.MergeEntries(existing, Entry());

        Assert.Equal(new[] { OtherHash, Hash }, merged.Select(entry => entry.Hash));
        Assert.All(merged, entry => Assert.Equal(Words, entry.Words));
        Assert.All(merged, entry => Assert.Equal(Bytes, entry.Bytes));
        Assert.All(merged, entry => Assert.Equal("qwark build 47, helper stamp 0x1a2b3c4d", entry.Notes));
    }

    [Fact]
    public void ARewriteLeavesAnotherGamesExecutableAlone()
    {
        // BCES01503 hosts three games behind one title id, each with its own helper.
        var rac2Words = new[] { new PatchWord(0x00200000, 0x7C0802A6) };
        var rac2 = Entry(OtherHash, "RaC2", rac2Words, "qwark build 47, helper stamp 0x00000002");

        var merged = Rpcs3Patches.MergeEntries(new[] { rac2 }, Entry());

        Assert.Equal(rac2, merged[0]);
        Assert.Equal(Entry(), merged[1]);
    }

    [Fact]
    public void ARewriteOfTheSameExecutableReplacesItInPlace()
    {
        var old = Entry(words: new[] { new PatchWord(1, 2) });
        var merged = Rpcs3Patches.MergeEntries(new[] { old, Entry(OtherHash) }, Entry());

        Assert.Equal(new[] { Hash, OtherHash }, merged.Select(entry => entry.Hash));
        Assert.Equal(Words, merged[0].Words);
    }

    // ================================================================ patch_config.yml

    /// <summary>patch_config.yml as RPCS3's Patch Manager writes it, with somebody's own switches in it.</summary>
    private const string UsersConfig =
        "PPU-c14042df6304d3e420a9917e6f8e5fc05cc38b4c:\n"
        + "  Infinite Ammo:\n"
        + "    \"Ratchet & Clank Future: Tools of Destruction\":\n"
        + "      BCUS98127:\n"
        + "        All:\n"
        + "          Enabled: true\n"
        + "  60fps:\n"
        + "    Ratchet & Clank Future™:\n"
        + "      BCUS98127:\n"
        + "        01.00:\n"
        + "          Enabled: false\n"
        + "          Configurable Values:\n"
        + "            Frame rate: 60\n";

    private static PatchConfigKey Key(string hash = Hash, string game = "RaC1") => new(hash, game, Title);

    [Fact]
    public void EnablingKeepsTheUsersSwitchesExactlyAsTheyWere()
    {
        string merged = Rpcs3Patches.EnablePatches(UsersConfig, new[] { Key() }, "patch_config.yml");

        Assert.StartsWith(UsersConfig, merged);
        Assert.True(Rpcs3Patches.IsEnabled(merged, Key(), "x"));

        var root = (YamlMappingNode)Load(merged);
        Assert.Equal("true", ((YamlScalarNode)root["PPU-c14042df6304d3e420a9917e6f8e5fc05cc38b4c"]["Infinite Ammo"]
            ["Ratchet & Clank Future: Tools of Destruction"]["BCUS98127"]["All"]["Enabled"]).Value);
        Assert.Equal("60", ((YamlScalarNode)root["PPU-c14042df6304d3e420a9917e6f8e5fc05cc38b4c"]["60fps"]
            ["Ratchet & Clank Future™"]["BCUS98127"]["01.00"]["Configurable Values"]["Frame rate"]).Value);
    }

    [Fact]
    public void TheSwitchIsWhereRpcs3LooksForAnAllVersionsEntry()
    {
        string created = Rpcs3Patches.EnablePatches(null, new[] { Key() }, "patch_config.yml");

        Assert.Equal(
            "PPU-ec77eaf73a4f55d1c4ece532c3be6db0011e49ca:\n"
            + "  qwark savefile helper:\n"
            + "    RaC1:\n"
            + "      NPEA00385:\n"
            + "        All:\n"
            + "          Enabled: true\n",
            created);
    }

    [Fact]
    public void ASwitchedOffEntryIsSwitchedOnAndNothingElseMoves()
    {
        string off = UsersConfig
                     + "PPU-ec77eaf73a4f55d1c4ece532c3be6db0011e49ca:\n"
                     + "  qwark savefile helper:\n"
                     + "    RaC1:\n"
                     + "      NPEA00385:\n"
                     + "        All:\n"
                     + "          Enabled: false\n";

        Assert.False(Rpcs3Patches.IsEnabled(off, Key(), "x"));

        string on = Rpcs3Patches.EnablePatches(off, new[] { Key() }, "x");
        Assert.Equal(off.Replace("Enabled: false\n", "Enabled: true\n", StringComparison.Ordinal)
                        .Replace("01.00:\n          Enabled: true", "01.00:\n          Enabled: false", StringComparison.Ordinal),
            on);
    }

    [Fact]
    public void EveryExecutableInTheFileIsSwitchedOn()
    {
        string merged = Rpcs3Patches.EnablePatches(string.Empty, new[] { Key(), Key(OtherHash) }, "x");

        Assert.True(Rpcs3Patches.IsEnabled(merged, Key(), "x"));
        Assert.True(Rpcs3Patches.IsEnabled(merged, Key(OtherHash), "x"));
        Assert.False(Rpcs3Patches.IsEnabled(merged, Key(game: "RaC2"), "x"));
    }

    [Fact]
    public void AConfigThatIsNotAMapIsNeverReplaced()
    {
        var scalar = Assert.Throws<Rpcs3PatchException>(
            () => Rpcs3Patches.EnablePatches("just some text\n", new[] { Key() }, "patch_config.yml"));
        Assert.Contains("not a YAML map", scalar.Message);

        var broken = Assert.Throws<Rpcs3PatchException>(
            () => Rpcs3Patches.EnablePatches("PPU-x: [unclosed\n", new[] { Key() }, "patch_config.yml"));
        Assert.Contains("not valid YAML", broken.Message);

        // A hash the user has filled with something that is not a map is theirs to sort out.
        var odd = Assert.Throws<Rpcs3PatchException>(
            () => Rpcs3Patches.EnablePatches($"{Hash}: [ 1, 2 ]\n", new[] { Key() }, "patch_config.yml"));
        Assert.Contains("something other than a map", odd.Message);
    }

    private static YamlNode Load(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return stream.Documents[0].RootNode;
    }

    // ================================================================ the install

    [Fact]
    public void AnInstallWritesThePatchAndSwitchesItOnKeepingABackup()
    {
        string root = MakeRpcs3();
        var folder = FolderAt(root);
        File.WriteAllText(folder.PatchConfigFile, UsersConfig);
        File.WriteAllText(Path.Combine(folder.PatchesFolder, "patch.yml"), "Version: 1.2\n");

        var backedUp = new HashSet<string>();
        var plan = Rpcs3Patches.Plan(folder, Title, "RaC1", Hash, Patch(), 47);
        Rpcs3Patches.Write(plan, backedUp);

        Assert.Equal(ExpectedPatchFile, File.ReadAllText(folder.PatchFile(Title)));
        string config = File.ReadAllText(folder.PatchConfigFile);
        Assert.StartsWith(UsersConfig, config);
        Assert.True(Rpcs3Patches.IsEnabled(config, Key(), "x"));
        Assert.Equal(UsersConfig, File.ReadAllText(folder.PatchConfigFile + Rpcs3Patches.BackupSuffix));

        // RPCS3's own patch file is not this client's business.
        Assert.Equal("Version: 1.2\n", File.ReadAllText(Path.Combine(folder.PatchesFolder, "patch.yml")));
        Assert.Empty(Directory.GetFiles(root, "*.racman-tmp", SearchOption.AllDirectories));

        // A second install in the same session keeps the copy of what the user had, not of the first write.
        var again = Rpcs3Patches.Plan(folder, Title, "RaC1", OtherHash, Patch(), 47);
        Rpcs3Patches.Write(again, backedUp);
        Assert.Equal(UsersConfig, File.ReadAllText(folder.PatchConfigFile + Rpcs3Patches.BackupSuffix));
        Assert.True(Rpcs3Patches.IsEnabled(File.ReadAllText(folder.PatchConfigFile), Key(OtherHash), "x"));
        Assert.Equal(new[] { Hash, OtherHash },
            Rpcs3Patches.ReadPatchFile(folder.PatchFile(Title)).Entries.Select(entry => entry.Hash));
    }

    [Fact]
    public void AnInstallWithNoPatchConfigCreatesItAndBacksNothingUp()
    {
        string root = MakeRpcs3();
        Directory.Delete(Path.Combine(root, "config"));
        var folder = FolderAt(root);

        var plan = Rpcs3Patches.Plan(folder, Title, "RaC1", Hash, Patch(), 47);
        Assert.False(plan.ConfigExists);
        Rpcs3Patches.Write(plan, new HashSet<string>());

        Assert.True(Rpcs3Patches.IsEnabled(File.ReadAllText(folder.PatchConfigFile), Key(), "x"));
        Assert.False(File.Exists(folder.PatchConfigFile + Rpcs3Patches.BackupSuffix));
    }

    [Fact]
    public void AnInstallRefusesToOverwriteSomebodyElsesPatchFile()
    {
        string root = MakeRpcs3();
        var folder = FolderAt(root);
        const string theirs = "Version: 1.2\n\nPPU-aaaa:\n  \"Their cheat\":\n    Patch: []\n";
        File.WriteAllText(folder.PatchFile(Title), theirs);
        File.WriteAllText(folder.PatchConfigFile, UsersConfig);

        var refused = Assert.Throws<Rpcs3PatchException>(() => Rpcs3Patches.Plan(folder, Title, "RaC1", Hash, Patch(), 47));

        Assert.Contains("was not written by RaCMAN", refused.Message);
        Assert.Equal(theirs, File.ReadAllText(folder.PatchFile(Title)));
        Assert.Equal(UsersConfig, File.ReadAllText(folder.PatchConfigFile));
    }

    [Fact]
    public void AnInstallRefusesARplyWithNoWordsAndATitleThatIsNotOne()
    {
        var folder = FolderAt(MakeRpcs3());

        Assert.Throws<Rpcs3PatchException>(() => Rpcs3Patches.Plan(folder, Title, "RaC1", Hash, Patch(words: Array.Empty<PatchWord>()), 47));
        Assert.Throws<Rpcs3PatchException>(() => Rpcs3Patches.Plan(folder, "../evil", "RaC1", Hash, Patch(), 47));
        Assert.Throws<Rpcs3PatchException>(() => Rpcs3Patches.Plan(folder, Title, "RaC1", "not-a-hash", Patch(), 47));
    }

    [Fact]
    public void TheDiskCheckFollowsTheInstall()
    {
        string root = MakeRpcs3();
        var folder = FolderAt(root);
        File.WriteAllText(Path.Combine(root, "log", Rpcs3Patches.LogName), BootLog((Title, Hash)));

        var before = Rpcs3PatchDisk.Inspect(root, Windows(), Title);
        Assert.Equal(PatchFileKind.Missing, before.File!.Kind);
        Assert.False(before.Enabled);

        Rpcs3Patches.Write(Rpcs3Patches.Plan(folder, Title, "RaC1", Hash, Patch(), 47), new HashSet<string>());

        var after = Rpcs3PatchDisk.Inspect(root, Windows(), Title);
        Assert.Equal(Hash, after.Hash!.Hash);
        Assert.Equal(Words, after.File!.EntryFor(Hash)!.Words);
        Assert.True(after.Enabled);
        Assert.Equal(string.Empty, after.Problem);
    }

    [Fact]
    public void TheDiskCheckSaysSoWhenPatchConfigCannotBeRead()
    {
        string root = MakeRpcs3();
        File.WriteAllText(Path.Combine(root, "log", Rpcs3Patches.LogName), BootLog((Title, Hash)));
        File.WriteAllText(FolderAt(root).PatchConfigFile, "just some text\n");

        var disk = Rpcs3PatchDisk.Inspect(root, Windows(), Title);
        Assert.Contains("not a YAML map", disk.Problem);
    }

    // ================================================================ the decision

    private static SaveFilePatchSession Session(bool connected = true, bool rpcs3 = true, bool ingame = true,
        bool known = true, bool installed = false) =>
        new(connected, rpcs3, ingame, known, Title, 47, new SaveFileInfo(true, installed, installed, 0, 0x1000));

    private static Rpcs3PatchDisk Disk(PatchFileEntry? entry = null, bool enabled = false, bool applied = false,
        PatchFileKind kind = PatchFileKind.Ours, string problem = "")
    {
        var folder = Rpcs3Patches.Build(@"C:\rpcs3", Windows(), "test");
        var file = new PatchFileState("C:\\rpcs3\\patches\\x", kind,
            entry is null ? Array.Empty<PatchFileEntry>() : new[] { entry },
            kind == PatchFileKind.Foreign ? "not written by RaCMAN" : string.Empty);
        return new Rpcs3PatchDisk(Title, new Rpcs3FolderLookup(folder, string.Empty),
            new ExecutableHashLookup(Hash, string.Empty, applied), file, enabled, problem);
    }

    private static readonly SaveFilePatchReply Words47 = SaveFilePatchReply.Ok(Patch());

    private static SaveFilePatchState Decide(SaveFilePatchSession session, SaveFilePatchReply reply, Rpcs3PatchDisk? disk) =>
        Rpcs3PatchController.Decide(session, reply, disk).State;

    [Fact]
    public void BeforeThereIsAGameThereIsOnlyWaiting()
    {
        Assert.Equal(SaveFilePatchState.Waiting, Decide(Session(connected: false), Words47, Disk()));
        Assert.Equal(SaveFilePatchState.Waiting, Decide(Session(ingame: false), Words47, Disk()));
        Assert.Equal(SaveFilePatchState.Waiting, Decide(Session(), SaveFilePatchReply.Pending, Disk()));
        Assert.Equal(SaveFilePatchState.Waiting, Decide(Session(), Words47, null));
    }

    [Fact]
    public void AConsoleThatPatchesCodeItselfHasNothingToShow()
    {
        Assert.Equal(SaveFilePatchState.Hidden, Decide(Session(rpcs3: false), Words47, Disk()));
    }

    [Fact]
    public void TheHelperInTheGameIsActiveWhateverTheFilesSay()
    {
        Assert.Equal(SaveFilePatchState.Active, Decide(Session(installed: true), SaveFilePatchReply.NotAsked, null));
    }

    [Fact]
    public void NotInstalledOutOfDateAndSwitchedOffAllOfferTheButton()
    {
        var missing = Rpcs3PatchController.Decide(Session(), Words47, Disk());
        Assert.Equal(SaveFilePatchState.Install, missing.State);
        Assert.True(missing.CanInstall);
        Assert.StartsWith("Not installed", missing.Message);

        var stale = Rpcs3PatchController.Decide(Session(), Words47, Disk(Entry(words: new[] { new PatchWord(1, 2) }), enabled: true));
        Assert.Equal(SaveFilePatchState.Install, stale.State);
        Assert.StartsWith("Out of date", stale.Message);

        // A file from before the byte list, with the right words and no bytes, is out of date too.
        var noBytes = Rpcs3PatchController.Decide(Session(), Words47,
            Disk(Entry(bytes: Array.Empty<PatchByte>()), enabled: true));
        Assert.Equal(SaveFilePatchState.Install, noBytes.State);
        Assert.StartsWith("Out of date", noBytes.Message);

        var off = Rpcs3PatchController.Decide(Session(), Words47, Disk(Entry(), enabled: false));
        Assert.Equal(SaveFilePatchState.Install, off.State);
        Assert.Contains("switch it off", off.Message);
    }

    [Fact]
    public void TheCurrentWordsSwitchedOnMeanRestartTheGame()
    {
        var restart = Rpcs3PatchController.Decide(Session(), Words47, Disk(Entry(), enabled: true));

        Assert.Equal(SaveFilePatchState.RestartGame, restart.State);
        Assert.False(restart.CanInstall);
        Assert.Contains("Restart the game in RPCS3", restart.Message);
    }

    [Fact]
    public void APatchRpcs3AppliedThatQwarkDoesNotFindIsSaid()
    {
        var odd = Rpcs3PatchController.Decide(Session(), Words47, Disk(Entry(), enabled: true, applied: true));

        Assert.Equal(SaveFilePatchState.Unavailable, odd.State);
        Assert.Contains("qwark does not find the helper", odd.Message);
    }

    [Fact]
    public void EveryReasonItCannotBeInstalledIsSaid()
    {
        var noHelper = Rpcs3PatchController.Decide(Session(), new SaveFilePatchReply(SaveFilePatchReplyKind.NoHelper), Disk());
        Assert.Equal(SaveFilePatchState.Unavailable, noHelper.State);
        Assert.Contains("no savefile helper", noHelper.Message);

        var old = Rpcs3PatchController.Decide(Session(), new SaveFilePatchReply(SaveFilePatchReplyKind.TooOld), Disk());
        Assert.Contains($"It needs build {QwarkClient.ExpectedQwarkBuild}", old.Message);

        var failed = Rpcs3PatchController.Decide(Session(),
            new SaveFilePatchReply(SaveFilePatchReplyKind.Failed, Problem: "the link went away"), Disk());
        Assert.Equal("the link went away", failed.Message);

        var unknown = Rpcs3PatchController.Decide(Session(known: false), Words47, Disk());
        Assert.Equal(SaveFilePatchState.Unavailable, unknown.State);

        var noFolder = Rpcs3PatchController.Decide(Session(), Words47,
            new Rpcs3PatchDisk(Title, new Rpcs3FolderLookup(null, "RPCS3 is not running"), null, null, false, string.Empty));
        Assert.Equal(SaveFilePatchState.Unavailable, noFolder.State);
        Assert.True(noFolder.FolderProblem);
        Assert.Equal("RPCS3 is not running", noFolder.Message);

        var folder = Rpcs3Patches.Build(@"C:\rpcs3", Windows(), "test");
        var noHash = Rpcs3PatchController.Decide(Session(), Words47,
            new Rpcs3PatchDisk(Title, new Rpcs3FolderLookup(folder, string.Empty),
                new ExecutableHashLookup(null, "Boot NPEA00385 in RPCS3 first."), null, false, string.Empty));
        Assert.Equal(SaveFilePatchState.Unavailable, noHash.State);
        Assert.False(noHash.FolderProblem);
        Assert.Equal("Boot NPEA00385 in RPCS3 first.", noHash.Message);

        var foreign = Rpcs3PatchController.Decide(Session(), Words47, Disk(kind: PatchFileKind.Foreign));
        Assert.Equal(SaveFilePatchState.Unavailable, foreign.State);
        Assert.Contains("not written by RaCMAN", foreign.Message);

        var config = Rpcs3PatchController.Decide(Session(), Words47, Disk(problem: "patch_config.yml is not a YAML map"));
        Assert.Equal("patch_config.yml is not a YAML map", config.Message);
    }

    [Fact]
    public void ALookAtAnotherTitlesFolderIsNotTakenForThisOne()
    {
        var elsewhere = Disk(Entry(), enabled: true) with { TitleId = "NPEA00386" };
        Assert.Equal(SaveFilePatchState.Waiting, Decide(Session(), Words47, elsewhere));
    }

    // ================================================================ end to end

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
    public async Task FromNotInstalledToActiveAgainstTheFakeRpcs3()
    {
        string root = MakeRpcs3();
        File.WriteAllText(Path.Combine(root, "log", Rpcs3Patches.LogName), BootLog((Title, Hash)));

        using var server = new FakeQwarkServer { Emulator = true, NoCodePatches = true };
        server.Start();

        // The folder is set, so the search never looks at this PC's own RPCS3.
        var settings = new Settings { AutoReconnect = false, Rpcs3Target = true, Rpcs3Folder = root };
        using var state = new AppState(settings);
        state.Rpcs3Patch.Environment = Windows();
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        var patch = state.Rpcs3Patch;
        Assert.True(await PumpAsync(state, () => patch.Update().State == SaveFilePatchState.Install));
        Assert.Equal(root, patch.Folder!.Root);

        patch.Install(patch.Folder);
        Assert.True(await PumpAsync(state, () => !patch.Installing && patch.Update().State == SaveFilePatchState.RestartGame));
        Assert.Contains(state.Toasts, toast => toast.Kind == ToastKind.Success && toast.Text.Contains("Restart the game"));

        var written = Rpcs3Patches.ReadPatchFile(patch.Folder.PatchFile(Title));
        Assert.Equal(server.SaveFilePatchWords, written.EntryFor(Hash)!.Words);
        Assert.Equal(server.SaveFilePatchBytes, written.EntryFor(Hash)!.Bytes);
        Assert.Equal(Rpcs3Patches.NotesFor(server.Session.QwarkVersion, server.SaveFilePatchStamp), written.Entries[0].Notes);

        // The game starts again in RPCS3 with the patch applied: a new boot, and the helper is in.
        server.SaveFileHelperInstalled = true;
        server.Session = server.Session with { Generation = server.Session.Generation + 1 };
        Assert.True(await PumpAsync(state, () => patch.Update().State == SaveFilePatchState.Active));
        Assert.True(state.SaveFile.Installed);
    }

    [Fact]
    public async Task AFailedInstallSaysWhyInAToast()
    {
        string root = MakeRpcs3();

        // The log says another game booted last, so there is no hash to write the patch for.
        File.WriteAllText(Path.Combine(root, "log", Rpcs3Patches.LogName), BootLog(("NPEA00386", OtherHash)));

        using var server = new FakeQwarkServer { Emulator = true, NoCodePatches = true };
        server.Start();
        using var state = new AppState(new Settings { AutoReconnect = false, Rpcs3Target = true, Rpcs3Folder = root });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        state.Rpcs3Patch.Install(FolderAt(root));
        Assert.True(await PumpAsync(state, () => state.Toasts.Any(toast => toast.Kind == ToastKind.Error)));

        var toast = state.Toasts.Last(t => t.Kind == ToastKind.Error);
        Assert.Contains("was not installed", toast.Text);
        Assert.Contains("NPEA00386", toast.Text);
        Assert.False(File.Exists(FolderAt(root).PatchFile(Title)));
        Assert.False(File.Exists(FolderAt(root).PatchConfigFile));
    }

    [Fact]
    public async Task AQwarkRpcs3FromBeforeThePatchIsNamedAsTooOld()
    {
        string root = MakeRpcs3();

        using var server = new FakeQwarkServer { Emulator = true, NoCodePatches = true, SaveFileUnsupported = true };
        server.Start();
        using var state = new AppState(new Settings { AutoReconnect = false, Rpcs3Target = true, Rpcs3Folder = root });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.Rpcs3Patch.Update().State == SaveFilePatchState.Unavailable));
        Assert.Equal(SaveFilePatchReplyKind.TooOld, state.Rpcs3Patch.Reply.Kind);
        Assert.Contains($"It needs build {QwarkClient.ExpectedQwarkBuild}", state.Rpcs3Patch.LastStatus.Message);

        // Nothing was written, and nothing was asked of the folder beyond looking.
        Assert.Empty(Directory.GetFiles(root, "*.yml", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OnThePs3TargetNothingIsAskedOrRead()
    {
        using var server = new FakeQwarkServer { Emulator = true, NoCodePatches = true };
        server.Start();
        using var state = new AppState(new Settings { AutoReconnect = false });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.Telemetry is not null));
        Assert.Equal(SaveFilePatchState.Hidden, state.Rpcs3Patch.Update().State);
        await Task.Delay(100);
        state.Tick(1f / 60f);
        Assert.DoesNotContain(Opcode.SaveFilePatch, server.RequestLog());
    }

    // ================================================================ the setting

    [Fact]
    public void TheRpcs3FolderSettingRoundTripsAndDefaultsToEmpty()
    {
        Assert.Equal(string.Empty, new Settings().Rpcs3Folder);

        string path = Path.Combine(_root, "settings.json");
        var saved = Settings.Load(path);
        saved.Rpcs3Folder = @"D:\Emulators\rpcs3";
        saved.Save();

        Assert.Contains("\"rpcs3Folder\"", File.ReadAllText(path));
        Assert.Equal(@"D:\Emulators\rpcs3", Settings.Load(path).Rpcs3Folder);
    }
}
