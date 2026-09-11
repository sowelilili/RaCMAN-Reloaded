using System.Runtime.InteropServices;

namespace RaCMAN.App;

/// <summary>
/// Windows only: the client is a GUI application, so double-clicking it opens no black console
/// window behind the ImGui one. A GUI application started from a terminal has nowhere to print,
/// though, and the headless runs (<c>--help</c>, <c>--exit-after</c> and its summary lines) are
/// meant to be read there, so the process attaches itself to the console it was started from and
/// opens its output streams again on it.
/// <para>
/// Everywhere else this does nothing: on Linux and macOS the executable already writes to the
/// terminal that started it.
/// </para>
/// </summary>
internal static class ParentConsole
{
    /// <summary>AttachConsole's "the console of the process that started this one".</summary>
    private const int ParentProcess = -1;

    private const int StandardOutput = -11;
    private const int StandardError = -12;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int which, nint handle);

    /// <summary>
    /// Attaches to the terminal's console and points Console.Out and Console.Error at it. Called
    /// before anything writes a line, since .NET opens the writer behind Console.Out once and
    /// keeps it. A run whose output was redirected into a file or a pipe keeps that redirection:
    /// attaching a console replaces the standard handles, so the ones the launcher handed over are
    /// read first and put back.
    /// </summary>
    public static void Attach()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            nint output = GetStdHandle(StandardOutput);
            nint error = GetStdHandle(StandardError);

            if (!AttachConsole(ParentProcess)) return;

            if (IsReal(output)) SetStdHandle(StandardOutput, output);
            if (IsReal(error)) SetStdHandle(StandardError, error);

            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                       or IOException or UnauthorizedAccessException)
        {
            // Printing to the terminal is a convenience; a window that opens anyway is the point.
        }
    }

    /// <summary>True for a handle the launcher actually set, rather than the nothing a GUI process starts with.</summary>
    private static bool IsReal(nint handle) => handle != 0 && handle != -1;
}
