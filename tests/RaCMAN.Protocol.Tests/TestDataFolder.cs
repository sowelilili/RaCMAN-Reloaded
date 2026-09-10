using System.Runtime.CompilerServices;
using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Gives the whole test run a data folder of its own, in the temp directory, before a single test
/// has had the chance to look at one. Without this a test that saves settings or writes a colour
/// preset would land in the real <c>%APPDATA%\RaCMAN Reloaded</c> and quietly rewrite what someone
/// is using.
/// <para>
/// Both ways of pointing the client at a folder are used: the environment variable, because that is
/// what a child process started by a test would read, and <see cref="AppPaths.Use"/>, because that
/// is what settles it for this process even if something has already resolved the default.
/// </para>
/// </summary>
internal static class TestDataFolder
{
    private static string? _root;

    /// <summary>The folder this run is using. Created on first use and removed when the run ends.</summary>
    public static string Root => _root ?? throw new InvalidOperationException("The test data folder is not set up.");

    [ModuleInitializer]
    internal static void Initialise()
    {
        _root = Path.Combine(Path.GetTempPath(), "racman-reloaded-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        Environment.SetEnvironmentVariable(AppPaths.EnvironmentVariable, _root);
        AppPaths.Use(_root);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp folder is not worth failing a run over.
            }
        };
    }
}
