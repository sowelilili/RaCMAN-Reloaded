using System.Text;
using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The Lua console (2026-10-31): its scripts' files, what Run sends through LUA_EXEC and what it
/// refuses to, and the colours the editor gives a line. The fake console listens on loopback only.
/// </summary>
public class LuaConsoleTests
{
    private static string Folder(string name)
    {
        string folder = Path.Combine(TestDataFolder.Root, "lua-tests", name, Guid.NewGuid().ToString("N"));
        return folder;
    }

    // ---------------------------------------------------------------- the files

    [Fact]
    public void ScriptsLiveInTheDataFoldersLuaFolder()
    {
        Assert.Equal(Path.Combine(AppPaths.Root, "lua"), AppPaths.LuaScripts);
        Assert.Equal(".lua", LuaScriptStore.Extension);
    }

    [Fact]
    public void ASavedScriptLoadsBackAsItWasTyped()
    {
        var store = new LuaScriptStore(Folder("save"));
        Assert.Empty(store.List());

        const string script = "-- give the RYNO\nlocal n = 1\nreturn \"héllo\" .. n\n";
        string path = store.Save("ryno", script);
        Assert.Equal(Path.Combine(store.Folder, "ryno.lua"), path);
        Assert.True(File.Exists(path));
        Assert.Equal(script, store.Load("ryno"));
        Assert.Equal(new[] { "ryno" }, store.List());

        // UTF-8 with no byte order mark, in the platform's line endings.
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal(script.Replace("\n", Environment.NewLine), Encoding.UTF8.GetString(bytes));

        // Saving again replaces it, and the listing is alphabetical, without the extension.
        store.Save("ryno.lua", "return 2");
        store.Save("Bolts", "return 1");
        Assert.Equal("return 2", store.Load("ryno"));
        Assert.Equal(new[] { "Bolts", "ryno" }, store.List());
    }

