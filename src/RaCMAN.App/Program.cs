using System.Globalization;
using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;
using Velopack;

// First, before anything reads a file or draws a pixel: this is where an install, an update and an
// uninstall are carried out, and where a process started by the updater for one of those exits.
VelopackApp.Build().Run();

// --exit-after and --fake-server exist so the window can be smoke tested without a console.
double exitAfter = 0;
int startPanel = 0;
string? connectTo = null;
string? fakeScript = null;
string? dataDir = null;
bool fakeServer = false;
bool fakeRpcs3 = false;
bool padWindow = false;
bool noUpdateCheck = false;

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
        case AppPaths.Flag when i + 1 < args.Length:
            dataDir = args[++i];
            break;
        case "--no-update-check":
            noUpdateCheck = true;
            break;
        case "--help":
        case "-h":
            Console.WriteLine($"RaCMAN Reloaded {AppVersion.Current}");
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
            Console.WriteLine("  --data-dir <path>      keep this run's settings, mods and savefiles there");
            Console.WriteLine("  --no-update-check      do not look for a new version of this client");
            return 0;
    }
}

// Before anything opens a file: the data folder is where every one of them lives.
if (dataDir is not null) AppPaths.Use(dataDir);
AppPaths.EnsureRoot();

// Once, on the first start that finds an empty data folder: whatever a build that kept its files
// beside the executable left there is copied across. The originals are never removed.
var migration = DataFolderMigration.Run(AppPaths.Application, AppPaths.Root);

var settings = Settings.Load();

// Everything that would reach the network is off for a run driven by the headless flags, and for
// one that was told not to look.
bool updatesAllowed = !fakeServer && !fakeRpcs3 && !noUpdateCheck && exitAfter <= 0;

// --pad-window forces the mode on for a smoke run without leaving it on in the settings file.
var padWindowWas = padWindow ? settings.InputMode : (InputDisplayMode?)null;
if (padWindow) settings.InputMode = InputDisplayMode.Window;

using var state = new AppState(settings, updatesAllowed);

if (migration.MovedAnything) state.AddToast(migration.Describe(AppPaths.Root));

// Once a day, and never for a run that must not touch the network.
state.Updates.StartupCheck();

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

    // Exactly what the Connect button does: against a console that means qwark first and webMAN
    // only if nothing answers, and against the RPCS3 helper there is nothing to load.
    if (settings.Rpcs3Target) state.Run(() => state.Client.ConnectAsync(connectTo));
    else ConnectionPanel.ConnectToPs3(state, connectTo);
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
            bool up = await state.Rpcs3.WaitForPortAsync(ConnectionPanel.HelperStartupWait).ConfigureAwait(false);
            if (!up && state.Rpcs3.DiedOnStartup)
            {
                // Same rule as the Connect button: a helper that died on the way up is reported,
                // not reconnected to.
                string why = state.Rpcs3.Problem ?? $"{Rpcs3Host.ExeName} exited before it opened its port";
                Console.Error.WriteLine($"rpcs3: {why}");
                state.Post(() => state.AddToast(why, ToastKind.Error));
                return;
            }

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

    Console.WriteLine($"summary: version={AppVersion.Current} data=\"{AppPaths.Root}\" " +
                      $"target={target}{helper} " +
                      $"connected={state.Connected} telemetry={(state.Telemetry is null ? "none" : "yes")} " +
                      $"emulator={sess?.IsEmulator ?? false} nocodepatches={sess?.CodePatchesUnsupported ?? false} " +
                      $"features={state.Describe.Features.Length} planets={state.Planets.Length} " +
                      $"slots={state.Positions.Slots.Length} mods={state.ConsoleMods.Length} " +
                      $"watches={state.Watches.Length} combos={state.Combos.Length} " +
                      $"unlocks={state.Unlocks.Unlocks.Length} flagbytes={LevelFlagsPanel.LoadedByteCount} " +
                      $"mobyrows={MemoryPanel.MobyRowCount} skins={SkinLibrary.List().Length} " +
                      $"mobylayouts={MobyLayouts.All.Count} skin='{InputDisplayPanel.Status}' " +
                      $"savehelper={state.SaveFile.Supported}/{state.SaveFile.Size} " +
                      $"savefiles={SaveFilesPanel.Summary} " +
                      $"readout0={readout0} padmask=0x{padMask:X} input={settings.InputMode} " +
                      $"tcpfallback={state.Client.TelemetryViaTcp} " +
                      $"autosplitevents={state.AutosplitEvents.Length} " +
                      $"autosplit={state.Autosplitter.Received}/{state.Autosplitter.Acted}" +
                      $"+{state.Autosplitter.Adjustments}adj " +
                      $"livesplit={state.LiveSplit.Status} version=\"{state.LiveSplit.Version ?? "(none)"}\" " +
                      $"tooold={state.LiveSplit.TooOld} sent={state.LiveSplit.CommandsSent} " +
                      $"unanswered=[{string.Join(" ", state.LiveSplit.Unanswered)}]");

    // What LiveSplit itself said about the timer, which is the whole of the planet route's input,
    // so a headless run can be checked against LiveSplit.
    var view = state.Autosplitter.View;
    Console.WriteLine($"livesplit-timer: phase={view.Phase} index={view.SplitIndex} "
                      + $"current=\"{view.CurrentSplit}\" upcoming=\"{view.UpcomingSplit}\"");

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
