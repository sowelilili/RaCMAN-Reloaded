using System.Runtime.InteropServices;

namespace RaCMAN.App;

/// <summary>
/// The two folders this client lives in, and the difference between them.
/// <para>
/// The <em>application</em> folder is where the executable sits. It holds only what the release
/// ships and it is read-only as far as the user is concerned: the controller skins, the moby and
/// autosplit data, the shipped mod library, <c>qwark.sprx</c> and <c>qwark-rpcs3.exe</c>. The
/// updater replaces that whole folder when a new version lands, so nothing worth keeping may live
/// in it.
/// </para>
/// <para>
/// The <em>data</em> folder is the user's: the settings file, the colour presets, the watchlists,
/// the savefile library, the mods they installed themselves and the RPCS3 helper's own
/// <c>/dev_hdd0</c>. It is the platform's usual place for such a thing, and either the
/// <c>--data-dir</c> flag or the <c>RACMAN_DATA_DIR</c> environment variable moves it, which is
/// what a test run and a portable copy on a stick both use.
/// </para>
/// </summary>
public static class AppPaths
{
    /// <summary>Moves the data folder for one run. Read before anything else touches it.</summary>
    public const string Flag = "--data-dir";

    /// <summary>The same, for a run that cannot pass arguments. The flag wins over it.</summary>
    public const string EnvironmentVariable = "RACMAN_DATA_DIR";

    /// <summary>The folder name where the platform spells things out for people (Windows, macOS).</summary>
    public const string DisplayName = "RaCMAN Reloaded";

    /// <summary>The folder name where the platform expects a lower-case one (Linux).</summary>
    public const string UnixName = "racman-reloaded";

    public const string SettingsFileName = "racman-reloaded.settings.json";

    /// <summary>The mod library's list of what the release shipped; see <see cref="DataFolderMigration"/>.</summary>
    public const string ShippedModsManifest = "shipped.txt";

    private static string? _root;

    /// <summary>
    /// Where the platform keeps a desktop application's own files, or null when the environment
    /// does not say (no APPDATA, no HOME). Pure, and it takes the platform rather than reading it,
    /// so all three answers can be checked from whichever platform the tests happen to run on.
    /// </summary>
    public static string? PlatformRoot(OSPlatform platform, Func<string, string?> environment)
    {
        if (platform == OSPlatform.Windows)
        {
            return Value(environment, "APPDATA") is { } appData ? Path.Combine(appData, DisplayName) : null;
        }

        if (platform == OSPlatform.OSX)
        {
            return Value(environment, "HOME") is { } macHome
                ? Path.Combine(macHome, "Library", "Application Support", DisplayName)
                : null;
        }

        // Linux, and anything else that follows the XDG base directory spec.
        if (Value(environment, "XDG_CONFIG_HOME") is { } config) return Path.Combine(config, UnixName);
        return Value(environment, "HOME") is { } home ? Path.Combine(home, ".config", UnixName) : null;
    }

    /// <summary>
    /// The data folder for a run: the flag if it was given, else the environment variable, else the
    /// platform's own place, else <paramref name="fallback"/>. Pure, for the same reason as
    /// <see cref="PlatformRoot"/>; the live answer is <see cref="Root"/>.
    /// </summary>
    public static string Resolve(string? flag, OSPlatform platform, Func<string, string?> environment, string fallback)
    {
        if (Clean(flag) is { } fromFlag) return fromFlag;
        if (Clean(environment(EnvironmentVariable)) is { } fromEnvironment) return fromEnvironment;
        return PlatformRoot(platform, environment) ?? fallback;
    }

    /// <summary>
    /// The data folder. Resolved on first use, so the environment variable is read before anything
    /// has had a chance to write a file; <see cref="Use"/> replaces it for a run that was given the
    /// flag. A platform that will not say where its application data goes falls back to the
    /// application folder, which is where all of this used to live anyway.
    /// </summary>
    public static string Root
    {
        get => _root ??= Full(Resolve(null, Current, Environment.GetEnvironmentVariable, Application));
    }

    /// <summary>Points the data folder somewhere else. The <c>--data-dir</c> flag and the tests.</summary>
    public static void Use(string root)
    {
        if (Clean(root) is not { } cleaned) return;
        _root = Full(cleaned);
    }

    /// <summary>Creates the data folder. Best effort: a client that cannot write still starts.</summary>
    public static void EnsureRoot()
    {
        try
        {
            Directory.CreateDirectory(Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The panels report what they cannot write when they try to write it.
        }
    }

    /// <summary>Where the executable and everything the release ships sit.</summary>
    public static string Application => AppContext.BaseDirectory;

    public static string SettingsFile => Path.Combine(Root, SettingsFileName);

    public static string Colours => Path.Combine(Root, "colours");

    public static string Watchlists => Path.Combine(Root, "watchlists");

    public static string SaveFiles => Path.Combine(Root, "savefiles");

    /// <summary>The mods the user installed. A ZIP install lands here, never in the shipped library.</summary>
    public static string Mods => Path.Combine(Root, "mods");

    /// <summary>
    /// Where the RPCS3 helper maps <c>/dev_hdd0</c>, passed to it as <c>--root</c>. It is the
    /// console's own filesystem as far as qwark is concerned, so it is the user's data and not the
    /// release's, even though nobody edits it by hand.
    /// </summary>
    public static string Rpcs3Root => Path.Combine(Root, "qwark-rpcs3-root");

    /// <summary>The user's own game layout, which is used instead of the shipped one when it exists.</summary>
    public static string GameLayoutOverride => Path.Combine(Root, "gamelayout.json");

    public static string ShippedMods => Path.Combine(Application, "mods");

    public static string ShippedData => Path.Combine(Application, "data");

    public static string ShippedGameLayout => Path.Combine(ShippedData, "gamelayout.json");

    /// <summary>A path from the settings file: an absolute one as written, a bare name in the data folder.</summary>
    public static string InData(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(Root, path);

    private static OSPlatform Current =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows
        : OperatingSystem.IsMacOS() ? OSPlatform.OSX
        : OSPlatform.Linux;

    /// <summary>A path a person typed: quoted by a file manager's "copy as path", or nothing at all.</summary>
    private static string? Clean(string? path)
    {
        var trimmed = (path ?? string.Empty).Trim().Trim('"').Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string? Value(Func<string, string?> environment, string name)
    {
        var value = environment(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
