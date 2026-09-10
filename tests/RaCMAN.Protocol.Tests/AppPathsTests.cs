using System.Runtime.InteropServices;
using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Where the user's files go, per platform, and the once-only copy that brings them there from the
/// builds that kept everything beside the executable. Every path here is built by hand or in a temp
/// folder: nothing in this file may touch the real data folder, which the test run has moved
/// anyway (see <see cref="TestDataFolder"/>).
/// </summary>
public class AppPathsTests
{
    private static Func<string, string?> Environment(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void WindowsKeepsItUnderApplicationData()
    {
        var root = AppPaths.PlatformRoot(OSPlatform.Windows, Environment(("APPDATA", @"C:\Users\someone\AppData\Roaming")));

        Assert.Equal(Path.Combine(@"C:\Users\someone\AppData\Roaming", "RaCMAN Reloaded"), root);
    }

    [Fact]
    public void LinuxPrefersTheXdgConfigHome()
    {
        var root = AppPaths.PlatformRoot(OSPlatform.Linux,
            Environment(("XDG_CONFIG_HOME", "/home/someone/.config-elsewhere"), ("HOME", "/home/someone")));

        Assert.Equal(Path.Combine("/home/someone/.config-elsewhere", "racman-reloaded"), root);
    }

    [Fact]
    public void LinuxFallsBackToDotConfigUnderHome()
    {
        var root = AppPaths.PlatformRoot(OSPlatform.Linux, Environment(("HOME", "/home/someone")));

        Assert.Equal(Path.Combine("/home/someone", ".config", "racman-reloaded"), root);
    }

    [Fact]
    public void MacOsUsesApplicationSupport()
    {
        var root = AppPaths.PlatformRoot(OSPlatform.OSX, Environment(("HOME", "/Users/someone")));

        Assert.Equal(Path.Combine("/Users/someone", "Library", "Application Support", "RaCMAN Reloaded"), root);
    }

    [Fact]
    public void AnEnvironmentThatSaysNothingHasNoPlatformFolder()
    {
        Assert.Null(AppPaths.PlatformRoot(OSPlatform.Windows, Environment()));
        Assert.Null(AppPaths.PlatformRoot(OSPlatform.Linux, Environment()));
        Assert.Null(AppPaths.PlatformRoot(OSPlatform.OSX, Environment()));
    }

    [Fact]
    public void TheFlagWinsOverTheVariableAndTheVariableOverThePlatform()
    {
        var environment = Environment(
            (AppPaths.EnvironmentVariable, @"D:\from-the-variable"),
            ("APPDATA", @"C:\Users\someone\AppData\Roaming"));

        Assert.Equal(@"D:\from-the-flag",
            AppPaths.Resolve(@"D:\from-the-flag", OSPlatform.Windows, environment, "fallback"));
        Assert.Equal(@"D:\from-the-variable",
            AppPaths.Resolve(null, OSPlatform.Windows, environment, "fallback"));
    }

    [Fact]
    public void AQuotedOrEmptyFlagIsNotAFolder()
    {
        var environment = Environment(("APPDATA", @"C:\Roaming"));

        // A path pasted out of a file manager arrives quoted; an empty one is nothing at all.
        Assert.Equal(@"D:\somewhere", AppPaths.Resolve("  \"D:\\somewhere\" ", OSPlatform.Windows, environment, "fallback"));
        Assert.Equal(Path.Combine(@"C:\Roaming", "RaCMAN Reloaded"),
            AppPaths.Resolve("   ", OSPlatform.Windows, environment, "fallback"));
    }

    [Fact]
    public void WithNowhereToPutItTheApplicationFolderIsTheFallback()
    {
        Assert.Equal(@"C:\app", AppPaths.Resolve(null, OSPlatform.Windows, Environment(), @"C:\app"));
    }

    [Fact]
    public void TheTestRunIsPointedAtItsOwnFolder()
    {
        // The whole point of TestDataFolder: no test may write into the real data folder.
        Assert.Equal(Path.GetFullPath(TestDataFolder.Root), AppPaths.Root);
        Assert.Equal(Path.Combine(AppPaths.Root, AppPaths.SettingsFileName), Settings.DefaultPath);
    }

    [Fact]
    public void EveryUserFolderIsUnderTheDataFolderAndEveryShippedOneIsNot()
    {
        foreach (var folder in new[]
                 {
                     AppPaths.SettingsFile, AppPaths.Colours, AppPaths.Watchlists, AppPaths.SaveFiles,
                     AppPaths.Mods, AppPaths.Rpcs3Root, AppPaths.GameLayoutOverride,
                 })
        {
            Assert.StartsWith(AppPaths.Root, folder, StringComparison.Ordinal);
        }

        Assert.StartsWith(AppPaths.Application, AppPaths.ShippedMods, StringComparison.Ordinal);
        Assert.StartsWith(AppPaths.Application, AppPaths.ShippedGameLayout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shipped data files are read out of the application folder until the user puts a copy of
    /// one in the data folder, which is the only way an edit survives an update. Only the "no copy
    /// yet" half is asserted here: both overrides are static state that the test classes reading
    /// them run against in parallel, so a file written into the shared data folder would decide
    /// another class's route for it.
    /// </summary>
    [Fact]
    public void TheShippedDataFilesAreUsedUntilTheDataFolderHasItsOwn()
    {
        Assert.Equal(Path.Combine(AppPaths.Root, "gamelayout.json"), AppPaths.GameLayoutOverride);
        Assert.Equal(Path.Combine(AppPaths.Root, "autosplit"), AutosplitRoutes.OverrideFolder);

        Assert.False(GameLayout.UsingOverride);
        Assert.Equal(GameLayout.ShippedPath, GameLayout.DefaultPath);
        Assert.StartsWith(AppPaths.Application, AutosplitRoutes.FileFor(GameId.Rac2), StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingsPathThatIsAlreadyAbsoluteIsLeftAlone()
    {
        string absolute = Path.Combine(Path.GetTempPath(), "racman-somewhere-else");

        Assert.Equal(absolute, AppPaths.InData(absolute));
        Assert.Equal(Path.Combine(AppPaths.Root, "savefiles"), AppPaths.InData("savefiles"));
    }
}
