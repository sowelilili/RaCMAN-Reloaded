using System.Buffers.Binary;
using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The two pieces of client-side view data that live in files rather than in code: the
/// controller skins and the per-game moby row layouts.
/// </summary>
public class AppDataTests
{
    private const string SampleSkin = """
        # Add in numbers following the structure below.
        # Name: drawX, drawY, spriteX, spriteY, spriteWidth, spriteHeight

        # Base controller image
        base: 0, 0, 0, 0, 800, 558

        # Analog sticks
        r3: 469, 328, 106, 627, 105, 105
        r3Press: 469, 328, 0, 627, 105, 105
        l3: 210, 328, 106, 627, 105, 105
        l3Press: 210, 328, 0, 627, 105, 105

        # Add pitch that should be used for the analog stick.
        analogPitch: 32

        # D-Pad buttons
        dpadLeft: 74, 244, 0, 560, 52, 38
        dpadRight: 162, 244, 130, 560, 52, 38
        dpadDown: 124, 276, 53, 560, 38, 52
        dpadUp: 124, 198, 92, 560, 38, 52

        # Face buttons
        cross: 609, 303, 389, 560, 62, 62
        circle: 680, 232, 326, 560, 62, 62
        triangle: 609, 161, 263, 560, 62, 62
        square: 538, 232, 200, 560, 62, 62

        # Pause buttons
        select: 291, 252, 460, 561, 38, 20
        start: 459, 252, 499, 561, 37, 20

        # Shoulder buttons
        r1: 596, 73, 458, 654, 89, 27
        l1: 99, 73, 458, 654, 89, 27
        l2: 99, 0, 460, 586, 86, 65
        r2: 599, 0, 460, 586, 86, 65

        # Add in name of the image you used. Needs to be in the same folder as the .txt
        imageName: redGen1.png
        """;

    // ---------------------------------------------------------------- skins

    [Fact]
    public void SkinTextParsesSpritesPitchAndImageName()
    {
        var skin = ControllerSkin.Parse("DS3 Test", @"C:\skins\DS3 Test", SampleSkin);

        Assert.Equal("DS3 Test", skin.Name);
        Assert.Equal("redGen1.png", skin.ImageFileName);
        Assert.Equal(32, skin.AnalogPitch);
        Assert.Equal(new SkinSprite(0, 0, 0, 0, 800, 558), skin.Base);
        Assert.Equal(new SkinSprite(124, 276, 53, 560, 38, 52), skin.Sprites["dpadDown"]);
        Assert.Empty(skin.Missing);
        Assert.Equal(Path.Combine(@"C:\skins\DS3 Test", "redGen1.png"), skin.ImagePath);
    }

    [Fact]
    public void SkinTextIgnoresCommentsBlanksAndShortLines()
    {
        var skin = ControllerSkin.Parse("t", "d", "# comment\n\n\nbase: 1, 2, 3, 4, 5, 6\nbroken: 1, 2\nnope\ncross: 1, 2, 3, 4, 5, 6, 7\n");

        Assert.Equal(new SkinSprite(1, 2, 3, 4, 5, 6), skin.Base);
        Assert.False(skin.Sprites.ContainsKey("broken"));
        Assert.False(skin.Sprites.ContainsKey("nope"));

        // A seventh number is ignored rather than rejecting the line.
        Assert.Equal(new SkinSprite(1, 2, 3, 4, 5, 6), skin.Sprites["cross"]);
    }

    [Fact]
    public void SkinTextDefaultsPitchAndImageAndReportsMissingSprites()
    {
        var skin = ControllerSkin.Parse("t", "d", "base: 0, 0, 0, 0, 10, 10\n");

        Assert.Equal(32, skin.AnalogPitch);
        Assert.Equal("skin.png", skin.ImageFileName);
        Assert.Contains("l3", skin.Missing);
        Assert.Contains("start", skin.Missing);
        Assert.DoesNotContain("base", skin.Missing);
    }

