using System.Net;
using System.Net.Sockets;
using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The RPCS3 helper process: where it is found, that it is started once and stopped cleanly, and
/// that a qwark already serving the port is connected to rather than fought over.
/// <para>
/// The stub is a script that prints the two lines qwark-rpcs3.exe prints and then waits on stdin
/// for "exit", which is the whole of the contract this class has with the helper.
/// </para>
/// </summary>
public class Rpcs3HostTests
{
    [Fact]
    public void FindPrefersTheExecutableBesideTheClient()
    {
        var folder = TempFolder();
        try
        {
            string beside = Path.Combine(folder, Rpcs3Host.ExeName);
            File.WriteAllText(beside, "not really an executable");

            Assert.Equal(beside, Rpcs3Host.Find(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void FindFallsBackToTheSiblingQwarkRepo()
    {
        var folder = TempFolder();
        try
        {
            // The development layout: the client runs out of its build output, and qwark is a
            // sibling of the repo that build output is in.
            string output = Path.Combine(folder, "racman-reloaded", "src", "RaCMAN.App", "bin", "Release", "net8.0");
            Directory.CreateDirectory(output);
            Directory.CreateDirectory(Path.Combine(folder, "qwark"));
            string sibling = Path.Combine(folder, "qwark", Rpcs3Host.ExeName);
            File.WriteAllText(sibling, "not really an executable");

            Assert.Equal(sibling, Rpcs3Host.Find(output));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void FindTakesTheSettingsOverrideWhenThereIsNothingElse()
    {
        var folder = TempFolder();
        try
        {
            string elsewhere = Path.Combine(folder, "somewhere-else.exe");
            File.WriteAllText(elsewhere, "not really an executable");

            Assert.Equal(elsewhere, Rpcs3Host.Find(folder, elsewhere));
            Assert.Null(Rpcs3Host.Find(folder, Path.Combine(folder, "missing.exe")));
            Assert.Null(Rpcs3Host.Find(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void EnsureSaysSoWhenThereIsNoHelperToStart()
    {
        var folder = TempFolder();
        try
        {
            using var host = new Rpcs3Host(folder, FreePort());

            Assert.False(host.Ensure(null, Rpcs3Host.DefaultPinePort, out string message));
            Assert.Contains(Rpcs3Host.ExeName, message, StringComparison.Ordinal);
            Assert.NotNull(host.Problem);
            Assert.False(host.IsRunning);
            Assert.Equal("not started", host.Status);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task StartsTheHelperKeepsItsOutputAndStopsItOnStdin()
    {
        var folder = TempFolder();
        try
        {
            string stub = WriteStub(folder);
            using var host = new Rpcs3Host(folder, FreePort());

            Assert.True(host.Ensure(stub, 28012, out string message));
            Assert.Contains("Started", message, StringComparison.Ordinal);
            Assert.True(host.IsRunning);
            Assert.NotNull(host.Pid);
            Assert.Equal($"running (pid {host.Pid})", host.Status);

            // The helper's stdout reaches the panel, and its "pine:" line is picked out of it —
            // from "pine:" onwards, because the real helper stamps every log line with a time.
            Assert.True(await Waits(() => host.LastPineLine is not null),
                "the stub's pine: line never arrived: " + string.Join(" | ", host.Lines));
            Assert.Equal("pine: waiting for RPCS3", host.LastPineLine);
            Assert.Contains(host.Lines, line => line.Contains("qwark-rpcs3", StringComparison.Ordinal));

            host.Stop();

            Assert.False(host.IsRunning);
            Assert.Null(host.Pid);
            Assert.Equal(0, host.ExitCode);
            Assert.Equal("exited (code 0)", host.Status);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void DoesNotStartASecondCopyWhenTheQwarkPortIsAlreadyServed()
    {
        var folder = TempFolder();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            string stub = WriteStub(folder);
            using var host = new Rpcs3Host(folder, port);

            Assert.True(Rpcs3Host.PortInUse(port));
            Assert.True(host.Ensure(stub, 28012, out string message));

            Assert.Contains(port.ToString(), message, StringComparison.Ordinal);
            Assert.True(host.Adopted);
            Assert.False(host.IsRunning);
            Assert.Equal("not started", host.Status);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void StoppingSomethingThatWasNeverStartedIsHarmless()
    {
        var folder = TempFolder();
        try
        {
            var host = new Rpcs3Host(folder, FreePort());
            host.Stop();
            host.Dispose();

            Assert.False(host.IsRunning);
            Assert.Null(host.ExitCode);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// A stand-in for qwark-rpcs3.exe: the banner, a pine status line, then stdin until "exit",
    /// which is exactly how the real helper and the qwark-host simulator both behave.
    /// </summary>
    private static string WriteStub(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            string cmd = Path.Combine(folder, "stub-qwark-rpcs3.cmd");
            File.WriteAllText(cmd, string.Join("\r\n",
                "@echo off",
                "echo qwark-rpcs3 ready on 9673",
                "echo [05:37:25] pine: waiting for RPCS3",
                ":loop",
                "set \"line=\"",
                "set /p line=",
                "if not defined line exit /b 0",
                "if /i \"%line%\"==\"exit\" exit /b 0",
                "goto loop",
                string.Empty));
            return cmd;
        }

        string sh = Path.Combine(folder, "stub-qwark-rpcs3.sh");
        File.WriteAllText(sh, string.Join("\n",
            "#!/bin/sh",
            "echo 'qwark-rpcs3 ready on 9673'",
            "echo '[05:37:25] pine: waiting for RPCS3'",
            "while read -r line; do",
            "  [ \"$line\" = exit ] && exit 0",
            "done",
            "exit 0",
            string.Empty));
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    /// <summary>A port nothing is listening on, so the "already served" path is not taken by accident.</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> Waits(Func<bool> condition, int timeoutMs = 5000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 25)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }

        return condition();
    }

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "racman-rpcs3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
