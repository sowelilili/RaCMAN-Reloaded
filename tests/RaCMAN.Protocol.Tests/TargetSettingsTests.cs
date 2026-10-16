using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Each target's own settings. Up to 1.2.2 the console's address and the RPCS3 settings were flat
/// keys side by side, and the RPCS3 Connect wrote the helper's 127.0.0.1 over the console's address,
/// so the console opened on 127.0.0.1 after any session that ended under RPCS3. Every console
/// address here is a documentation address, and every connection goes to the in-process fake.
/// </summary>
public sealed class TargetSettingsTests : IDisposable
{
    private const string ConsoleIp = "192.0.2.10";

    private readonly string _folder;

    public TargetSettingsTests()
    {
        _folder = Path.Combine(TestDataFolder.Root, "targets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }

    private string SettingsPath => Path.Combine(_folder, AppPaths.SettingsFileName);

    // ---------------------------------------------------------------- the bug

    [Fact]
    public async Task ClosingWhileConnectedToRpcs3KeepsTheConsoleAddress()
    {
        using var server = new FakeQwarkServer { Emulator = true, NoCodePatches = true };
        server.Start();

        // The console was used last time; this time it is RPCS3, with the fake serving the helper's port.
        var settings = Settings.Load(SettingsPath);
        settings.ObsPadEnabled = false;
        settings.Ps3.Host = ConsoleIp;
        UseFakeAsTheHelper(settings, server);
        settings.Rpcs3Target = true;
        settings.Save();

        using (var state = new AppState(settings))
        {
            await ConnectToRpcs3Async(state, server);

            // Connecting wrote nothing to the console's section...
            Assert.Equal(ConsoleIp, Settings.Load(SettingsPath).Ps3.Host);

            // ...and neither does closing while connected, which is the save the client makes on
            // its way out.
            Assert.True(state.Connected);
            settings.Save();
        }

        var reopened = Settings.Load(SettingsPath);
        Assert.True(reopened.Rpcs3Target);
        Assert.Equal(ConsoleIp, reopened.Ps3.Host);

        // The copy an older client reads says the same.
        Assert.Equal(ConsoleIp, (string?)Json()["lastHost"]);
    }

    [Fact]
    public async Task TheRpcs3TargetNeverSavesLoopbackAsTheConsoleAddress()
    {
        using var server = new FakeQwarkServer { Emulator = true, NoCodePatches = true };
        server.Start();

        // A first start: nobody has typed a console address yet.
        var settings = Settings.Load(SettingsPath);
        settings.ObsPadEnabled = false;
        UseFakeAsTheHelper(settings, server);
        settings.Save();

        using (var state = new AppState(settings))
        {
            // Every way RPCS3 is reached: the radio button, the Connect button, and --connect.
            state.SwitchTarget(rpcs3: true);
            await ConnectToRpcs3Async(state, server);

            ConnectionPanel.ConnectOnStart(state, ConnectionPanel.LocalHost);
            Assert.True(await PumpAsync(state, () => state.InFlight == 0 && state.Connected));
            Assert.Equal(server.Port, state.Client.Port);

            // Back to the console, and closed.
            state.SwitchTarget(rpcs3: false);
            Assert.True(await PumpAsync(state, () => state.InFlight == 0 && !state.Client.WantsConnection));
            settings.Save();
        }

        var json = Json();
        Assert.Equal(Ps3Settings.DefaultHost, (string?)json["ps3"]!["host"]);
        Assert.Equal(Ps3Settings.DefaultHost, (string?)json["lastHost"]);
        Assert.DoesNotContain(ConnectionPanel.LocalHost, json["ps3"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(Ps3Settings.DefaultHost, Settings.Load(SettingsPath).Ps3.Host);
    }

    /// <summary>
    /// --connect on the console still remembers the host it was given, which is the console's own
    /// address; the RPCS3 section is not touched, and the console's port is the one connected to.
    /// </summary>
    [Fact]
    public async Task ConnectingTheConsoleRemembersItsHostAndUsesItsPort()
    {
        using var server = new FakeQwarkServer();
        server.Start();

        var settings = Settings.Load(SettingsPath);
        settings.ObsPadEnabled = false;
        settings.Ps3.Port = server.Port;
        settings.Ps3.StandaloneConnection = true;
        settings.Ps3.AutoReconnect = false;
        settings.Rpcs3.Folder = _folder;
        settings.Save();
        string rpcs3Before = Section("rpcs3");

        using (var state = new AppState(settings))
        {
            ConnectionPanel.ConnectOnStart(state, ConnectionPanel.LocalHost);
            Assert.True(await PumpAsync(state, () => state.InFlight == 0 && state.Connected));
            Assert.Equal(server.Port, state.Client.Port);
            settings.Save();
        }

        Assert.Equal(ConnectionPanel.LocalHost, Settings.Load(SettingsPath).Ps3.Host);
        Assert.Equal(rpcs3Before, Section("rpcs3"));
    }

    // ---------------------------------------------------------------- switching

    [Fact]
    public void SwitchingTargetsBackAndForthKeepsBothSets()
    {
        var settings = Settings.Load(SettingsPath);
        settings.ObsPadEnabled = false;
        settings.Ps3.Host = ConsoleIp;
        settings.Ps3.Port = 9700;
        settings.Ps3.StandaloneConnection = true;
        settings.Ps3.WebManSlot = 2;
        settings.Ps3.SprxPath = @"C:\builds\qwark.sprx";
        settings.Ps3.AutoReconnect = false;
        settings.Rpcs3.PinePort = 28099;
        settings.Rpcs3.HelperPort = 9800;
        settings.Rpcs3.Folder = _folder;
        settings.Rpcs3.QwarkPath = @"D:\qwark\qwark-rpcs3.exe";
        settings.Rpcs3.AutoReconnect = true;
        settings.Save();

        string ps3Before = Section("ps3");
        string rpcs3Before = Section("rpcs3");

        using var state = new AppState(settings);
        Assert.False(state.Client.AutoReconnect);

        foreach (bool rpcs3 in new[] { true, false, true, true, false, true, false })
        {
            state.SwitchTarget(rpcs3);

            Assert.Equal(rpcs3, Settings.Load(SettingsPath).Rpcs3Target);
            Assert.Equal(ps3Before, Section("ps3"));
            Assert.Equal(rpcs3Before, Section("rpcs3"));

            // The client takes up the reconnect switch of the target it now serves.
            Assert.Equal(rpcs3, state.Client.AutoReconnect);
            Assert.Equal(rpcs3, settings.AutoReconnect);
        }

        var reopened = Settings.Load(SettingsPath);
        Assert.Equal(ConsoleIp, reopened.Ps3.Host);
        Assert.Equal(9700, reopened.Ps3.Port);
        Assert.True(reopened.Ps3.StandaloneConnection);
        Assert.Equal(2, reopened.Ps3.WebManSlot);
        Assert.Equal(@"C:\builds\qwark.sprx", reopened.Ps3.SprxPath);
        Assert.False(reopened.Ps3.AutoReconnect);
        Assert.Equal(28099, reopened.Rpcs3.PinePort);
        Assert.Equal(9800, reopened.Rpcs3.HelperPort);
        Assert.Equal(_folder, reopened.Rpcs3.Folder);
        Assert.Equal(@"D:\qwark\qwark-rpcs3.exe", reopened.Rpcs3.QwarkPath);
        Assert.True(reopened.Rpcs3.AutoReconnect);
    }

    /// <summary>The helper's port is the RPCS3 section's, and the helper is told about it only when it moved.</summary>
    [Fact]
    public void TheHelperServesThePortItsSectionNames()
    {
        var settings = new Settings { ObsPadEnabled = false, Rpcs3 = { HelperPort = 9800 } };
        using var state = new AppState(settings);

        Assert.Equal(9800, state.Rpcs3.QwarkPort);
        Assert.Equal(new[] { "--pine-port", "28012", "--port", "9800" }, Rpcs3Host.Arguments(28012, 9800, null));
        Assert.Equal(new[] { "--pine-port", "28012", "--root", "R" }, Rpcs3Host.Arguments(28012, QwarkClient.DefaultPort, "R"));
    }

    // ---------------------------------------------------------------- migration

    [Fact]
    public void AFileFrom122LoadsIntoTheTwoSectionsWithNothingLost()
    {
        File.WriteAllText(SettingsPath, V122File(ConsoleIp, "ps3"));

        var loaded = Settings.Load(SettingsPath);

        Assert.Equal(ConsoleIp, loaded.Ps3.Host);
        Assert.Equal(QwarkClient.DefaultPort, loaded.Ps3.Port);
        Assert.True(loaded.Ps3.StandaloneConnection);
        Assert.Equal(3, loaded.Ps3.WebManSlot);
        Assert.Equal(@"C:\builds\qwark.sprx", loaded.Ps3.SprxPath);
        Assert.False(loaded.Ps3.AutoReconnect);

        Assert.Equal(28099, loaded.Rpcs3.PinePort);
        Assert.Equal(QwarkClient.DefaultPort, loaded.Rpcs3.HelperPort);
        Assert.Equal(@"D:\qwark\qwark-rpcs3.exe", loaded.Rpcs3.QwarkPath);
        Assert.Equal(@"D:\Emulators\rpcs3", loaded.Rpcs3.Folder);
        Assert.False(loaded.Rpcs3.AutoReconnect);

        // The shared preferences are where they were.
        Assert.False(loaded.Rpcs3Target);
        Assert.Equal("dark", loaded.Theme);
        Assert.True(loaded.DebugInfo);
        Assert.Equal(2.5f, loaded.TableRefreshSeconds);
        Assert.False(loaded.ObsPadEnabled);
        Assert.Equal(1180, loaded.WindowWidth);
        Assert.False(loaded.Updates.Check);
        Assert.Equal("any%", loaded.SaveFileCategoryFor("NPEA00385"));

        // Saved once, the file has the two sections, and it reads back the same.
        loaded.Save();
        var json = Json();
        Assert.Equal(ConsoleIp, (string?)json["ps3"]!["host"]);
        Assert.Equal("standalone", (string?)json["ps3"]!["connectionMode"]);
        Assert.Equal(28099, (int)json["rpcs3"]!["pinePort"]!);
        Assert.Equal(@"D:\Emulators\rpcs3", (string?)json["rpcs3"]!["folder"]);

        var again = Settings.Load(SettingsPath);
        Assert.Equal(ConsoleIp, again.Ps3.Host);
        Assert.Equal(3, again.Ps3.WebManSlot);
        Assert.Equal(@"D:\Emulators\rpcs3", again.Rpcs3.Folder);
        Assert.Equal(28099, again.Rpcs3.PinePort);
        Assert.False(again.Rpcs3.AutoReconnect);
    }

    /// <summary>
    /// A file 1.2.2 already broke: RPCS3 was the last target and the console's address is the
    /// helper's. Taken as it is, the console target would reach the RPCS3 helper on this PC, which
    /// answers on the same port. Nothing in the file remembers the address that was there, so the
    /// box goes back to its default and the user is asked for it; the rest of the file is intact.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("::1")]
    [InlineData("127.0.0.2")]
    public void AFileThe122BugAlreadyDamagedAsksForTheConsolesAddressAgain(string loopback)
    {
        File.WriteAllText(SettingsPath, V122File(loopback, "rpcs3"));

        var loaded = Settings.Load(SettingsPath);

        Assert.True(loaded.Rpcs3Target);
        Assert.Equal(Ps3Settings.DefaultHost, loaded.Ps3.Host);
        Assert.Equal(3, loaded.Ps3.WebManSlot);
        Assert.True(loaded.Ps3.StandaloneConnection);
        Assert.Equal(@"D:\Emulators\rpcs3", loaded.Rpcs3.Folder);
        Assert.Equal(28099, loaded.Rpcs3.PinePort);

        // Once the file has its sections, nothing takes the address away again.
        loaded.Save();
        Assert.Equal(Ps3Settings.DefaultHost, Settings.Load(SettingsPath).Ps3.Host);
    }

    /// <summary>
    /// A loopback address under the console target is a console target pointed at the qwark
    /// simulator on purpose, and so is an ordinary address under either target.
    /// </summary>
    [Fact]
    public void ALoopbackAddressTheConsoleTargetChoseIsKept()
    {
        File.WriteAllText(SettingsPath, V122File(ConnectionPanel.LocalHost, "ps3"));
        Assert.Equal(ConnectionPanel.LocalHost, Settings.Load(SettingsPath).Ps3.Host);

        File.WriteAllText(SettingsPath, V122File(ConsoleIp, "rpcs3"));
        Assert.Equal(ConsoleIp, Settings.Load(SettingsPath).Ps3.Host);

        Assert.False(Settings.IsLoopback(ConsoleIp));
        Assert.False(Settings.IsLoopback(Ps3Settings.DefaultHost));
        Assert.False(Settings.IsLoopback(null));
    }

    /// <summary>A file that has the sections reads them, whatever the copies for older builds say.</summary>
    [Fact]
    public void OnceAFileHasTheSectionsTheFlatKeysAreOnlyCopies()
    {
        File.WriteAllText(SettingsPath, """
            {
              "target": "rpcs3",
              "ps3": { "host": "192.0.2.10", "autoReconnect": false },
              "rpcs3": { "pinePort": 28099 },
              "lastHost": "127.0.0.1",
              "autoReconnect": true,
              "rpcs3PinePort": 28555
            }
            """);

        var loaded = Settings.Load(SettingsPath);

        Assert.Equal(ConsoleIp, loaded.Ps3.Host);
        Assert.False(loaded.Ps3.AutoReconnect);
        Assert.Equal(28099, loaded.Rpcs3.PinePort);
        Assert.True(loaded.Rpcs3.AutoReconnect);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void AHandEditedSectionThatMakesNoSenseFallsBackToTheDefaults(int port)
    {
        File.WriteAllText(SettingsPath, $$"""
            {
              "ps3": { "host": null, "port": {{port}}, "sprxPath": null },
              "rpcs3": { "pinePort": {{port}}, "helperPort": {{port}}, "folder": null, "qwarkPath": null }
            }
            """);

        var loaded = Settings.Load(SettingsPath);

        Assert.Equal(Ps3Settings.DefaultHost, loaded.Ps3.Host);
        Assert.Equal(QwarkClient.DefaultPort, loaded.Ps3.Port);
        Assert.Equal(Ps3Settings.DefaultSprxPath, loaded.Ps3.SprxPath);
        Assert.Equal(Rpcs3Host.DefaultPinePort, loaded.Rpcs3.PinePort);
        Assert.Equal(QwarkClient.DefaultPort, loaded.Rpcs3.HelperPort);
        Assert.Equal(string.Empty, loaded.Rpcs3.Folder);
        Assert.Equal(string.Empty, loaded.Rpcs3.QwarkPath);
    }

    // ---------------------------------------------------------------- an older client on the same folder

    /// <summary>
    /// An older client reads this file with the serializer's defaults, which skip a key they do not
    /// know, and finds both targets' settings where it always looked. What it saves drops the
    /// sections it has never heard of, and that file comes back here with what the flat keys held.
    /// Three things have no flat key and do not come back: the console's port, the helper's port,
    /// and the reconnect switch of the target that was not chosen, which takes the chosen one's.
    /// </summary>
    [Fact]
    public void AnOlderClientReadsTheNewFileAndWhatItSavesComesBack()
    {
        var settings = Settings.Load(SettingsPath);
        settings.Ps3.Host = ConsoleIp;
        settings.Ps3.WebManSlot = 2;
        settings.Ps3.Port = 9700;
        settings.Ps3.AutoReconnect = true;
        settings.Rpcs3.PinePort = 28099;
        settings.Rpcs3.HelperPort = 9701;
        settings.Rpcs3.Folder = @"D:\Emulators\rpcs3";
        settings.Rpcs3.AutoReconnect = false;
        settings.Rpcs3Target = true;
        settings.Save();

        var old = JsonSerializer.Deserialize<V122Keys>(File.ReadAllText(SettingsPath))!;

        Assert.Equal(ConsoleIp, old.LastHost);
        Assert.Equal("rpcs3", old.Target);
        Assert.Equal(2, old.WebManSlot);
        Assert.Equal(28099, old.Rpcs3PinePort);
        Assert.Equal(@"D:\Emulators\rpcs3", old.Rpcs3Folder);

        // An older build has one reconnect switch and applies it to whichever target it opens on.
        Assert.False(old.AutoReconnect);

        old.LastHost = "192.0.2.20";
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(old));

        var back = Settings.Load(SettingsPath);
        Assert.Equal("192.0.2.20", back.Ps3.Host);
        Assert.Equal(2, back.Ps3.WebManSlot);
        Assert.Equal(28099, back.Rpcs3.PinePort);
        Assert.Equal(@"D:\Emulators\rpcs3", back.Rpcs3.Folder);
        Assert.True(back.Rpcs3Target);
        Assert.False(back.Rpcs3.AutoReconnect);

        // What an older build's save loses.
        Assert.Equal(QwarkClient.DefaultPort, back.Ps3.Port);
        Assert.Equal(QwarkClient.DefaultPort, back.Rpcs3.HelperPort);
        Assert.False(back.Ps3.AutoReconnect);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The fake is the helper: its port is the RPCS3 section's, and nothing else of RPCS3's is real.</summary>
    private void UseFakeAsTheHelper(Settings settings, FakeQwarkServer server)
    {
        settings.Rpcs3.HelperPort = server.Port;
        settings.Rpcs3.Folder = _folder;
        settings.Rpcs3.AutoReconnect = false;

        // Should a helper ever be started by mistake, it is pointed at a port no RPCS3 is serving.
        settings.Rpcs3.PinePort = FreePort();
    }

    /// <summary>The panel's Connect under RPCS3, which finds the fake on the helper's port and takes it.</summary>
    private static async Task ConnectToRpcs3Async(AppState state, FakeQwarkServer server)
    {
        // Checked before anything can start a helper: the fake has to be seen on that port.
        Assert.True(Rpcs3Host.PortInUse(server.Port));

        ConnectionPanel.ConnectToRpcs3(state);

        Assert.True(state.Rpcs3.Adopted);
        Assert.False(state.Rpcs3.IsRunning);
        Assert.True(await PumpAsync(state, () => state.InFlight == 0 && state.Connected));
        Assert.Equal(server.Port, state.Client.Port);
    }

    private JsonNode Json() => JsonNode.Parse(File.ReadAllText(SettingsPath))!;

    private string Section(string name) => Json()[name]!.ToJsonString();

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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

    /// <summary>A settings file as 1.2.2 wrote it: every key the two targets used, and some shared ones.</summary>
    private static string V122File(string lastHost, string target) => $$"""
        {
          "lastHost": "{{lastHost}}",
          "target": "{{target}}",
          "connectionMode": "standalone",
          "rpcs3PinePort": 28099,
          "rpcs3QwarkPath": "D:\\qwark\\qwark-rpcs3.exe",
          "rpcs3Folder": "D:\\Emulators\\rpcs3",
          "autoReconnect": false,
          "theme": "dark",
          "debugInfo": true,
          "tableRefreshSeconds": 2.5,
          "webManSlot": 3,
          "sprxPath": "C:\\builds\\qwark.sprx",
          "modsPath": "mods",
          "saveFilesPath": "savefiles",
          "mirrorSaveFiles": true,
          "saveFileCategory": { "NPEA00385": "any%" },
          "obsPadEnabled": false,
          "obsPadPort": 9674,
          "windowWidth": 1180,
          "windowHeight": 742,
          "firewallOffered": true,
          "autosplit": { "enabled": false, "autoConnect": false, "host": "127.0.0.1", "port": 16834, "games": {} },
          "updates": { "check": false, "lastCheckUtc": null }
        }
        """;

    /// <summary>The keys 1.2.2's settings class read for the two targets, as that class declared them.</summary>
    private sealed class V122Keys
    {
        [JsonPropertyName("lastHost")]
        public string LastHost { get; set; } = "192.168.1.";

        [JsonPropertyName("target")]
        public string Target { get; set; } = "ps3";

        [JsonPropertyName("connectionMode")]
        public string ConnectionMode { get; set; } = "webman";

        [JsonPropertyName("rpcs3PinePort")]
        public int Rpcs3PinePort { get; set; } = 28012;

        [JsonPropertyName("rpcs3QwarkPath")]
        public string Rpcs3QwarkPath { get; set; } = string.Empty;

        [JsonPropertyName("rpcs3Folder")]
        public string Rpcs3Folder { get; set; } = string.Empty;

        [JsonPropertyName("autoReconnect")]
        public bool AutoReconnect { get; set; } = true;

        [JsonPropertyName("webManSlot")]
        public int WebManSlot { get; set; } = 5;

        [JsonPropertyName("sprxPath")]
        public string SprxPath { get; set; } = "qwark.sprx";
    }
}
