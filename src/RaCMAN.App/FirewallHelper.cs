using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace RaCMAN.App;

/// <summary>
/// Launches the bundled windows-firewall.ps1 to add an inbound UDP allow rule, so the console's
/// telemetry reaches the client. Windows-only; a no-op elsewhere. The script self-elevates, so the
/// user sees one UAC prompt and nothing happens if they decline.
/// </summary>
public static class FirewallHelper
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>The script that lives beside the executable in a published build.</summary>
    public static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "windows-firewall.ps1");

    public static bool ScriptPresent => IsSupported && File.Exists(ScriptPath);

    /// <summary>
    /// Kicks off the elevated helper. Returns a short status for a toast: the OS approval prompt
    /// carries the real outcome, so success here only means the request was launched.
    /// </summary>
    public static (bool ok, string message) RequestRule()
    {
        if (!IsSupported)
            return (false, "The firewall helper is Windows-only.");

        if (!ScriptPresent)
            return (false, "windows-firewall.ps1 isn't next to the app (dev build?). Run it from a published copy.");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{ScriptPath}\"",
                UseShellExecute = true,   // required for the UAC "runas" elevation the script does
            };
            Process.Start(psi);
            return (true, "Approve the Windows administrator prompt to allow inbound UDP.");
        }
        catch (Exception ex)
        {
            return (false, $"Couldn't launch the firewall helper: {ex.Message}");
        }
    }
}
