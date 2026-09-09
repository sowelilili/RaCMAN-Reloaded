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

    [Fact]
    public void TheInputDisplayStartsInThePanelAndTheModeMapsOntoTheWindowFlag()
    {
        var settings = new Settings();

        Assert.Equal(InputDisplayMode.Panel, settings.InputMode);
        Assert.False(settings.InputFloating);
        Assert.False(settings.InputWindowed);
        Assert.False(settings.InputWindowOnTop);
        Assert.Null(settings.InputWindowX);
        Assert.Null(settings.InputWindowY);
        Assert.Null(settings.InputWindowW);
        Assert.Null(settings.InputWindowH);

        settings.InputMode = InputDisplayMode.Window;
        Assert.True(settings.InputWindowed);
        Assert.False(settings.InputFloating);

        settings.InputMode = InputDisplayMode.Panel;
        Assert.False(settings.InputWindowed);
        Assert.False(settings.InputFloating);
    }

    /// <summary>
    /// The floating pad is gone, and the flag it left behind is what an upgrading user's file
    /// still says. Setting the mode clears it, so the file stops carrying a dead key.
    /// </summary>
    [Fact]
    public void TheOldFloatingFlagReadsAsThePadWindowAndIsClearedWhenTheModeIsSet()
    {
        var settings = new Settings { InputFloating = true };

        Assert.Equal(InputDisplayMode.Window, settings.InputMode);

        settings.InputMode = InputDisplayMode.Panel;

        Assert.False(settings.InputFloating);
        Assert.False(settings.InputWindowed);
        Assert.Equal(InputDisplayMode.Panel, settings.InputMode);
    }

    [Fact]
    public void SaveAndLoadRoundTripThePadWindowMode_OnTopFlagAndGeometry()
    {
        var folder = TempFolder();
        try
        {
            string path = Path.Combine(folder, "racman-reloaded.settings.json");
            var saved = Settings.Load(path);
            saved.InputMode = InputDisplayMode.Window;
            saved.InputWindowOnTop = true;
            saved.InputWindowX = -1720;
            saved.InputWindowY = 240;
            saved.InputWindowW = 800;
            saved.InputWindowH = 558;
            saved.Save();

            var loaded = Settings.Load(path);

            Assert.Equal(InputDisplayMode.Window, loaded.InputMode);
            Assert.True(loaded.InputWindowed);
            Assert.False(loaded.InputFloating);
            Assert.True(loaded.InputWindowOnTop);
            Assert.Equal(-1720, loaded.InputWindowX);
            Assert.Equal(240, loaded.InputWindowY);
            Assert.Equal(800, loaded.InputWindowW);
            Assert.Equal(558, loaded.InputWindowH);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Nobody loses their pad when the floating mode goes away: it opens in its own window.</summary>
    [Fact]
    public void AFileThatOnlyKnowsInputFloatingOpensThePadWindow()
    {
        var folder = TempFolder();
        try
        {
            string path = Path.Combine(folder, "old.settings.json");
            File.WriteAllText(path, """{ "inputSkin": "DS3 Black", "inputFloating": true }""");

            var loaded = Settings.Load(path);

            Assert.Equal(InputDisplayMode.Window, loaded.InputMode);
            Assert.False(loaded.InputWindowOnTop);
            Assert.Null(loaded.InputWindowX);
            Assert.Null(loaded.InputWindowW);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---------------------------------------------------------------- table refresh

    [Fact]
    public void TablesRefreshOnceASecondUntilSomethingSaysOtherwise()
    {
        Assert.Equal(1f, new Settings().TableRefreshSeconds);
        Assert.Equal(1f, Settings.DefaultTableRefreshSeconds);
    }

    /// <summary>
    /// The file is hand-editable and the box takes typing, so the period is clamped where it is
    /// stored: zero is "manual only", and nothing can ask for a re-read every frame.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(2.5f, 2.5f)]
    [InlineData(10f, 10f)]
    [InlineData(99f, 10f)]
    [InlineData(-3f, 0f)]
    [InlineData(float.NaN, 1f)]
    [InlineData(float.PositiveInfinity, 1f)]
    public void TheTableRefreshPeriodIsClampedToWhatThePanelsCanUse(float written, float kept)
    {
        Assert.Equal(kept, new Settings { TableRefreshSeconds = written }.TableRefreshSeconds);
    }

    [Fact]
    public void SaveAndLoadRoundTripTheTableRefreshPeriod()
    {
        var folder = TempFolder();
        try
        {
            string path = Path.Combine(folder, "racman-reloaded.settings.json");
            var saved = Settings.Load(path);
            saved.TableRefreshSeconds = 0f;
            saved.Save();

            Assert.Equal(0f, Settings.Load(path).TableRefreshSeconds);

            saved.TableRefreshSeconds = 2.5f;
            saved.Save();

            Assert.Equal(2.5f, Settings.Load(path).TableRefreshSeconds);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AFileFromBeforeTheTableRefreshSettingRefreshesOnceASecond()
    {
        var folder = TempFolder();
        try
        {
            string path = Path.Combine(folder, "old.settings.json");
            File.WriteAllText(path, """{ "lastHost": "192.168.1.50" }""");

            Assert.Equal(1f, Settings.Load(path).TableRefreshSeconds);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A period nobody could have typed still loads: the setter is what clamps it.</summary>
    [Fact]
    public void AHandEditedRefreshPeriodIsClampedOnLoad()
    {
        var folder = TempFolder();
        try
        {
            string path = Path.Combine(folder, "hand-edited.settings.json");
            File.WriteAllText(path, """{ "tableRefreshSeconds": -12.5 }""");

            Assert.Equal(0f, Settings.Load(path).TableRefreshSeconds);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheTargetIsThePs3UntilSomethingSaysOtherwise()
    {
        var settings = new Settings();

        Assert.Equal("ps3", settings.Target);
        Assert.False(settings.Rpcs3Target);
        Assert.Equal(Rpcs3Host.DefaultPinePort, settings.Rpcs3PinePort);
        Assert.Equal(28012, settings.Rpcs3PinePort);
        Assert.Equal(string.Empty, settings.Rpcs3QwarkPath);
    }

    [Fact]
    public void SaveAndLoadRoundTripTheRpcs3Keys()
    {
        var folder = TempFolder();
        try
        {
            string path = Path.Combine(folder, "racman-reloaded.settings.json");
            var saved = Settings.Load(path);
            saved.Rpcs3Target = true;
            saved.Rpcs3PinePort = 28099;
            saved.Rpcs3QwarkPath = @"D:\qwark\qwark-rpcs3.exe";
            saved.Save();

            var loaded = Settings.Load(path);

            Assert.Equal("rpcs3", loaded.Target);
            Assert.True(loaded.Rpcs3Target);
            Assert.Equal(28099, loaded.Rpcs3PinePort);
            Assert.Equal(@"D:\qwark\qwark-rpcs3.exe", loaded.Rpcs3QwarkPath);

            // And back again, so the radio button is not a one-way door.
            loaded.Rpcs3Target = false;
            loaded.Save();
            Assert.Equal("ps3", Settings.Load(path).Target);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("rpcs3", true)]
    [InlineData("RPCS3", true)]
    [InlineData("ps3", false)]
    [InlineData("nonsense", false)]
    public void OnlyRpcs3TurnsTheEmulatorTargetOn(string target, bool rpcs3)
    {
        Assert.Equal(rpcs3, new Settings { Target = target }.Rpcs3Target);
    }

    [Fact]
    public void AFileFromBeforeTheRpcs3TargetLoadsOnThePs3()
    {
        var folder = TempFolder();
        try
        {
            string path = Path.Combine(folder, "old.settings.json");
            File.WriteAllText(path, """{ "lastHost": "192.168.1.50", "webManSlot": 3 }""");

            var loaded = Settings.Load(path);

            Assert.False(loaded.Rpcs3Target);
            Assert.Equal("ps3", loaded.Target);
            Assert.Equal(28012, loaded.Rpcs3PinePort);
            Assert.Equal(string.Empty, loaded.Rpcs3QwarkPath);
            Assert.Equal("192.168.1.50", loaded.LastHost);
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
