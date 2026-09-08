using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

public static class ConnectionPanel
{
    private static string _host = string.Empty;
    private static string _sprxPath = string.Empty;
    private static string _notifyText = "Hello from RaCMAN Reloaded";
    private static int _slot = 5;
    private static bool _confirmBootInstall;
    private static bool _initialised;

    public static void Draw(AppState state)
    {
        if (!_initialised)
        {
            _host = state.Settings.LastHost;
            _sprxPath = state.Settings.SprxPath;
            _slot = state.Settings.WebManSlot;
            _initialised = true;
        }

        Ui.Heading("Connection");

        ImGui.SetNextItemWidth(260);
        ImGui.InputText("PS3 address", ref _host, 64);
        ImGui.SameLine();

        bool connecting = state.Client.WantsConnection && !state.Connected;
        ImGui.BeginDisabled(state.Connected || connecting);
        if (ImGui.Button("Connect"))
        {
            state.Settings.LastHost = _host;
            state.Settings.Save();
            string host = _host.Trim();
            state.Run(() => state.Client.ConnectAsync(host));
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

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Load qwark through webMAN");
        Ui.Hint($"Uploads qwark.sprx to {WebManLoader.RemotePath} over FTP, then asks webMAN to load it into a VSH slot.");

        ImGui.SetNextItemWidth(360);
        ImGui.InputText("qwark.sprx path", ref _sprxPath, 512);

        // The default is qwark.sprx beside the executable, which is where the release layout puts it.
        // The resolved path is absolute and long, so both spellings of this line wrap.
        string sprx = ResolveSprx(_sprxPath);
        bool sprxExists = File.Exists(sprx);
        if (sprxExists) Ui.Hint(sprx);
        else Ui.Warning($"{sprx} (not found; build ../qwark or point this at the SPRX)");

        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("VSH slot", ref _slot))
        {
            _slot = Math.Clamp(_slot, 0, 7);
        }

        if (ImGui.Button("Load qwark via webMAN"))
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

        ImGui.SameLine();
        if (ImGui.Button("Check if loaded"))
        {
            string ip = _host.Trim();
            state.Run(() => state.WebMan.IsLoadedAsync(ip),
                loaded => state.AddToast(loaded ? "webMAN reports qwark.sprx loaded" : "webMAN does not list qwark.sprx",
                    loaded ? ToastKind.Success : ToastKind.Info));
        }

        ImGui.Spacing();
        Ui.Hint($"A boot install puts the SPRX in {WebManLoader.BootPath} and lists it in {WebManLoader.BootPluginsPath}.");
        ImGui.Checkbox("I understand a bad boot plugin needs a plugin-disabling recovery", ref _confirmBootInstall);
        ImGui.BeginDisabled(!_confirmBootInstall);
        if (ImGui.Button("Install to boot_plugins.txt"))
        {
            string ip = _host.Trim();
            string path = sprx;
            state.Run(() => state.WebMan.InstallToBootAsync(ip, path, new Progress<string>(m => state.Post(() => state.AddToast(m)))),
                changed => state.AddToast(changed ? "Added to boot_plugins.txt" : "boot_plugins.txt already had it"));
        }

        ImGui.SameLine();
        if (ImGui.Button("Remove from boot_plugins.txt"))
        {
            string ip = _host.Trim();
            state.Run(() => state.WebMan.RemoveFromBootAsync(ip, new Progress<string>(m => state.Post(() => state.AddToast(m)))),
                changed => state.AddToast(changed ? "Removed from boot_plugins.txt" : "boot_plugins.txt did not list it"));
        }

        ImGui.EndDisabled();

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

    private static string ResolveSprx(string path)
    {
        var trimmed = (path ?? string.Empty).Trim().Trim('"');
        if (trimmed.Length == 0) trimmed = WebManLoader.SprxName;
        return Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(AppContext.BaseDirectory, trimmed);
    }

    private static void DrawFirewallButton(AppState state)
    {
        if (!FirewallHelper.IsSupported) return;   // Windows-only; the rule is a no-op elsewhere

        if (ImGui.Button("Allow inbound UDP through Windows Firewall"))
        {
            var (ok, message) = FirewallHelper.RequestRule();
            state.AddToast(message, ok ? ToastKind.Info : ToastKind.Error);
        }
        ImGui.SameLine();
        Ui.Hint("Adds a firewall rule for this app (asks for admin). Or run \"Allow through Firewall.cmd\".");
    }

    /// <summary>
    /// The console is running an older qwark.sprx than the one this client shipped with, so its
    /// feature tables are the previous build's. Not a debug detail: it is the difference between
    /// a missing cheat being absent and it being broken, and re-uploading the SPRX fixes it.
    /// </summary>
    private static void DrawStaleBuild(AppState state)
    {
        if (!state.QwarkStale) return;

        ImGui.Spacing();
        Ui.Warning($"The console is running qwark build {state.Hello!.QwarkVersion}; this client shipped with build "
                   + $"{QwarkClient.ExpectedQwarkBuild}. Re-upload qwark.sprx with the button below and the "
                   + "console will reload it.");
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
