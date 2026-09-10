using RaCMAN.App;

using Area = RaCMAN.App.WindowGeometry.Area;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The rules that put the main window back where the last run left it. The window remembers a size
/// and a corner across launches, and a settings file travels: it can name a size no monitor here is
/// big enough for, or a corner on a screen that has since been unplugged, and neither may open a
/// window nobody can reach.
/// </summary>
public class WindowGeometryTests
{
    /// <summary>A 1920x1080 screen with a taskbar along the bottom, which is what a work area is.</summary>
    private static readonly Area Primary = new(0, 0, 1920, 1040);

    /// <summary>A second screen to the left of it, which is where the negative coordinates come from.</summary>
    private static readonly Area Secondary = new(-1920, -120, 1920, 1080);

    private static readonly Area[] Desktop = { Primary, Secondary };

    [Fact]
    public void TheDefaultIsTheSizeTheNavAndTheGameSubPagesFitIn()
    {
        Assert.Equal(822, WindowGeometry.DefaultWidth);
        Assert.Equal(564, WindowGeometry.DefaultHeight);
    }

    [Fact]
    public void AFileWithNoSavedSizeOpensAtTheDefault()
    {
        Assert.Equal((822, 564), WindowGeometry.Size(null, null, Primary));
    }

    /// <summary>The default is the floor, so nothing can open a window too small for the side nav.</summary>
    [Theory]
    [InlineData(700, 400, 822, 564)]
    [InlineData(0, 0, 822, 564)]
    [InlineData(-40, -40, 822, 564)]
    [InlineData(821, 563, 822, 564)]
    public void ASizeBelowTheFloorOpensAtTheFloor(int width, int height, int kept, int keptHeight)
    {
        Assert.Equal((kept, keptHeight), WindowGeometry.Size(width, height, Primary));
    }

    [Fact]
    public void ASizeTheScreenCanTakeIsKeptAsItIs()
    {
        Assert.Equal((1200, 900), WindowGeometry.Size(1200, 900, Primary));
    }

    /// <summary>A settings file carried over from a bigger screen must not open past this one's edges.</summary>
    [Fact]
    public void ASizeFromABiggerScreenIsCutDownToThisOne()
    {
        Assert.Equal((1920, 1040), WindowGeometry.Size(3840, 2160, Primary));
    }

    /// <summary>
    /// The floor wins on a screen smaller than it: a window the layout does not fit in is worse
    /// than one that hangs over the edge of a very small desktop.
    /// </summary>
    [Fact]
    public void AScreenSmallerThanTheFloorStillGetsTheFloor()
    {
        Assert.Equal((822, 564), WindowGeometry.Size(1200, 900, new Area(0, 0, 640, 480)));
    }

    [Fact]
    public void ACornerOnAScreenThatIsStillThereIsKept()
    {
        Assert.Equal((320, 180), WindowGeometry.Position(320, 180, 900, 600, Desktop));
    }

    [Fact]
    public void ACornerOnTheSecondScreenIsKeptNegativeCoordinatesAndAll()
    {
        Assert.Equal((-1500, -20), WindowGeometry.Position(-1500, -20, 900, 600, Desktop));
    }

    /// <summary>The screen it was left on has gone, so the platform places the window instead.</summary>
    [Theory]
    [InlineData(4200, 300)]
    [InlineData(-6000, -6000)]
    [InlineData(200, 3000)]
    public void ACornerOnNoScreenAtAllIsDropped(int x, int y)
    {
        Assert.Null(WindowGeometry.Position(x, y, 900, 600, Desktop));
    }

    [Fact]
    public void AWindowHangingOffTheEdgeIsPulledBackOntoItsScreen()
    {
        // 1600 + 900 is past the right-hand edge, and 900 + 600 is under the taskbar.
        Assert.Equal((1020, 440), WindowGeometry.Position(1600, 900, 900, 600, Desktop));
    }

    /// <summary>A window bigger than the screen keeps its corner: there is nowhere to pull it back to.</summary>
    [Fact]
    public void AWindowWiderThanItsScreenKeepsTheCornerItWasSavedAt()
    {
        Assert.Equal((0, 0), WindowGeometry.Position(0, 0, 4000, 3000, new[] { Primary }));
    }

    [Fact]
    public void HalfASavedCornerIsNoCornerAtAll()
    {
        Assert.Null(WindowGeometry.Position(100, null, 900, 600, Desktop));
        Assert.Null(WindowGeometry.Position(null, 100, 900, 600, Desktop));
        Assert.Null(WindowGeometry.Position(null, null, 900, 600, Desktop));
    }

    [Fact]
    public void AFirstRunOpensAtTheDefaultWithNoCornerToAskFor()
    {
        var restored = WindowGeometry.Restore(null, null, null, null, Desktop);

        Assert.Equal((822, 564, (int?)null, (int?)null), restored);
    }

    /// <summary>The size is measured against the screen the window is on, not against the primary.</summary>
    [Fact]
    public void ASizeIsCutDownToTheScreenTheCornerIsOn()
    {
        var narrow = new Area(2000, 0, 1000, 700);
        var restored = WindowGeometry.Restore(1600, 1200, 2100, 100, new[] { Primary, narrow });

        Assert.Equal((1000, 700, (int?)2000, (int?)0), restored);
    }

    [Fact]
    public void WithNoMonitorsAtAllTheDefaultSizeAndNoCornerComeBack()
    {
        var restored = WindowGeometry.Restore(1400, 900, 40, 40, Array.Empty<Area>());

        Assert.Equal((822, 564, (int?)null, (int?)null), restored);
    }
}
