using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

public static class ConnectionPanel
{
    /// <summary>Where a helper on this PC is: RPCS3 and qwark-rpcs3.exe both run here.</summary>
    public const string LocalHost = "127.0.0.1";

    /// <summary>How long a just-started helper gets to open the qwark port before the first connect.</summary>
    public static readonly TimeSpan HelperStartupWait = TimeSpan.FromSeconds(5);

    private static string _host = string.Empty;
    private static string _sprxPath = string.Empty;
    private static string _notifyText = "Hello from RaCMAN Reloaded";
    private static int _slot = 5;
    private static int _pinePort = Rpcs3Host.DefaultPinePort;
    private static bool _confirmBootInstall;
    private static bool _initialised;

    /// <summary>Puts a host into the address box (the settings import uses it), whether or not the panel has been drawn yet.</summary>
    public static void UseHost(string host)
    {
        _host = host;
        _initialised = true;
    }

    public static void Draw(AppState state)
    {
        if (!_initialised)
        {
            _host = state.Settings.LastHost;
            _sprxPath = state.Settings.SprxPath;
            _slot = state.Settings.WebManSlot;
            _pinePort = state.Settings.Rpcs3PinePort;
            _initialised = true;
        }

        Ui.Heading("Connection");

        bool rpcs3 = DrawTarget(state);

        if (!rpcs3)
        {
            ImGui.SetNextItemWidth(260);
            ImGui.InputText("PS3 address", ref _host, 64);
            ImGui.SameLine();
        }

        bool connecting = state.Client.WantsConnection && !state.Connected;
        ImGui.BeginDisabled(state.Connected || connecting);
        if (ImGui.Button("Connect"))
        {
            if (rpcs3) ConnectToRpcs3(state);
            else
            {
                state.Settings.LastHost = _host;
                state.Settings.Save();
                ConnectToPs3(state, _host);
            }
        }

        ImGui.EndDisabled();
        ImGui.SameLine();

        ImGui.BeginDisabled(!state.Client.WantsConnection);
        if (ImGui.Button("Disconnect"))
        {
            state.Run(() => state.Client.DisconnectAsync());
            state.ResetPanels();
        }

        ImGui.EndDisabled();

        bool autoReconnect = state.Client.AutoReconnect;
        if (ImGui.Checkbox("Reconnect automatically", ref autoReconnect))
        {
            state.Client.AutoReconnect = autoReconnect;
            state.Settings.AutoReconnect = autoReconnect;
            state.Settings.Save();
        }

        ImGui.Spacing();
        var statusColour = state.Connected ? Ui.Green : state.Client.WantsConnection ? Ui.Yellow : Ui.Grey;
        ImGui.TextColored(statusColour, state.StatusLine());

        if (state.Connected)
        {
            var session = state.Session;
            Ui.DebugHint($"Telemetry port {state.Client.TelemetryPort} | {(state.Telemetry is null ? "no packet yet" : $"{state.Telemetry.Watches.Length} watch values")}");
            if (state.Client.TelemetryViaTcp)
            {
                Ui.Warning("Live state is coming over TCP: the console's UDP telemetry isn't reaching this PC. "
                           + "Everything works, but for lower-latency updates allow RaCMAN through the firewall.");
                DrawFirewallButton(state);
            }
            Ui.DebugHint($"Planet {session.CurrentPlanet} | slot {session.SelectedSlot} | position {session.PosX:0.##}, {session.PosY:0.##}, {session.PosZ:0.##}");
            if (session.PreviousPending) Ui.Warning("A previous session is waiting to be re-applied.");
        }

        DrawStaleBuild(state);
        DrawVersions(state);

        if (rpcs3)
        {
            DrawRpcs3(state);
            DrawConsoleSection(state);
            return;
        }

        // Nothing here for anybody who is not developing qwark: in webMAN mode Connect does the
        // whole of it by itself, asking webMAN and loading the module only when nothing answers on
        // qwark's port. The buttons that do the two halves by hand, the file they send and the slot
        // it goes into are all detail, so that much only exists with debug information on.
        if (!Ui.Debug)
        {
            // The boxes that edit these two are hidden, so the stored values are the only ones
            // there are; taking them here also picks up an import that landed after this panel was
            // first drawn.
            _sprxPath = state.Settings.SprxPath;
            _slot = state.Settings.WebManSlot;

            // Standalone is the mode for a console that loads qwark itself, and the boot plugin is
            // how it comes to: that install is the setup step, not a debug detail, so it is the one
            // part of this section a standalone user sees.
            if (state.Settings.StandaloneConnection) DrawBootInstall(state, withPathBox: true);

            DrawConsoleSection(state);
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Load qwark through webMAN");

        Ui.Hint($"Uploads qwark.sprx to {WebManLoader.RemotePath} over FTP, then asks webMAN to load it into a VSH slot. "
                + "In webMAN mode Connect does this on its own when nothing answers on qwark's port.");

        ImGui.SetNextItemWidth(360);
        ImGui.InputText("qwark.sprx path", ref _sprxPath, 512);

        // The default is qwark.sprx beside the executable, which is where the release layout puts it.
        string sprx = Ps3Connect.ResolveSprx(_sprxPath);

        // The resolved path is absolute and long, so both spellings of this line wrap.
        if (File.Exists(sprx)) Ui.Hint(sprx);
        else Ui.Warning($"{sprx} (not found; build ../qwark or point this at the SPRX)");

        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("VSH slot", ref _slot))
        {
            _slot = Math.Clamp(_slot, 0, 7);
        }

        if (ImGui.Button("Load qwark via webMAN"))
        {
            if (!File.Exists(sprx))
            {
                // "It did not work" without a path is nothing anybody can act on.
                state.AddToast($"No qwark.sprx at {sprx}. Build ../qwark, or point the box above at the SPRX.",
                    ToastKind.Error);
            }
            else
            {
                state.Settings.SprxPath = _sprxPath;
                state.Settings.WebManSlot = _slot;
                state.Settings.Save();

                string ip = _host.Trim();
                string path = sprx;
                int slot = _slot;
                state.Run(
                    () => state.WebMan.LoadAsync(ip, path, slot, new Progress<string>(message => state.Post(() => state.AddToast(message)))),
                    $"qwark.sprx loaded into slot {slot}");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Check if loaded"))
        {
            string ip = _host.Trim();
            state.Run(() => state.WebMan.PluginStatusAsync(ip), status => state.AddToast(
                status.State switch
                {
                    VshPluginState.Loaded => $"{WebManLoader.SprxName} is in VSH slot {status.SlotText}"
                                             + (status.Path.Length > 0 ? $", loaded from {status.Path}" : string.Empty),
                    VshPluginState.NotLoaded => $"No VSH slot holds {WebManLoader.SprxName}",
                    _ => $"webMAN on {ip} did not answer with a plugin page",
                },
                status.State switch
                {
                    VshPluginState.Loaded => ToastKind.Success,
                    VshPluginState.NotLoaded => ToastKind.Info,
                    _ => ToastKind.Error,
                }));
        }

        DrawBootInstall(state, withPathBox: false);

        DrawConsoleSection(state);
    }

    /// <summary>
    /// The boot plugin: the console loads qwark itself at every start and nothing has to send it
    /// afterwards, which is the whole of what standalone mode expects of a console. A change to how
    /// the console starts, and a bad one needs a recovery to undo, so it keeps its own paragraph
    /// behind a checkbox rather than sitting under the buttons that only load for this session.
    /// <paramref name="withPathBox"/> adds the file box the debug section above already draws, for
    /// the standalone user who sees this block and nothing else of the webMAN section.
    /// </summary>
    private static void DrawBootInstall(AppState state, bool withPathBox)
    {
        ImGui.Spacing();

        if (withPathBox)
        {
            ImGui.Separator();
            Ui.Heading("Installation");

            Ui.Hint($"Puts qwark.sprx in {WebManLoader.BootPluginsPath} to load it on console boot. "
                    + "Requires webMAN for installation.");

            ImGui.SetNextItemWidth(360);
            ImGui.InputText("qwark.sprx path", ref _sprxPath, 512);
        }
        else
        {
            Ui.Hint($"Automatically boot qwark.sprx on startup. Writes to {WebManLoader.BootPluginsPath}.");
        }

        string sprx = Ps3Connect.ResolveSprx(_sprxPath);

        // The debug section above has already said where the file is; this block says it only when
        // it is the whole of what a standalone user sees of the SPRX.
        if (withPathBox)
        {
            if (File.Exists(sprx)) Ui.Hint(sprx);
            else Ui.Warning($"{sprx} (not found; the release puts qwark.sprx beside this client)");
        }

        ImGui.Checkbox("I understand a bad boot plugin needs a plugin-disabling recovery", ref _confirmBootInstall);
        ImGui.BeginDisabled(!_confirmBootInstall);
        if (ImGui.Button("Install to boot_plugins.txt"))
        {
            state.Settings.SprxPath = _sprxPath;
            state.Settings.Save();

            string ip = _host.Trim();
            string path = sprx;
            state.Run(() => state.WebMan.InstallToBootAsync(ip, path, new Progress<string>(m => state.Post(() => state.AddToast(m)))),
                changed => state.AddToast(
                    (changed ? "Added to boot_plugins.txt" : "boot_plugins.txt already had it")
                    + ". Restart the console; the module only loads at boot."));
        }

        ImGui.SameLine();
        if (ImGui.Button("Remove from boot_plugins.txt"))
        {
            string ip = _host.Trim();
            state.Run(() => state.WebMan.RemoveFromBootAsync(ip, new Progress<string>(m => state.Post(() => state.AddToast(m)))),
                changed => state.AddToast(changed ? "Removed from boot_plugins.txt" : "boot_plugins.txt did not list it"));
        }

        ImGui.EndDisabled();

        DrawUpdateModuleButton(state, sprx);
    }

    /// <summary>
    /// The standalone update by hand. It is the same call the client makes by itself when a
    /// standalone console reports an old build, so there is nothing here a user has to reach for:
    /// it exists to run the sequence against a console on demand, which is a qwark developer's
    /// business and nobody else's, and it is offered only in the mode that has it.
    /// </summary>
    private static void DrawUpdateModuleButton(AppState state, string sprx)
    {
        if (!Ui.Debug || !state.Settings.StandaloneConnection) return;

        ImGui.BeginDisabled(!state.Connected || !File.Exists(sprx));
        if (ImGui.Button("Update qwark.sprx on the console now"))
        {
            state.Settings.SprxPath = _sprxPath;
            state.Settings.Save();
            state.UpdateConsoleModule(sprx);
        }

        ImGui.EndDisabled();
        Ui.DebugHint($"Writes {sprx} to the boot path through qwark's own file ops and swaps it in. "
                     + "The console reads a boot plugin once, at boot, so it has to be restarted afterwards.");
    }

    /// <summary>
    /// The PS3 Connect button, and the same thing on start. In webMAN mode that is the slot
    /// question first and the port after it; in standalone mode it is the port and nothing else.
    /// Every step says what it is doing, and a failure names the step it stopped at, because "could
    /// not connect" covers four different things on the long way round. What the load needs, the
    /// SPRX and the VSH slot, comes from the settings, which is where the boxes on this panel put it.
    /// </summary>
    public static void ConnectToPs3(AppState state, string host)
    {
        string ip = host.Trim();
        string sprx = Ps3Connect.ResolveSprx(state.Settings.SprxPath);
        int slot = state.Settings.WebManSlot;
        bool webMan = state.Settings.WebManConnection;

        state.Run(async () =>
        {
            var outcome = await Ps3Connect.RunAsync(
                webMan,
                ip,
                connect: () => state.Connected ? Task.CompletedTask : state.Client.ConnectAsync(ip),
                pluginStatus: () => state.WebMan.PluginStatusAsync(ip),
                load: () => File.Exists(sprx)
                    ? state.WebMan.LoadAsync(ip, sprx, slot,
                        new Progress<string>(message => state.Post(() => state.AddToast(message))))
                    : throw new FileNotFoundException(
                        $"no {WebManLoader.SprxName} at {sprx}; build ../qwark, or turn on \"Show debug "
                        + "information\" in Settings to point this at the SPRX", sprx),
                wait: delay => Task.Delay(delay),
                say: (step, message) =>
                {
                    // The reconnect loop's own detour is rate-limited, and a detour taken here
                    // counts: the SPRX has just gone across, so the loop has no reason to send it
                    // again a second later.
                    if (step == Ps3ConnectStep.Ask) state.NoteWebManDetour();
                    state.Post(() => state.AddToast(message));
                });

            state.Post(() =>
            {
                // A failure always says which step it was. A success only does when the sequence
                // went the long way round, because the status line above says the short way did.
                if (!outcome.Connected) state.AddToast(outcome.Message, ToastKind.Error);
                else if (outcome.Step != Ps3ConnectStep.Connect) state.AddToast(outcome.Message, ToastKind.Success);
            });
        });
    }

    /// <summary>
    /// The console utilities: a notification to prove the module is listening, and the three
    /// commands that re-read or rewrite qwark's own state. None of it is part of playing a game,
    /// and each one is a request nobody needs to make by hand, so the whole section only appears
    /// with debug information on.
    /// </summary>
    private static void DrawConsoleSection(AppState state)
    {
        if (!Ui.Debug) return;

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Console");

        ImGui.BeginDisabled(!state.Connected);
        ImGui.SetNextItemWidth(360);
        ImGui.InputText("Notification", ref _notifyText, 255);
        ImGui.SameLine();
        if (ImGui.Button("Send"))
        {
            string text = _notifyText;
            state.Run(() => state.Client.NotifyAsync(text), "Notification sent");
        }

        if (ImGui.Button("Re-read everything")) state.ForceRefresh();
        ImGui.SameLine();
        if (ImGui.Button("Reload console config")) state.Run(() => state.Client.ConfigReloadAsync(), "Config reloaded");
        ImGui.SameLine();
        if (ImGui.Button("Save console config")) state.Run(() => state.Client.ConfigSaveAsync(), "Config saved");
        ImGui.EndDisabled();
    }

    /// <summary>
    /// Which qwark this client talks to. The choice lives in the settings rather than in a command
    /// line, because it decides what the rest of this panel is: a console has an address and an
    /// SPRX to upload, and RPCS3 has a helper process on this PC and nothing to upload at all.
    /// </summary>
    private static bool DrawTarget(AppState state)
    {
        int target = state.Settings.Rpcs3Target ? 1 : 0;
        int chosen = target;

        ImGui.TextUnformatted("Play on");
        ImGui.SameLine();
        ImGui.RadioButton("PS3", ref chosen, 0);
        ImGui.SameLine();
        ImGui.RadioButton("RPCS3", ref chosen, 1);

        if (chosen != target)
        {
            state.Settings.Rpcs3Target = chosen == 1;
            state.Settings.Save();

            // The connection that is up belongs to the other target, so it goes with the choice.
            if (state.Client.WantsConnection)
            {
                state.Run(() => state.Client.DisconnectAsync());
                state.ResetPanels();
            }
        }

        ImGui.Spacing();
        return chosen == 1;
    }

    /// <summary>Starts the helper if nothing is serving the port yet, then connects to this PC.</summary>
    public static void ConnectToRpcs3(AppState state)
    {
        // The port lives on the Settings panel now, so take it from there rather than from a copy
        // this panel made when it was first drawn.
        _pinePort = state.Settings.Rpcs3PinePort;

        state.Settings.LastHost = LocalHost;
        state.Settings.Save();

        if (!state.Rpcs3.Ensure(state.Settings.Rpcs3QwarkPath, _pinePort, out string message))
        {
            state.AddToast(message, ToastKind.Error);
            return;
        }

        state.AddToast(message);
        state.Run(async () =>
        {
            bool up = await state.Rpcs3.WaitForPortAsync(HelperStartupWait).ConfigureAwait(false);
            if (!up && state.Rpcs3.DiedOnStartup)
            {
                // Connecting now would only start the reconnect loop against a port nothing will
                // ever open; the RPCS3 section below carries the exit code and what it means.
                string why = state.Rpcs3.Problem ?? $"{Rpcs3Host.ExeName} exited before it opened its port";
                state.Post(() => state.AddToast(why, ToastKind.Error));
                return;
            }

            await state.Client.ConnectAsync(LocalHost).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// What the helper is doing, and the two things about RPCS3 the user has to know: its IPC
    /// server has to be on, and nothing that patches code will work there.
    /// </summary>
    private static void DrawRpcs3(AppState state)
    {
        var host = state.Rpcs3;

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("RPCS3");

        Ui.Hint($"qwark-rpcs3: {host.Status}"
                + (host.Adopted && !host.IsRunning ? $" (something else is serving port {host.QwarkPort})" : string.Empty));

        // The helper's own words about RPCS3, not this client's: being connected to the helper says
        // nothing about whether the helper has found the emulator, so the line is not coloured
        // green on the strength of our own connection.
        string pine = host.LastPineLine ?? "pine: no word from the helper yet";
        if (pine.Contains("has not answered", StringComparison.Ordinal)
            || pine.Contains("lost", StringComparison.Ordinal))
        {
            // The helper diagnosing a silent or vanished RPCS3, in its own words: most often
            // another program sitting on the IPC port, which RPCS3 serves one at a time.
            Ui.Warning(pine);
        }
        else
        {
            Ui.Hint(pine);
        }

        if (host.Problem is { } problem) Ui.Error(problem);

        int port = state.Settings.Rpcs3PinePort;
        Ui.Hint($"Turn RPCS3's IPC server on (Settings, the \"IPC server\" option) and leave it on port {port}. "
                + "Start the game in RPCS3, then Connect. The port is on this client's Settings panel.");
        Ui.Warning("Code patches do not work on RPCS3: cheats that patch game code, mods and client "
                   + "patches are greyed out. Everything that reads and writes values still works.");

        if (Ui.Debug)
        {
            Ui.DebugHint(host.Find(state.Settings.Rpcs3QwarkPath) ?? $"{Rpcs3Host.ExeName} not found");
            foreach (var line in host.Lines) Ui.DebugHint(line);
        }
    }

    private static void DrawFirewallButton(AppState state)
    {
        if (!FirewallHelper.IsSupported) return;   // Windows-only; the rule is a no-op elsewhere

        string executable = FirewallHelper.TargetExecutable;

        if (ImGui.Button("Allow inbound UDP through Windows Firewall"))
        {
            var (ok, message) = FirewallHelper.RequestRule(executable);
            state.AddToast(message, ok ? ToastKind.Info : ToastKind.Error);

            // The same book-keeping the first-run modal does: the rule is for this executable, so
            // a later start can tell whether it is still there rather than assuming it.
            state.Settings.RecordFirewall(ok, executable);
        }
        ImGui.SameLine();
        Ui.Hint("Adds a firewall rule for this app (asks for admin). Or run \"Allow through Firewall.cmd\".");
        Ui.DebugHint($"Rule would name {executable}");
    }

    /// <summary>
    /// The console is running an older qwark.sprx than the one this client shipped with, so its
    /// feature tables are the previous build's. Not a debug detail: it is the difference between
    /// a missing cheat being absent and it being broken, and re-uploading the SPRX fixes it.
    /// <para>
    /// What fixes it depends on the mode. webMAN mode sends and loads the module itself, so the
    /// button below is the answer. Standalone mode has already written the new module to the boot
    /// path by the time the user reads this, and what is left is the restart, which this client
    /// cannot do: that notice replaces the warning and stays until the console comes back on the
    /// new build.
    /// </para>
    /// </summary>
    private static void DrawStaleBuild(AppState state)
    {
        if (state.QwarkUpdateStaged > 0)
        {
            ImGui.Spacing();
            Ui.Warning(state.QwarkUpdateNotice);
            return;
        }

        if (!state.QwarkStale) return;

        // The RPCS3 helper is a program on this PC that the client's own update replaces; the mode
        // setting is about consoles and says nothing about it.
        string sprx = Ps3Connect.ResolveSprx(state.Settings.SprxPath);
        string fix = !state.Settings.StandaloneConnection || state.Settings.Rpcs3Target
            ? "Re-upload qwark.sprx with the button below and the console will reload it."
            : File.Exists(sprx)
                ? "The client is putting the new qwark.sprx on the console by itself; it will say here when "
                  + "the console has to be restarted to load it."
                : $"There is no qwark.sprx beside this client to send ({sprx}).";

        ImGui.Spacing();
        Ui.Warning($"The console is running qwark build {state.Hello!.QwarkVersion}; this client shipped with build "
                   + $"{QwarkClient.ExpectedQwarkBuild}. " + fix);
    }

    /// <summary>
    /// What HELLO said about the module. The protocol version is the one number that has to
    /// match: a client and a module that disagree about it disagree about every payload below.
    /// </summary>
    private static void DrawVersions(AppState state)
    {
        var hello = state.Hello;
        if (hello is null)
        {
            ImGui.Spacing();
            Ui.DebugHint($"Client protocol version {QwarkClient.ClientProtocolVersion}; no handshake yet.");
            return;
        }

        ImGui.Spacing();
        Ui.DebugHint($"qwark build {hello.QwarkVersion} | protocol {hello.ProtocolVersion} (HELLO)");

        if (hello.ProtocolVersion != QwarkClient.ClientProtocolVersion)
        {
            Ui.Error($"Protocol mismatch: the console speaks version {hello.ProtocolVersion}, this client speaks "
                     + $"{QwarkClient.ClientProtocolVersion}. Payload layouts differ between versions; update "
                     + "whichever side is older.");
        }
    }
}
