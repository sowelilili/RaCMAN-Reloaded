using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RaCMAN.App;

/// <summary>
/// Shows a folder in whatever the platform calls its file manager: explorer.exe on Windows,
/// xdg-open on Linux, open on macOS. This is what the panels offer instead of printing the
/// absolute path of a library, which was three wrapped lines of text nobody could act on.
///
/// The folder is created before it is opened. A library folder only exists once something has
/// been put in it, and a button that does nothing on a fresh install is worse than no button.
/// </summary>
public static class FileExplorer
{
    /// <summary>False where there is no file manager to ask, which greys the buttons out.</summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>
    /// What would be run to show <paramref name="folder"/>: the program and its one argument, or
    /// null when there is nothing to run or no folder to show. Pure, and it takes the platform
    /// rather than reading it, so all three mappings can be checked from whichever platform the
    /// tests happen to run on.
    /// </summary>
    public static (string Program, string Argument)? CommandFor(string folder, OSPlatform platform)
    {
        // Trimmed the same way the path boxes are read elsewhere: a pasted path often arrives
        // quoted, and a quote reaches the file manager as part of the name.
        string target = (folder ?? string.Empty).Trim().Trim('"');
        if (target.Length == 0) return null;

        if (platform == OSPlatform.Windows) return ("explorer.exe", target);
        if (platform == OSPlatform.Linux) return ("xdg-open", target);
        if (platform == OSPlatform.OSX) return ("open", target);
        return null;
    }

    /// <summary>
    /// Opens the folder, creating it first if it is not there yet. The message is for a toast:
    /// nothing is reported on success, because the window that appears is the answer.
    /// </summary>
    public static (bool ok, string message) Open(string folder)
    {
        var command = CommandFor(folder, Current);
        if (command is null) return (false, "There is no file manager to open on this platform.");

        var (program, target) = command.Value;
        try
        {
            Directory.CreateDirectory(target);

            // UseShellExecute false so this is one process start and not the shell's idea of what
            // the path means; the file manager takes the folder as its only argument everywhere.
            var info = new ProcessStartInfo(program) { UseShellExecute = false };
            info.ArgumentList.Add(target);
            using var process = Process.Start(info);

            return process is null
                ? (false, $"Could not open {target}: {program} did not start")
                : (true, $"Opened {target}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException
                                      or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or InvalidOperationException)
        {
            return (false, $"Could not open {target}: {ex.Message}");
        }
    }

    /// <summary>This platform in the terms <see cref="CommandFor"/> takes.</summary>
    private static OSPlatform Current =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows
        : OperatingSystem.IsLinux() ? OSPlatform.Linux
        : OperatingSystem.IsMacOS() ? OSPlatform.OSX
        : OSPlatform.Create("UNKNOWN");
}
