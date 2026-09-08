using System.Globalization;
using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

// --exit-after and --fake-server exist so the window can be smoke tested without a console.
double exitAfter = 0;
int startPanel = 0;
string? connectTo = null;
string? fakeScript = null;
bool fakeServer = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--exit-after" when i + 1 < args.Length:
            double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out exitAfter);
            break;
        case "--panel" when i + 1 < args.Length:
            int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out startPanel);
            break;
        case "--connect" when i + 1 < args.Length:
            connectTo = args[++i];
            break;
        case "--fake-server":
            fakeServer = true;
            break;
        case "--fake-script" when i + 1 < args.Length:
            fakeScript = args[++i];
            break;
        case "--help":
        case "-h":
            Console.WriteLine("RaCMAN Reloaded");
            Console.WriteLine("  --connect <host>       connect to qwark on start");
            Console.WriteLine("  --fake-server          run the in-process fake qwark and connect to it");
            Console.WriteLine("  --fake-script <steps>  drive the fake console's session: " + FakeScript.Usage);
            Console.WriteLine("  --panel <n>            open on panel n: 0 connection, 1 game, 2 positions,");
            Console.WriteLine("                         3 unlocks, 4 level flags, 5 memory, 6 mods,");
            Console.WriteLine("                         7 save files, 8 combos, 9 input display");
            Console.WriteLine("  --exit-after <secs>    close the window after this many seconds");
            return 0;
    }
}

var settings = Settings.Load();
using var state = new AppState(settings);

FakeQwarkServer? fake = null;
using var scriptCts = new CancellationTokenSource();

if (fakeServer)
{
    fake = new FakeQwarkServer();
    fake.Start();
    connectTo ??= "127.0.0.1";
    Console.WriteLine($"Fake qwark listening on 127.0.0.1:{fake.Port}");
    state.Run(() => state.Client.ConnectAsync(connectTo, fake.Port));
    if (fakeScript is not null) _ = FakeScript.RunAsync(fake, fakeScript, scriptCts.Token);
}
else if (connectTo is not null)
{
    settings.LastHost = connectTo;
    state.Run(() => state.Client.ConnectAsync(connectTo));
}
else if (fakeScript is not null)
{
    Console.Error.WriteLine("--fake-script needs --fake-server");
}

int exitCode;
using (var window = new AppWindow(state, exitAfter, startPanel))
{
    window.Run();
    exitCode = window.Failure is null ? 0 : 1;
    if (window.Failure is not null)
    {
        Console.Error.WriteLine($"RaCMAN Reloaded exited on {window.Failure.GetType().Name}: {window.Failure.Message}");
    }
}

scriptCts.Cancel();

if (exitAfter > 0)
{
    var sess = state.Session;
    uint readout0 = sess is not null && sess.Readout.Length > 0 ? sess.Readout[0] : 0u;
    uint padMask = sess?.PadMask ?? 0u;
    Console.WriteLine($"summary: connected={state.Connected} telemetry={(state.Telemetry is null ? "none" : "yes")} " +
                      $"features={state.Describe.Features.Length} planets={state.Planets.Length} " +
                      $"slots={state.Positions.Slots.Length} mods={state.ConsoleMods.Length} " +
                      $"watches={state.Watches.Length} combos={state.Combos.Length} " +
                      $"unlocks={state.Unlocks.Unlocks.Length} flagbytes={LevelFlagsPanel.LoadedByteCount} " +
                      $"mobyrows={MemoryPanel.MobyRowCount} skins={SkinLibrary.List().Length} " +
                      $"mobylayouts={MobyLayouts.All.Count} skin='{InputDisplayPanel.Status}' " +
                      $"savehelper={state.Describe.HasSaveFileHelper} savefiles={SaveFilesPanel.Summary} " +
                      $"readout0={readout0} padmask=0x{padMask:X} tcpfallback={state.Client.TelemetryViaTcp}");
}

fake?.Dispose();
settings.Save();
return exitCode;
