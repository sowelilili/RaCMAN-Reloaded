using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The one-off copy out of the application folder and into the data folder. Both folders are temp
/// folders here; the real ones are never touched.
/// </summary>
public class DataFolderMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "racman-migration-" + Guid.NewGuid().ToString("N"));

    private string App => Path.Combine(_root, "app");

    private string Data => Path.Combine(_root, "data");

    public DataFolderMigrationTests()
    {
        Directory.CreateDirectory(App);
        Directory.CreateDirectory(Data);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Windows sometimes holds a handle a moment longer than the test does.
        }
    }

    private void WriteApp(string relative, string content)
    {
        var path = Path.Combine(App, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private bool InData(string relative) =>
        File.Exists(Path.Combine(Data, relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void TheSettingsFileAndTheThreeLibrariesComeAcross()
    {
        WriteApp(AppPaths.SettingsFileName, """{ "lastHost": "192.168.1.50" }""");
        WriteApp("colours/rac2.json", "[]");
        WriteApp("watchlists/NPEA00385.json", "[]");
        WriteApp("savefiles/NPEA00385/misc/a-save", "bytes");

        var result = DataFolderMigration.Run(App, Data);

        Assert.True(result.Ran);
        Assert.Equal(new[] { "settings", "colours", "watchlists", "savefiles" }, result.Moved);
        Assert.True(InData(AppPaths.SettingsFileName));
        Assert.True(InData("colours/rac2.json"));
        Assert.True(InData("watchlists/NPEA00385.json"));
        Assert.True(InData("savefiles/NPEA00385/misc/a-save"));
    }

    [Fact]
    public void ThePremadeSavefilesAReleaseShipsStayWhereTheyAre()
    {
        WriteApp(AppPaths.SettingsFileName, "{}");
        WriteApp($"{RaCMAN.Protocol.PremadeSaveFiles.FolderName}/NPEA00385/NG+.sav", "bytes");

        var result = DataFolderMigration.Run(App, Data);

        Assert.Equal(new[] { "settings" }, result.Moved);
        Assert.False(Directory.Exists(Path.Combine(Data, RaCMAN.Protocol.PremadeSaveFiles.FolderName)));
        Assert.False(Directory.Exists(Path.Combine(Data, "savefiles")));
    }

    [Fact]
    public void TheOriginalsAreLeftWhereTheyWere()
    {
        WriteApp(AppPaths.SettingsFileName, "{}");
        WriteApp("colours/rac2.json", "[]");

        DataFolderMigration.Run(App, Data);

        Assert.True(File.Exists(Path.Combine(App, AppPaths.SettingsFileName)));
        Assert.True(File.Exists(Path.Combine(App, "colours", "rac2.json")));
    }

    [Fact]
    public void ADataFolderThatAlreadyHasSettingsIsNeverTouched()
    {
        WriteApp(AppPaths.SettingsFileName, """{ "theme": "dark" }""");
        File.WriteAllText(Path.Combine(Data, AppPaths.SettingsFileName), """{ "theme": "light" }""");

        var result = DataFolderMigration.Run(App, Data);

        Assert.False(result.Ran);
        Assert.Contains("light", File.ReadAllText(Path.Combine(Data, AppPaths.SettingsFileName)));
    }

    [Fact]
    public void AnEmptyApplicationFolderMovesNothingAndSaysSo()
    {
        var result = DataFolderMigration.Run(App, Data);

        Assert.True(result.Ran);
        Assert.False(result.MovedAnything);
    }

    [Fact]
    public void TheSameFolderTwiceIsNotAMigration()
    {
        WriteApp(AppPaths.SettingsFileName, "{}");

        Assert.False(DataFolderMigration.Run(App, App).Ran);
    }

    [Fact]
    public void OnlyTheModsTheReleaseDidNotShipAreTaken()
    {
        WriteApp("mods/" + AppPaths.ShippedModsManifest,
            "# what this release shipped\nlibs\nNPEA00385\nNPEA00385/flight\nNPEA00385/hardcore\n");
        WriteApp("mods/NPEA00385/flight/patch.txt", "#-name: Flight");
        WriteApp("mods/NPEA00385/hardcore/patch.txt", "#-name: Hardcore");
        WriteApp("mods/NPEA00385/my-own-mod/patch.txt", "#-name: Mine");
        WriteApp("mods/libs/helper.lua", "-- shipped");

        // A whole title the release never shipped a folder for.
        WriteApp("mods/BCES01503/another-mod/patch.txt", "#-name: Also mine");

        var result = DataFolderMigration.Run(App, Data);

        Assert.Contains("2 mods", result.Moved);
        Assert.True(InData("mods/NPEA00385/my-own-mod/patch.txt"));
        Assert.True(InData("mods/BCES01503/another-mod/patch.txt"));
        Assert.False(InData("mods/NPEA00385/flight/patch.txt"));
        Assert.False(InData("mods/libs/helper.lua"));
    }

    [Fact]
    public void WithoutAManifestNoModIsTreatedAsTheUsers()
    {
        // A development tree: the mods folder is the repo's, and nothing there is at risk.
        WriteApp("mods/NPEA00385/flight/patch.txt", "#-name: Flight");

        var result = DataFolderMigration.Run(App, Data);

        Assert.True(result.Ran);
        Assert.False(result.MovedAnything);
        Assert.False(InData("mods/NPEA00385/flight/patch.txt"));
    }

    [Fact]
    public void TheManifestIgnoresBlankLinesCommentsAndBackslashes()
    {
        WriteApp("mods/" + AppPaths.ShippedModsManifest, "\n# a comment\r\nNPEA00385\r\nNPEA00385\\flight\r\n\n");

        var shipped = DataFolderMigration.ReadShippedManifest(Path.Combine(App, "mods"));

        Assert.NotNull(shipped);
        Assert.Equal(2, shipped!.Count);
        Assert.Contains("NPEA00385/flight", shipped);
        Assert.DoesNotContain("# a comment", shipped);
    }

    [Fact]
    public void AModAReleaseDroppedIsNotTakenForTheUsers()
    {
        // A new release unzipped over an older one: the crash-patch mod the older one shipped is
        // still in the folder, and the new manifest does not list it. It is qwark's now, and copied
        // into the data folder it would become the user's own and load on top of qwark's patch.
        WriteApp("mods/" + AppPaths.ShippedModsManifest, "NPEA00423\nNPEA00423/il-ghost\n");
        WriteApp("mods/NPEA00423/il-ghost/patch.txt", "#-name: IL ghost");
        WriteApp("mods/NPEA00423/crash-patch/patch.txt", "#-name: Crash patches");
        WriteApp("mods/NPEA00423/my-own-mod/patch.txt", "#-name: Mine");

        // And a whole title folder the release does not ship, with a dropped mod in it.
        WriteApp("mods/NPEA00387/coolcam/patch.txt", "#-name: Cool cam");
        WriteApp("mods/NPEA00387/mine-too/patch.txt", "#-name: Also mine");

        var exclusions = LegacyModExclusions.Parse("NPEA00423/crash-patch  built into qwark\nNPEA00387/coolcam  not shipped\n");
        var result = DataFolderMigration.Run(App, Data, exclusions);

        Assert.Contains("2 mods", result.Moved);
        Assert.True(InData("mods/NPEA00423/my-own-mod/patch.txt"));
        Assert.True(InData("mods/NPEA00387/mine-too/patch.txt"));
        Assert.False(InData("mods/NPEA00423/crash-patch/patch.txt"));
        Assert.False(InData("mods/NPEA00387/coolcam/patch.txt"));
        Assert.False(InData("mods/NPEA00423/il-ghost/patch.txt"));
    }

    [Fact]
    public void TheShippedExclusionListNamesTheCrashPatchMod()
    {
        // The list the client reads by default, which is what keeps the leftover out of the data
        // folder and out of the Mods panel.
        Assert.True(LegacyModExclusions.Shipped.Excludes("NPEA00423", "crash-patch"));
    }

    [Fact]
    public void OneMovedModIsCountedInTheSingular()
    {
        WriteApp("mods/" + AppPaths.ShippedModsManifest, "NPEA00385\n");
        WriteApp("mods/NPEA00385/my-own-mod/patch.txt", "#-name: Mine");

        Assert.Contains("1 mod", DataFolderMigration.Run(App, Data).Moved);
    }
}
