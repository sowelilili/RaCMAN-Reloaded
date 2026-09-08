using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The client preferences file. It has to survive both a missing file and a file written by an
/// older build that never heard of the newer keys, because a broken settings file must not stop
/// the client from starting.
/// </summary>
public class SettingsTests
{
    [Fact]
    public void DefaultsAreTheLightThemeAndNoDebugInfo()
    {
        var settings = new Settings();

        Assert.Equal("light", settings.Theme);
        Assert.True(settings.LightTheme);
        Assert.False(settings.DebugInfo);
    }

    [Fact]
    public void DarkIsTheOnlyValueThatTurnsTheLightThemeOff()
    {
        Assert.False(new Settings { Theme = "dark" }.LightTheme);
        Assert.False(new Settings { Theme = "DARK" }.LightTheme);
        Assert.True(new Settings { Theme = "light" }.LightTheme);
        Assert.True(new Settings { Theme = "nonsense" }.LightTheme);
    }

    [Fact]
    public void SaveAndLoadRoundTripTheThemeAndTheDebugSwitch()
    {
        var folder = TempFolder();
        try
        {
            string path = Path.Combine(folder, "racman-reloaded.settings.json");
            var saved = Settings.Load(path);
            saved.Theme = "dark";
            saved.DebugInfo = true;
            saved.Save();

            var loaded = Settings.Load(path);

            Assert.Equal("dark", loaded.Theme);
            Assert.False(loaded.LightTheme);
            Assert.True(loaded.DebugInfo);
            Assert.Equal(path, loaded.Path);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AFileWithoutTheNewKeysLoadsWithTheDefaults()
    {
        var folder = TempFolder();
        try
        {
            string path = Path.Combine(folder, "old.settings.json");
            File.WriteAllText(path, """{ "lastHost": "192.168.1.50", "autoReconnect": false }""");

            var loaded = Settings.Load(path);

            Assert.Equal("192.168.1.50", loaded.LastHost);
            Assert.False(loaded.AutoReconnect);
            Assert.Equal("light", loaded.Theme);
            Assert.True(loaded.LightTheme);
            Assert.False(loaded.DebugInfo);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AMissingFileLoadsTheDefaultsAndRemembersWhereItShouldGo()
    {
        var folder = TempFolder();
        try
        {
            string path = Path.Combine(folder, "not-written-yet.json");

            var loaded = Settings.Load(path);

            Assert.Equal(path, loaded.Path);
            Assert.Equal("light", loaded.Theme);
            Assert.False(loaded.DebugInfo);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "racman-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
