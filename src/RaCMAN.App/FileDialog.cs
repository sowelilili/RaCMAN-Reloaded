using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RaCMAN.App;

/// <summary>
/// The platform's own "open file" dialog, without carrying a package for it: comdlg32 on Windows,
/// zenity or kdialog on Linux, osascript on macOS.
///
/// A modal dialog owns the thread it runs on until the user is finished, so none of this may
/// happen on the render thread: the window would stop drawing and stop answering telemetry.
/// Everything below runs elsewhere and hands the path back through a Task; the caller is expected
/// to bring it home with <see cref="AppState.Post"/> before it touches any ImGui state.
/// </summary>
public static class FileDialog
{
    // Resolved once: the Browse button asks IsSupported every frame, and walking PATH per frame
    // to find zenity would be a file-system hit per frame for an answer that cannot change.
    private static readonly Lazy<string?> LinuxTool = new(() => Which("zenity") ?? Which("kdialog"));

    /// <summary>
    /// False when this machine has no dialog to show (a Linux box with neither zenity nor
    /// kdialog), which is when the caller has to keep the paste-a-path box as the way in.
    /// </summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows()
        || OperatingSystem.IsMacOS()
        || (OperatingSystem.IsLinux() && LinuxTool.Value is not null);

    /// <summary>
    /// Shows the dialog and answers the chosen path, or null when the user cancelled or there is
    /// no dialog to show. A cancel is not an error and never throws.
    /// </summary>
    /// <param name="title">Window title, where the platform lets us set one.</param>
    /// <param name="filterDescription">What the extension is, e.g. "ZIP files".</param>
    /// <param name="extension">The extension to offer, with or without the dot.</param>
    public static Task<string?> OpenAsync(string title, string filterDescription, string extension)
    {
        extension = (extension ?? string.Empty).TrimStart('.');

        if (OperatingSystem.IsWindows()) return WindowsAsync(title, filterDescription, extension);
        if (OperatingSystem.IsMacOS()) return Task.Run(() => RunTool("osascript", "-e", $"POSIX path of (choose file of type {{\"{extension}\"}})"));
        if (OperatingSystem.IsLinux()) return Task.Run(() => RunLinux(title, extension));

        return Task.FromResult<string?>(null);
    }

    // ---------------------------------------------------------------- Windows

    private const int OfnNoChangeDir = 0x00000008;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnExplorer = 0x00080000;

    /// <summary>The common dialog wants a UTF-16 buffer it can write the path into.</summary>
    private const int PathBufferChars = 1024;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public string? Filter;
        public string? CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public string? FileTitle;
        public int MaxFileTitle;
        public string? InitialDir;
        public string? Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public string? DefaultExtension;
        public IntPtr CustomData;
        public IntPtr Hook;
        public string? TemplateName;
        public IntPtr Reserved;
        public int ReservedInt;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName parameters);

    /// <summary>Zero after a cancel, an error code after a failure; the two look the same otherwise.</summary>
    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    /// <summary>
    /// The common dialog is a COM host underneath, so it needs a single-threaded apartment; the
    /// thread pool is multi-threaded, hence a thread of its own for the life of the dialog.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static Task<string?> WindowsAsync(string title, string filterDescription, string extension)
    {
        var completion = new TaskCompletionSource<string?>();

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(ShowWindowsDialog(title, filterDescription, extension));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "file-dialog",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [SupportedOSPlatform("windows")]
    private static string? ShowWindowsDialog(string title, string filterDescription, string extension)
    {
        // The filter is a run of NUL-separated pairs ended by an empty one, not a normal string.
        string filter = $"{filterDescription} (*.{extension})\0*.{extension}\0All files (*.*)\0*.*\0\0";

        IntPtr buffer = Marshal.AllocHGlobal(PathBufferChars * sizeof(char));
        try
        {
            // An empty initial file name; the dialog appends the chosen path here.
            Marshal.WriteInt16(buffer, 0, 0);

            var parameters = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),

                // No owner window: fishing the HWND out of GLFW would tie this file to the window,
                // and an ownerless dialog is still modal to the thread that shows it.
                Owner = IntPtr.Zero,
                Filter = filter,
                FilterIndex = 1,
                File = buffer,
                MaxFile = PathBufferChars,
                Title = title,
                DefaultExtension = extension,
                Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir,
            };

            if (!GetOpenFileName(ref parameters))
            {
                // False is both "cancelled" and "failed"; only the error code tells them apart,
                // and a failure that looked like a cancel would be a button that does nothing.
                int error = CommDlgExtendedError();
                if (error != 0) throw new InvalidOperationException($"the common dialog failed (0x{error:X4})");
                return null;
            }

            string? path = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---------------------------------------------------------------- Linux and macOS

    private static string? RunLinux(string title, string extension)
    {
        string? tool = LinuxTool.Value;
        if (tool is null) return null;

        return Path.GetFileNameWithoutExtension(tool).Equals("kdialog", StringComparison.OrdinalIgnoreCase)
            ? RunTool(tool, "--getopenfilename", ".", $"*.{extension}")
            : RunTool(tool, "--file-selection", $"--title={title}", $"--file-filter=*.{extension}");
    }

    /// <summary>Runs a dialog helper and reads the path off its stdout. A non-zero exit is a cancel.</summary>
    private static string? RunTool(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;

            string output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0) return null;

            // zenity can answer several paths separated by '|'; we only ever want one.
            string path = output.Split('|')[0].Trim();
            return path.Length == 0 ? null : path;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>The first PATH entry holding an executable of that name, or null.</summary>
    private static string? Which(string tool)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(folder.Trim(), tool);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not this client's problem.
            }
        }

        return null;
    }
}
