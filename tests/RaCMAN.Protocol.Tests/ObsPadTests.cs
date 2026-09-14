using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The input display as OBS sees it: what <c>/skin.json</c> says, how an event is framed, and the
/// server itself on a port of its own. Nothing here leaves 127.0.0.1.
/// </summary>
public class ObsPadTests : IDisposable
{
    /// <summary>A one-pixel PNG, so the sheet route has real bytes to serve and a size to decode.</summary>
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private const string SkinText = """
        base: 0, 0, 0, 0, 40, 30
        cross: 1, 2, 3, 4, 5, 6
        l3: 4, 4, 100, 0, 8, 8
        l3Press: 4, 4, 108, 0, 8, 8
        r3: 20, 4, 100, 0, 8, 8
        r3Press: 20, 4, 108, 0, 8, 8
        analogPitch: 7
        imageName: skin.png
        """;

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "racman-obspad-" + Guid.NewGuid().ToString("N"));

    public ObsPadTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "skin.txt"), SkinText);
        File.WriteAllBytes(Path.Combine(_folder, "skin.png"), OnePixelPng);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ControllerSkin Skin() => ControllerSkin.Parse("test skin", _folder, SkinText);

    /// <summary>One skin, named the way the request has to name it, and nothing else in the library.</summary>
    private sealed class OneSkin : IPadSkins
    {
        private readonly ControllerSkin _skin;

        public OneSkin(ControllerSkin skin)
        {
            _skin = skin;
        }

        public string Selected => _skin.Name;

        public ControllerSkin? Find(string name) =>
            string.Equals(name, _skin.Name, StringComparison.Ordinal) ? _skin : null;
    }

    // ---------------------------------------------------------------- the skin as the page reads it

    [Fact]
    public void SkinJsonCarriesTheBaseSizeThePitchAndTheSheetsOwnSize()
    {
        using var document = JsonDocument.Parse(ObsPadServer.SkinJson(Skin(), 64, 48));
        var root = document.RootElement;

        Assert.Equal("test skin", root.GetProperty("name").GetString());
        Assert.Equal(40, root.GetProperty("width").GetInt32());
        Assert.Equal(30, root.GetProperty("height").GetInt32());
        Assert.Equal(7, root.GetProperty("analogPitch").GetInt32());
        Assert.Equal(64, root.GetProperty("sheetWidth").GetInt32());
        Assert.Equal(48, root.GetProperty("sheetHeight").GetInt32());
    }

    [Fact]
    public void SkinJsonCarriesEverySpriteRectangleByName()
    {
        using var document = JsonDocument.Parse(ObsPadServer.SkinJson(Skin(), 64, 48));
        var cross = document.RootElement.GetProperty("sprites").GetProperty("cross");

        Assert.Equal(1, cross.GetProperty("dx").GetInt32());
        Assert.Equal(2, cross.GetProperty("dy").GetInt32());
        Assert.Equal(3, cross.GetProperty("sx").GetInt32());
        Assert.Equal(4, cross.GetProperty("sy").GetInt32());
        Assert.Equal(5, cross.GetProperty("w").GetInt32());
        Assert.Equal(6, cross.GetProperty("h").GetInt32());
    }

    [Fact]
    public void SkinJsonNamesTheMaskBitBehindEachSpriteTheSkinHas()
    {
        using var document = JsonDocument.Parse(ObsPadServer.SkinJson(Skin(), 64, 48));
        var buttons = document.RootElement.GetProperty("buttons").EnumerateArray().ToArray();

        var cross = Assert.Single(buttons, entry => entry.GetProperty("sprite").GetString() == "cross");
        Assert.Equal((uint)PadButton.Cross, cross.GetProperty("bit").GetUInt32());

        // This skin draws no d-pad, so the page is never asked to blit a rectangle it has not got.
        Assert.DoesNotContain(buttons, entry => entry.GetProperty("sprite").GetString() == "dpadUp");
    }

    [Fact]
    public void SkinJsonResolvesTheStickNamingQuirkSoThePageNeedNotKnowIt()
    {
        using var document = JsonDocument.Parse(ObsPadServer.SkinJson(Skin(), 64, 48));
        var sticks = document.RootElement.GetProperty("sticks").EnumerateArray().ToArray();
        Assert.Equal(2, sticks.Length);

        // The shipped skins name the highlighted cell "l3" and the idle one "l3Press".
        var left = sticks[0];
        Assert.Equal((uint)PadButton.L3, left.GetProperty("bit").GetUInt32());
        Assert.Equal("l3Press", left.GetProperty("idle").GetString());
        Assert.Equal("l3", left.GetProperty("pressed").GetString());
        Assert.Equal(2, left.GetProperty("x").GetInt32());
        Assert.Equal(3, left.GetProperty("y").GetInt32());

        var right = sticks[1];
        Assert.Equal((uint)PadButton.R3, right.GetProperty("bit").GetUInt32());
        Assert.Equal(0, right.GetProperty("x").GetInt32());
        Assert.Equal(1, right.GetProperty("y").GetInt32());
    }

    // ---------------------------------------------------------------- the event stream's own format

    [Fact]
    public void AnEventIsADataLineAndABlankLine()
    {
        Assert.Equal("data: {\"m\":0}\n\n", ObsPadServer.Frame(null, "{\"m\":0}"));
        Assert.Equal("data: {\"m\":0}\n\n", ObsPadServer.Frame(string.Empty, "{\"m\":0}"));
    }

    [Fact]
    public void ANamedEventCarriesItsNameOnTheLineAbove()
    {
        Assert.Equal("event: skin\ndata: {}\n\n", ObsPadServer.Frame(ObsPadServer.SkinEvent, "{}"));
    }

    [Fact]
    public void ASnapshotIsTheMaskTheFourAxesInWireOrderAndTheLiveFlag()
    {
        var snapshot = new PadSnapshot(1088, 0f, 0f, 0.5f, -0.25f, Live: true);
        Assert.Equal("{\"m\":1088,\"a\":[0,0,0.5,-0.25],\"live\":true}", snapshot.ToJson());

        Assert.Equal("{\"m\":0,\"a\":[0,0,0,0],\"live\":false}", PadSnapshot.Idle.ToJson());
    }

    [Fact]
    public void ASnapshotTakesThePadAndWhetherTheConsoleWasInAGame()
    {
        var session = SessionInfo.Empty with
        {
            State = SessionState.Ingame,
            PadMask = (uint)PadButton.Cross,
            Analog = new[] { 0.25f, -0.5f, 1f, -1f },
        };

        var snapshot = PadSnapshot.From(session);
        Assert.Equal((uint)PadButton.Cross, snapshot.Mask);
        Assert.Equal(0.25f, snapshot.Rx);
        Assert.Equal(-1f, snapshot.Ly);
        Assert.True(snapshot.Live);

        // At the XMB the pad is still reported, and the overlay has to show that it is not live.
        Assert.False(PadSnapshot.From(session with { State = SessionState.Xmb }).Live);
    }

    // ---------------------------------------------------------------- the server itself

    [Fact]
    public async Task TheServerServesTheSkinTheSheetAndAStreamAndGivesThePortBackOnDispose()
    {
        int port = FreePort();
        var server = new ObsPadServer(new OneSkin(Skin()));

        try
        {
            server.Apply(enabled: true, port);
            Assert.Equal(ObsPadState.Serving, server.State);

            using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromSeconds(10),
            };

            using (var parsed = JsonDocument.Parse(await http.GetStringAsync("skin.json")))
            {
                Assert.Equal(40, parsed.RootElement.GetProperty("width").GetInt32());

                // Read off the sheet itself, which skin.txt does not carry.
                Assert.Equal(1, parsed.RootElement.GetProperty("sheetWidth").GetInt32());
            }

            // A name the library does not have is not an error: that source shows the selected skin.
            using (var parsed = JsonDocument.Parse(await http.GetStringAsync("skin.json?skin=../nothing")))
            {
                Assert.Equal("test skin", parsed.RootElement.GetProperty("name").GetString());
            }

            Assert.Equal(OnePixelPng, await http.GetByteArrayAsync("skin.png"));

            // The page itself, out of the assembly.
            Assert.Contains("<canvas", await http.GetStringAsync("pad"), StringComparison.Ordinal);

            using (var events = await http.GetStreamAsync("events"))
            using (var reader = new StreamReader(events))
            {
                // One event on connect, so a source that opens between packets has a pad to draw.
                Assert.Equal(PadSnapshot.Idle.ToJson(), await NextDataAsync(reader));

                server.Publish(new PadSnapshot((uint)PadButton.Cross, 0f, 0f, 0f, 0f, Live: true));
                server.Publish(new PadSnapshot((uint)PadButton.Circle, 0f, 0f, 0.5f, 0f, Live: true));

                Assert.Equal("{\"m\":64,\"a\":[0,0,0,0],\"live\":true}", await NextDataAsync(reader));
                Assert.Equal("{\"m\":32,\"a\":[0,0,0.5,0],\"live\":true}", await NextDataAsync(reader));

                Assert.Equal(1, server.Streams);
            }
        }
        finally
        {
            server.Dispose();
        }

        // The listener let the port go, so the next run (or the next client) can have it.
        var probe = new TcpListener(IPAddress.Loopback, port);
        probe.Start();
        probe.Stop();
    }

    [Fact]
    public void ADisabledServerListensOnNothing()
    {
        using var server = new ObsPadServer(new OneSkin(Skin()));
        server.Apply(enabled: false, FreePort());

        Assert.Equal(ObsPadState.Off, server.State);

        // Nothing is watching, so a packet is dropped where it arrives.
        server.Publish(PadSnapshot.Idle);
        Assert.Equal(0, server.Streams);
    }

    [Fact]
    public void ThePortInUseIsReportedAndLeavesTheClientRunning()
    {
        int port = FreePort();
        var held = new TcpListener(IPAddress.Loopback, port);
        held.Start();

        try
        {
            using var server = new ObsPadServer(new OneSkin(Skin()));
            server.Apply(enabled: true, port);

            Assert.Equal(ObsPadState.Failed, server.State);
            Assert.False(string.IsNullOrEmpty(server.Problem));

            // The setting moving is the retry, and a free port is all it takes.
            server.Apply(enabled: true, FreePort());
            Assert.Equal(ObsPadState.Serving, server.State);
        }
        finally
        {
            held.Stop();
        }
    }

    /// <summary>The next <c>data:</c> line, past any keep-alive comment, or a failed test.</summary>
    private static async Task<string> NextDataAsync(StreamReader reader)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(line);

            if (line!.StartsWith("data:", StringComparison.Ordinal)) return line["data:".Length..].Trim();
        }
    }

    /// <summary>A port nothing holds, asked of the operating system rather than guessed.</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
