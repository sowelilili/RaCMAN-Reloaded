using System.Runtime.InteropServices;
using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The "Open folder" buttons under the libraries. Nothing here launches a file manager: the
/// mapping from a platform to the program it would run is a pure function precisely so it can be
/// checked for all three platforms from whichever one these tests run on.
/// </summary>
public class FileExplorerTests
{
    [Fact]
    public void EachPlatformGetsItsOwnFileManager()
    {
        Assert.Equal(("explorer.exe", @"C:\RaCMAN\mods"),
            FileExplorer.CommandFor(@"C:\RaCMAN\mods", OSPlatform.Windows));

        Assert.Equal(("xdg-open", "/home/me/RaCMAN/mods"),
            FileExplorer.CommandFor("/home/me/RaCMAN/mods", OSPlatform.Linux));

        Assert.Equal(("open", "/Users/me/RaCMAN/mods"),
            FileExplorer.CommandFor("/Users/me/RaCMAN/mods", OSPlatform.OSX));
    }

    /// <summary>The folder is the whole argument, so a pasted path keeps its spaces and loses its quotes.</summary>
    [Fact]
    public void TheFolderIsPassedAsOneArgumentAndUnquoted()
    {
        var command = FileExplorer.CommandFor("  \"C:\\Program Files\\RaCMAN\\savefiles\"  ", OSPlatform.Windows);

        Assert.NotNull(command);
        Assert.Equal(@"C:\Program Files\RaCMAN\savefiles", command.Value.Argument);
    }

    [Fact]
    public void ThereIsNothingToRunWithoutAFolderOrAKnownPlatform()
    {
        Assert.Null(FileExplorer.CommandFor(string.Empty, OSPlatform.Windows));
        Assert.Null(FileExplorer.CommandFor("   ", OSPlatform.Linux));
        Assert.Null(FileExplorer.CommandFor("/mods", OSPlatform.Create("FREEBSD")));
    }

    /// <summary>The three platforms this client ships for are the three it can open a folder on.</summary>
    [Fact]
    public void SupportedWhereverThereIsAFileManagerToAsk()
    {
        bool known = OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
        Assert.Equal(known, FileExplorer.IsSupported);
    }
}
