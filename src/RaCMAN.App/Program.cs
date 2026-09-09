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
bool fakeRpcs3 = false;
bool padWindow = false;

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
        case "--game-section" when i + 1 < args.Length:
            GamePanel.RequestedSubPage = args[++i];
            break;
        case "--fake-server":
            fakeServer = true;
            break;
        case "--fake-rpcs3":
            fakeServer = true;
            fakeRpcs3 = true;
            break;
        case "--pad-window":
            padWindow = true;
            break;
        case "--fake-script" when i + 1 < args.Length:
            fakeScript = args[++i];
            break;
        case "--help":
        case "-h":
            Console.WriteLine("RaCMAN Reloaded");
            Console.WriteLine("  --connect <host>       connect to qwark on start");
            Console.WriteLine("  --fake-server          run the in-process fake qwark and connect to it");
            Console.WriteLine("  --fake-rpcs3           as --fake-server, with the emulator and no-code-patches flags set");
            Console.WriteLine("  --fake-script <steps>  drive the fake console's session: " + FakeScript.Usage);
            Console.WriteLine("  --panel <n>            open on panel n: 0 connection, 1 game, 2 positions,");
            Console.WriteLine("                         3 unlocks, 4 level flags, 5 memory, 6 mods,");
            Console.WriteLine("                         7 save files, 8 combos, 9 autosplitter,");
            Console.WriteLine("                         10 input display, 11 settings");
            Console.WriteLine("  --game-section <name>  open the Game panel on that side sub-page (Debug, Cosmetics, ...)");
            Console.WriteLine("  --pad-window           show the input display in its own OS window");
            Console.WriteLine("  --exit-after <secs>    close the window after this many seconds");
            return 0;
    }
}

var settings = Settings.Load();

// --pad-window forces the mode on for a smoke run without leaving it on in the settings file.
var padWindowWas = padWindow ? settings.InputMode : (InputDisplayMode?)null;
if (padWindow) settings.InputMode = InputDisplayMode.Window;

using var state = new AppState(settings);

FakeQwarkServer? fake = null;
using var scriptCts = new CancellationTokenSource();

if (fakeServer)
{
    fake = new FakeQwarkServer();

    // The RPCS3 look, from the flags alone: an emulator that refuses code patches.
    fake.Emulator = fakeRpcs3;
    fake.NoCodePatches = fakeRpcs3;

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
else if (settings.Rpcs3Target)
{
    // The RPCS3 target is a helper on this PC, so there is nothing to type and nothing to wait
    // for: start it and connect, exactly as the panel's Connect button would.
    bool ready = state.Rpcs3.Ensure(settings.Rpcs3QwarkPath, settings.Rpcs3PinePort, out string rpcs3Message);
    Console.WriteLine($"rpcs3: {rpcs3Message}");
    if (ready)
    {
        state.Run(async () =>
        {
            await state.Rpcs3.WaitForPortAsync(ConnectionPanel.HelperStartupWait).ConfigureAwait(false);
            await state.Client.ConnectAsync(ConnectionPanel.LocalHost).ConfigureAwait(false);
        });
    }
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

    // Which qwark this run talked to, and — for RPCS3 — what became of the helper process. The
    // helper is still alive here: the AppState that owns it is disposed after this block.
    string target = settings.Rpcs3Target ? Settings.Rpcs3TargetName : Settings.Ps3TargetName;
    string helper = settings.Rpcs3Target
        ? $" rpcs3helper=\"{state.Rpcs3.Status}{(state.Rpcs3.Adopted ? ", adopted" : string.Empty)}\""
          + $" rpcs3pine=\"{state.Rpcs3.LastPineLine ?? "(none)"}\""
        : string.Empty;

    Console.WriteLine($"summary: target={target}{helper} " +
                      $"connected={state.Connected} telemetry={(state.Telemetry is null ? "none" : "yes")} " +
                      $"emulator={sess?.IsEmulator ?? false} nocodepatches={sess?.CodePatchesUnsupported ?? false} " +
                      $"features={state.Describe.Features.Length} planets={state.Planets.Length} " +
                      $"slots={state.Positions.Slots.Length} mods={state.ConsoleMods.Length} " +
                      $"watches={state.Watches.Length} combos={state.Combos.Length} " +
                      $"unlocks={state.Unlocks.Unlocks.Length} flagbytes={LevelFlagsPanel.LoadedByteCount} " +
                      $"mobyrows={MemoryPanel.MobyRowCount} skins={SkinLibrary.List().Length} " +
                      $"mobylayouts={MobyLayouts.All.Count} skin='{InputDisplayPanel.Status}' " +
                      $"savehelper={state.Describe.HasSaveFileHelper} savefiles={SaveFilesPanel.Summary} " +
                      $"readout0={readout0} padmask=0x{padMask:X} input={settings.InputMode} " +
                      $"tcpfallback={state.Client.TelemetryViaTcp} " +
                      $"autosplitevents={state.AutosplitEvents.Length} " +
                      $"autosplit={state.Autosplitter.Received}/{state.Autosplitter.Acted}" +
                      $"+{state.Autosplitter.Adjustments}adj " +
                      $"livesplit={state.LiveSplit.Status} sent={state.LiveSplit.CommandsSent} " +
                      $"unanswered=[{string.Join(" ", state.LiveSplit.Unanswered)}]");

    // Which run the split names were read from, and what LiveSplit itself said about the timer:
    // the whole of the planet route's input, so a headless run can be checked against LiveSplit.
    var runs = state.Autosplitter.Runs.State;
    var view = state.Autosplitter.View;
    Console.WriteLine($"livesplit-run: candidates={runs.Candidates} manual={runs.Manual} "
                      + $"run={runs.Summary} problem={runs.Problem ?? "(none)"}");
    Console.WriteLine($"livesplit-timer: phase={view.Phase} index={view.SplitIndex} "
                      + $"current=\"{view.CurrentSplit}\" upcoming=\"{view.UpcomingSplit}\" "
                      + $"from={view.UpcomingSourceLabel}");

    // The run-event log, so a headless run says what it decided and not only how much of it.
    foreach (var entry in state.Autosplitter.Log())
    {
        Console.WriteLine($"autosplit-log t={entry.TimeMs / 1000.0:0.000}s {entry.Kind} "
                          + $"\"{entry.Event}\" -> {entry.Action}");
    }
}

if (padWindowWas is { } previousMode) settings.InputMode = previousMode;

fake?.Dispose();
settings.Save();
return exitCode;