    [Fact]
    public void AnyLuaFileOpensWithLfEndingsAndNoMark()
    {
        string folder = Folder("open");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "from-elsewhere.lua");
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("a = 1\r\nb = 2\rreturn a + b")).ToArray());

        Assert.Equal("a = 1\nb = 2\nreturn a + b", LuaScriptStore.Read(path));

        // And a script written anywhere comes back the same.
        string other = Path.Combine(folder, "copy.lua");
        LuaScriptStore.Write(other, "x\ny");
        Assert.Equal("x\ny", LuaScriptStore.Read(other));
        Assert.Throws<FileNotFoundException>(() => LuaScriptStore.Read(Path.Combine(folder, "none.lua")));
    }

    [Fact]
    public void ANameIsMadeSafeForAFile()
    {
        Assert.Equal("ryno", LuaScriptStore.CleanName("  ryno.lua "));
        Assert.Equal("ryno", LuaScriptStore.CleanName("ryno.LUA"));
        Assert.Equal("a_b_c", LuaScriptStore.CleanName("a/b\\c"));
        Assert.Equal("what_", LuaScriptStore.CleanName("what?"));
        Assert.Equal(string.Empty, LuaScriptStore.CleanName("   "));
        Assert.Equal(string.Empty, LuaScriptStore.CleanName(".."));
        Assert.Equal(string.Empty, LuaScriptStore.CleanName(null));

        var store = new LuaScriptStore(Folder("names"));
        Assert.Throws<ArgumentException>(() => store.FileFor(" .lua"));
        Assert.Equal(Path.Combine(store.Folder, "a_b.lua"), store.FileFor("a:b"));

        // Nothing can be saved outside the folder.
        Assert.Equal(store.Folder, Path.GetDirectoryName(store.FileFor("../../escape")));
    }

    // ---------------------------------------------------------------- what Run sends

    [Fact]
    public void RunChecksTheScriptAgainstLuaExecsLimit()
    {
        Assert.Equal((null, "There is no Lua to run."), LuaBox.Prepare("  \n\t "));
        Assert.Equal(("return 1", (string?)null), LuaBox.Prepare("return 1"));

        string full = "--" + new string('x', LuaExecRequest.MaxSourceBytes - 2);
        Assert.Equal(full, LuaBox.Prepare(full).Source);

        // The limit is the request's bytes, so a character that is two bytes in UTF-8 counts twice.
        string over = "--" + new string('é', LuaExecRequest.MaxSourceBytes / 2);
        var (source, problem) = LuaBox.Prepare(over);
        Assert.Null(source);
        Assert.Contains($"{Encoding.UTF8.GetByteCount(over)} bytes", problem);
        Assert.Contains("your console runs at most 8192 bytes", problem, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(8192, LuaExecRequest.MaxSourceBytes);
    }

    private static async Task<QwarkClient> ConnectAsync(FakeQwarkServer server)
    {
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return client;
    }

    [Fact]
    public async Task RunSendsTheWholeScriptAsOneLuaExec()
    {
        using var server = new FakeQwarkServer();
        server.UseTod();
        server.Start();
        using var client = await ConnectAsync(server);

        const string script = "-- two lines\nlocal v = _VERSION\nreturn v\n";
        var ok = await LuaBox.RunAsync(client, script, rpcs3: false);
        Assert.Equal(LuaBox.LineKind.Ok, ok.Kind);
        Assert.StartsWith("Last run: OK", ok.Line);
        Assert.Equal(FakeQwarkServer.LuaVersion, ok.Output);
        Assert.Equal(new[] { script }, server.LuaSources);

        // A Lua error is the game's answer, shown with its message.
        var error = await LuaBox.RunAsync(client, "error(\"no\")", rpcs3: false);
        Assert.Equal(LuaBox.LineKind.Error, error.Kind);
        Assert.NotEmpty(error.Output);

        // A script past the limit, or with nothing in it, never reaches the console.
        int sent = server.LuaSources.Count;
        var tooLong = await LuaBox.RunAsync(client, new string('x', LuaExecRequest.MaxSourceBytes + 1), rpcs3: false);
        Assert.Equal(LuaBox.LineKind.Error, tooLong.Kind);
        Assert.Contains("8193 bytes", tooLong.Line);
        var empty = await LuaBox.RunAsync(client, "\n\n", rpcs3: false);
        Assert.Equal(LuaBox.LineKind.Error, empty.Kind);
        Assert.Equal(sent, server.LuaSources.Count);
        Assert.Equal(2, server.RequestLog().Count(op => op == Opcode.LuaExec));
    }

    [Fact]
    public async Task ARefusedRunSaysWhatToDo()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var client = await ConnectAsync(server);

        // A RaC1 has no Lua: the console answers UNSUPPORTED, and the line says so.
        var refused = await LuaBox.RunAsync(client, "return 1", rpcs3: false);
        Assert.Equal(LuaBox.LineKind.Error, refused.Kind);
        Assert.Contains("UNSUPPORTED", refused.Line);
        Assert.Contains("no Lua helper", refused.Line);
    }

    [Fact]
    public void TheStarterScriptFitsAndSaysYourConsole()
    {
        Assert.NotNull(LuaBox.Prepare(LuaBox.StarterSource).Source);
        Assert.Contains("your console", LuaBox.StarterSource);
    }

    // ---------------------------------------------------------------- the colours

    private static List<(string Text, LuaSyntax.Kind Kind)> Runs(string line, ref LuaSyntax.State state) =>
        LuaSyntax.Tokens(line, ref state).Select(run => (line.Substring(run.Start, run.Length), run.Kind)).ToList();

    [Fact]
    public void ALineIsColouredByWhatItsWordsAre()
    {
        var state = LuaSyntax.State.Plain;
        var runs = Runs("local n = tonumber(\"12\") + 0x1F -- the count", ref state);
        Assert.Equal(new (string, LuaSyntax.Kind)[]
        {
            ("local", LuaSyntax.Kind.Keyword),
            ("n", LuaSyntax.Kind.Code),
            ("=", LuaSyntax.Kind.Code),
            ("tonumber", LuaSyntax.Kind.Builtin),
            ("(", LuaSyntax.Kind.Code),
            ("\"12\"", LuaSyntax.Kind.String),
            (")", LuaSyntax.Kind.Code),
            ("+", LuaSyntax.Kind.Code),
            ("0x1F", LuaSyntax.Kind.Number),
            ("-- the count", LuaSyntax.Kind.Comment),
        }, runs);
        Assert.Equal(LuaSyntax.State.Plain, state);

        // A field of a table is not the global of the same name; a concatenation is no field.
        runs = Runs("t.print(1.5e-3) .. print 'it\\'s'", ref state);
        Assert.Contains(("print", LuaSyntax.Kind.Code), runs);
        Assert.Contains(("print", LuaSyntax.Kind.Builtin), runs);
        Assert.Contains(("1.5e-3", LuaSyntax.Kind.Number), runs);
        Assert.Contains(("'it\\'s'", LuaSyntax.Kind.String), runs);
    }

    [Fact]
    public void LongCommentsAndStringsRunOverLines()
    {
        var lines = new[] { "x = 1 --[[ a long", "comment ]] y = [==[ a", "string ]] still ]==] z", "w" };
        var states = LuaSyntax.LineStates(lines);
        Assert.Equal(LuaSyntax.State.Plain, states[0]);
        Assert.Equal(new LuaSyntax.State(LuaSyntax.Kind.Comment, 0), states[1]);
        Assert.Equal(new LuaSyntax.State(LuaSyntax.Kind.String, 2), states[2]);
        Assert.Equal(LuaSyntax.State.Plain, states[3]);

        var state = states[1];
        var runs = Runs(lines[1], ref state);
        Assert.Equal(("comment ]]", LuaSyntax.Kind.Comment), runs[0]);
        Assert.Equal(("y", LuaSyntax.Kind.Code), runs[1]);
        Assert.Equal(("[==[ a", LuaSyntax.Kind.String), runs[^1]);

        state = states[2];
        runs = Runs(lines[2], ref state);
        Assert.Equal(("string ]] still ]==]", LuaSyntax.Kind.String), runs[0]);
        Assert.Equal(("z", LuaSyntax.Kind.Code), runs[1]);

        // An unclosed quote ends with its line, as Lua's own reader stops there.
        state = LuaSyntax.State.Plain;
        runs = Runs("s = \"open", ref state);
        Assert.Equal(("\"open", LuaSyntax.Kind.String), runs[^1]);
        Assert.Equal(LuaSyntax.State.Plain, state);
    }
}
