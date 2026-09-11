namespace RaCMAN.App;

/// <summary>
/// The main window's size and place: the size the layout is drawn for, and the rules that bring a
/// size and a position saved on some other desktop back onto this one. It is all arithmetic over
/// plain rectangles and nothing in here asks GLFW anything, so the tests can put a window on a
/// monitor that is not there.
/// </summary>
public static class WindowGeometry
{
    /// <summary>
    /// The client size the window opens at, and the smallest one it is ever restored to. The height
    /// is what the side nav needs before it starts scrolling, which is why it is not a rounder
    /// number. It is measured against the game with the most entries: UYA has an Unlocks entry that
    /// Deadlocked has not, and 564 was one row short of it.
    /// </summary>
    public const int DefaultWidth = 822;

    public const int DefaultHeight = 570;

    /// <summary>
    /// One monitor as far as this file is concerned: where its usable area starts and how big it
    /// is, in the same screen coordinates a window's position is in.
    /// </summary>
    public readonly record struct Area(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;

        public int Bottom => Y + Height;

        public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
    }

    /// <summary>The desktop a saved window is put back on when the caller knows of no monitors at all.</summary>
    public static Area Fallback => new(0, 0, DefaultWidth, DefaultHeight);

    /// <summary>
    /// Everything the window needs to open: the size to use and the position to ask for, or no
    /// position at all when the saved one is on a monitor this desktop no longer has and the
    /// window should simply open where the platform puts it.
    /// </summary>
    public static (int Width, int Height, int? X, int? Y) Restore(
        int? width, int? height, int? x, int? y, IReadOnlyList<Area> monitors)
    {
        var home = HomeMonitor(x, y, monitors);
        var (fittedWidth, fittedHeight) = Size(width, height, home);
        var place = Position(x, y, fittedWidth, fittedHeight, monitors);

        return (fittedWidth, fittedHeight, place?.X, place?.Y);
    }

    /// <summary>
    /// The size to open at: never smaller than the default, because that is the size the nav and
    /// the panels were laid out for, and never bigger than the monitor it is opening on, so a size
    /// saved on a larger screen does not arrive with its edges past the desktop.
    /// </summary>
    public static (int Width, int Height) Size(int? width, int? height, Area monitor) =>
        (Fit(width, DefaultWidth, monitor.Width), Fit(height, DefaultHeight, monitor.Height));

    /// <summary>
    /// Where to open, or null for "wherever the platform likes". A saved corner on a monitor that
    /// has gone away is not a place anybody can reach, so it is dropped rather than clamped; one
    /// that is on a monitor but hangs off its far edge is pulled back until the window fits.
    /// </summary>
    public static (int X, int Y)? Position(int? x, int? y, int width, int height, IReadOnlyList<Area> monitors)
    {
        if (x is not { } left || y is not { } top) return null;

        foreach (var monitor in monitors)
        {
            if (!monitor.Contains(left, top)) continue;

            // A window wider or taller than the monitor keeps the corner it was saved at: there is
            // nowhere to pull it back to.
            return (
                Math.Max(monitor.X, Math.Min(left, monitor.Right - width)),
                Math.Max(monitor.Y, Math.Min(top, monitor.Bottom - height)));
        }

        return null;
    }

    /// <summary>
    /// The monitor a saved window belongs to: the one its corner is on, else the first the caller
    /// listed, which is the primary. This is the monitor the size is measured against.
    /// </summary>
    private static Area HomeMonitor(int? x, int? y, IReadOnlyList<Area> monitors)
    {
        if (monitors.Count == 0) return Fallback;

        if (x is { } left && y is { } top)
        {
            foreach (var monitor in monitors)
            {
                if (monitor.Contains(left, top)) return monitor;
            }
        }

        return monitors[0];
    }

    /// <summary>
    /// One dimension of the size rule. A hand-edited zero or a negative number is not a size, so it
    /// reads as the default, and a monitor smaller than the floor loses: the window is the size the
    /// layout needs even where the desktop has less to give.
    /// </summary>
    private static int Fit(int? saved, int floor, int available)
    {
        int wanted = saved is { } value && value > 0 ? value : floor;

        return Math.Max(floor, Math.Min(wanted, available));
    }
}
