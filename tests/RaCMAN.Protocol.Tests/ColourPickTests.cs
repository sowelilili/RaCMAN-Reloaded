using System.Numerics;
using RaCMAN.App.Panels;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The colour pickers' own hue, saturation and value (HsvPick): taking the saturation or the value
/// to 0 and back keeps the hue, and the RGB is worked out again only for a change from outside.
/// </summary>
public class ColourPickTests
{
    private static void Near(Vector3 want, Vector3 got) =>
        Assert.True(Vector3.Distance(want, got) < 0.01f, $"want {want}, got {got}");

    [Theory]
    [InlineData(0xFF0000u, 0f, 1f, 1f)]
    [InlineData(0x00FF00u, 1f / 3f, 1f, 1f)]
    [InlineData(0x0000FFu, 2f / 3f, 1f, 1f)]
    [InlineData(0xFF8E00u, 0.0928f, 1f, 1f)]
    [InlineData(0x404040u, 0f, 0f, 0.251f)]
    public void RgbAndHsvConvertBothWays(uint rgb, float h, float s, float v)
    {
        var hsv = ColourMath.ToHsv(rgb, Vector3.Zero);
        Near(new Vector3(h, s, v), hsv);
        Assert.Equal(rgb, ColourMath.ToRgb(hsv));
    }

    [Fact]
    public void AGreyKeepsTheHueBeforeItAndBlackTheSaturationToo()
    {
        var before = new Vector3(0.6f, 0.8f, 0.9f);
        Near(new Vector3(0.6f, 0f, 0.5f), ColourMath.ToHsv(0x808080, before));
        Near(new Vector3(0.6f, 0.8f, 0f), ColourMath.ToHsv(0x000000, before));
    }

    [Fact]
    public void DraggingSaturationOrValueToZeroAndBackKeepsTheHue()
    {
        var pick = new HsvPick();
        pick.Sync(0xFF8E00, now: 0);
        float hue = pick.Hsv.X;

        // Saturation to 0: a grey, sent and read back as one.
        uint grey = pick.Pick(new Vector3(hue, 0f, 1f), now: 1);
        Assert.Equal(0xFFFFFFu, grey);
        pick.Sync(grey, now: 3);
        Assert.Equal(hue, pick.Hsv.X);

        // Value to 0: black, and the saturation picked before it too.
        pick.Pick(new Vector3(hue, 0.7f, 1f), now: 4);
        uint black = pick.Pick(new Vector3(hue, 0.7f, 0f), now: 5);
        Assert.Equal(0u, black);
        pick.Sync(black, now: 7);
        Assert.Equal(new Vector3(hue, 0.7f, 0f), pick.Hsv);

        // And back up: the hue the user had.
        Assert.Equal(0xFF8E00u, pick.Pick(new Vector3(hue, 1f, 1f), now: 8));
    }

    [Fact]
    public void AReadoutStillCatchingUpWithAPickIsNotTakenButAChangeFromOutsideIs()
    {
        var pick = new HsvPick();
        pick.Sync(0x123456, now: 0);
        uint sent = pick.Pick(new Vector3(0.1f, 1f, 1f), now: 10);

        // The readout still says the old colour for a moment: the picker keeps its own.
        pick.Sync(0x123456, now: 10.2);
        Assert.Equal(sent, pick.Rgb);

        // Once it has caught up, the console's value (rounded by it) is taken.
        pick.Sync(sent + 1, now: 10 + HsvPick.Hold + 0.1);
        Assert.Equal(sent + 1, pick.Rgb);

        // A preset or "(none)" from this client releases the picker at once.
        pick.Pick(new Vector3(0.5f, 1f, 1f), now: 20);
        pick.Release();
        pick.Sync(0xFF0000, now: 20.1);
        Assert.Equal(0xFF0000u, pick.Rgb);
        Assert.Equal(0f, pick.Hsv.X);
    }
}