    [Fact]
    public void SkinTextWithoutABaseIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => ControllerSkin.Parse("t", "d", "cross: 1, 2, 3, 4, 5, 6\n"));
    }

    [Fact]
    public void StickSpriteFollowsTheShippedSkinsNaming()
    {
        var skin = ControllerSkin.Parse("t", "d", SampleSkin);

        // The shipped skins name the highlighted cell "r3" and the idle cell "r3Press".
        Assert.True(skin.TryGetStick("r3", pressed: true, out var pressed));
        Assert.True(skin.TryGetStick("r3", pressed: false, out var idle));
        Assert.Equal(106, pressed.SpriteX);
        Assert.Equal(0, idle.SpriteX);
    }

    [Fact]
    public void EveryShippedSkinParsesAndPointsAtAnExistingImage()
    {
        string? root = SkinLibrary.FindRoot();
        Assert.NotNull(root);

        var names = SkinLibrary.List(root);
        Assert.Equal(21, names.Length);

        foreach (var name in names)
        {
            var skin = SkinLibrary.Load(name, root);
            Assert.True(File.Exists(skin.ImagePath), $"{name} names a missing image: {skin.ImagePath}");
            Assert.True(skin.AnalogPitch > 0, $"{name} has no analog pitch");
            Assert.Empty(skin.Missing);
        }
    }

    // ---------------------------------------------------------------- moby layouts

    private static string MobyDataFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "src", "RaCMAN.App", "data", "moby");
            if (Directory.Exists(candidate)) return candidate;
        }

        // Fall back to whatever the build copied next to the test binary.
        return Path.Combine(AppContext.BaseDirectory, "data", "moby");
    }

    [Fact]
    public void EveryGameHasAMobyLayoutKeyedByItsGameByte()
    {
        var layouts = MobyLayouts.Load(MobyDataFolder());

        Assert.Empty(MobyLayouts.Problems);
        Assert.Equal(4, layouts.Count);

        foreach (var game in new[] { GameId.Rac1, GameId.Rac2, GameId.Rac3, GameId.Rac4 })
        {
            var layout = layouts[game];
            Assert.Equal((byte)game, layout.Game);
            Assert.Equal(0x100, layout.Stride);
            Assert.NotEmpty(layout.Source);

            // The position Vec4 sits at 0x10 in all four Moby structs.
            Assert.Equal(0x10, layout.Fields["position"].Offset);
            Assert.Equal("vec3f", layout.Fields["position"].Type);
            Assert.Equal(0x20, layout.Fields["state"].Offset);

            foreach (var (name, field) in layout.Fields)
            {
                Assert.True(field.Length > 0, $"{game} field {name} has an unknown type '{field.Type}'");
                Assert.InRange(field.Offset, 0, layout.Stride - field.Length);
                Assert.NotEmpty(field.From);
            }
        }
    }

    [Theory]
    [InlineData(GameId.Rac1, 0xA6, 0xB2)]
    [InlineData(GameId.Rac2, 0xAA, 0xB2)]
    [InlineData(GameId.Rac3, 0xAA, 0xB2)]
    [InlineData(GameId.Rac4, 0xBC, 0xB2)]
    public void MobyClassAndUidOffsetsMatchTheStructsTheyCameFrom(GameId game, int oClass, int uid)
    {
        var layout = MobyLayouts.Load(MobyDataFolder())[game];
        Assert.Equal(oClass, layout.Fields["oClass"].Offset);
        Assert.Equal(uid, layout.Fields["uid"].Offset);
    }

    [Fact]
    public void MobyLayoutDecodesARowBigEndian()
    {
        var layout = MobyLayouts.Load(MobyDataFolder())[GameId.Rac1];

        var row = new byte[0x100];
        BinaryPrimitives.WriteSingleBigEndian(row.AsSpan(0x10), 1.5f);
        BinaryPrimitives.WriteSingleBigEndian(row.AsSpan(0x14), -2.5f);
        BinaryPrimitives.WriteSingleBigEndian(row.AsSpan(0x18), 300f);
        row[0x20] = 0xFF;                                           // state, signed: -1
        BinaryPrimitives.WriteInt16BigEndian(row.AsSpan(0xA6), 6886);
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(0xB2), 40000);

        Assert.True(layout.TryReadVector(row, "position", out float x, out float y, out float z));
        Assert.Equal(1.5f, x);
        Assert.Equal(-2.5f, y);
        Assert.Equal(300f, z);

        Assert.True(layout.TryReadInteger(row, "state", out long state));
        Assert.Equal(-1, state);

        Assert.True(layout.TryReadInteger(row, "oClass", out long oClass));
        Assert.Equal(6886, oClass);

        Assert.True(layout.TryReadInteger(row, "uid", out long uid));
        Assert.Equal(40000, uid);

        Assert.False(layout.TryReadInteger(row, "not-a-field", out _));
        Assert.False(layout.TryReadVector(row, "state", out _, out _, out _));
    }

    [Fact]
    public void AMissingMobyFolderIsReportedRatherThanThrown()
    {
        var layouts = MobyLayouts.Load(Path.Combine(Path.GetTempPath(), "racman-no-such-moby-folder"));
        Assert.Empty(layouts);
        Assert.Single(MobyLayouts.Problems);
    }

    [Fact]
    public void ABadMobyFileIsReportedAndTheRestStillLoad()
    {
        string folder = Path.Combine(Path.GetTempPath(), "racman-moby-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "Good.json"),
                """{ "game": 1, "name": "one", "stride": 256, "fields": { "position": { "offset": 16, "type": "vec3f", "from": "Vec4" } } }""");
            File.WriteAllText(Path.Combine(folder, "Broken.json"), "{ not json");
            File.WriteAllText(Path.Combine(folder, "NoGame.json"), """{ "name": "nothing" }""");

            var layouts = MobyLayouts.Load(folder);

            Assert.Single(layouts);
            Assert.True(layouts.ContainsKey(GameId.Rac1));
            Assert.Equal(2, MobyLayouts.Problems.Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
