using System.Numerics;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using RaCMAN.App.Panels;
using StbImageSharp;

namespace RaCMAN.App;

public sealed class AppWindow : GameWindow
{
    /// <summary>The nav's own list, in its own order; see <see cref="PanelNav"/>.</summary>
    private static string[] PanelNames => PanelNav.Names;

    private readonly AppState _state;
    private readonly double _exitAfterSeconds;

    private ImGuiController? _controller;
    private PadWindow? _pad;
    private int _panel;
    private double _elapsed;

    public AppWindow(AppState state, double exitAfterSeconds = 0, int startPanel = 0)
        : base(new GameWindowSettings { UpdateFrequency = 60 }, WindowSettings(state.Settings))
    {
        _state = state;
        _exitAfterSeconds = exitAfterSeconds;
        _panel = Math.Clamp(startPanel, 0, PanelNames.Length - 1);

        // Before OnLoad, so the controller builds its style from the saved theme and the very
        // first frame is already the right one.
        Ui.Light = state.Settings.LightTheme;
        Ui.Debug = state.Settings.DebugInfo;
    }

    /// <summary>Set when a frame threw, so the caller can report a non-zero exit code.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>
    /// The panel the nav is on, by name. A headless run prints it on its way out, which is the only
    /// way to see from outside the window where <c>--panel</c> and <c>--game-section</c> landed.
    /// </summary>
    public string PanelName => PanelNames[Math.Clamp(_panel, 0, PanelNames.Length - 1)];

    /// <summary>
    /// The window this run opens: the default size, or the size and corner the last run was left
    /// at, brought back onto a monitor this desktop actually has. <see cref="WindowGeometry"/> has
    /// the rules; this only asks GLFW what the desktop looks like.
    /// </summary>
    private static NativeWindowSettings WindowSettings(Settings settings)
    {
        var (width, height, x, y) = WindowGeometry.Restore(
            settings.WindowWidth, settings.WindowHeight, settings.WindowX, settings.WindowY, MonitorAreas());

        return new NativeWindowSettings
        {
            ClientSize = new OpenTK.Mathematics.Vector2i(width, height),

            // Null is "wherever the platform puts it", which is what a first run and a saved corner
            // on a monitor that has gone away both get.
            Location = x is { } left && y is { } top ? new OpenTK.Mathematics.Vector2i(left, top) : null,

            // The version is in the title because it is the first thing anyone is asked for
            // when they report something, and the Settings panel is two clicks away.
            Title = $"RaCMAN Reloaded {AppVersion.Current}",
            Icon = LoadIcon(),
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.ForwardCompatible,
            Vsync = VSyncMode.On,
        };
    }

    /// <summary>
    /// The window icon, decoded out of the assembly. Windows takes the taskbar icon from the
    /// executable itself, so this is what the title bar shows there and what every other platform
    /// has to go on. An icon is decoration: if anything about it fails, the window opens without
    /// one rather than not at all.
    /// </summary>
    private static WindowIcon? LoadIcon()
    {
        try
        {
            using var stream = typeof(AppWindow).Assembly.GetManifestResourceStream("RaCMAN.App.icon.png");
            if (stream is null) return null;

            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            if (image.Width <= 0 || image.Height <= 0) return null;

            return new WindowIcon(new OpenTK.Windowing.Common.Input.Image(image.Width, image.Height, image.Data));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The desktop's monitors, primary first, as their work areas: the part of each screen a window
    /// can actually occupy, so a restored window does not open under the taskbar. An empty list is
    /// a fine answer, and asks for the default size wherever the platform cares to put it.
    /// </summary>
    private static IReadOnlyList<WindowGeometry.Area> MonitorAreas()
    {
        var areas = new List<WindowGeometry.Area>();
        try
        {
            // The base constructor would be the first thing to bring GLFW up, and this runs in
            // front of it: nothing can be asked about monitors until it is initialised.
            GLFWProvider.EnsureInitialized();

            Add(areas, Monitors.GetPrimaryMonitor());
            foreach (var monitor in Monitors.GetMonitors()) Add(areas, monitor);
        }
        catch (Exception ex) when (ex is GLFWException or PlatformNotSupportedException
                                       or InvalidOperationException)
        {
            // Not knowing where the screens are is not a reason to fail to open a window.
        }

        return areas;
    }

    /// <summary>
    /// One monitor's work area, or its whole area where the platform reports no work area at all.
    /// The primary is asked for first and turns up in the full list as well, so a rectangle that is
    /// already there is skipped.
    /// </summary>
    private static void Add(List<WindowGeometry.Area> areas, MonitorInfo monitor)
    {
        var box = monitor.WorkArea;
        if (box.Size.X <= 0 || box.Size.Y <= 0) box = monitor.ClientArea;
        if (box.Size.X <= 0 || box.Size.Y <= 0) return;

        var area = new WindowGeometry.Area(box.Min.X, box.Min.Y, box.Size.X, box.Size.Y);
        if (!areas.Contains(area)) areas.Add(area);
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        _controller = new ImGuiController(this);
        ApplyTheme();

        // Only on a published Windows build with the helper beside it, and only when the firewall
        // really has no rule for the executable that is running: an update moves that executable.
        Panels.FirewallModal.MaybeOffer(_state);
    }

    /// <summary>
    /// Pushes the saved theme into ImGui, the status colours and the GL clear colour. Called once
    /// on load and again whenever the Settings panel flips a switch.
    /// </summary>
    private void ApplyTheme()
    {
        bool light = _state.Settings.LightTheme;
        Ui.Light = light;
        Ui.Debug = _state.Settings.DebugInfo;

        // ApplyStyle works on whatever ImGui context is current, and the pad window's was the last
        // one set if it drew a frame.
        _controller?.MakeCurrent();
        ImGuiController.ApplyStyle(light);
        if (light) GL.ClearColor(0.94f, 0.94f, 0.95f, 1f);
        else GL.ClearColor(0.07f, 0.07f, 0.09f, 1f);
        _state.ThemeDirty = false;
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        _elapsed += args.Time;
        if (_exitAfterSeconds > 0 && _elapsed >= _exitAfterSeconds) Close();

        var controller = _controller;
        if (controller is null) return;

        try
        {
            if (_state.ThemeDirty) ApplyTheme();

            _state.Tick((float)args.Time);
            CombosPanel.Update(_state);

            // Here rather than in Tick: a test builds an AppState of its own, and nothing a test
            // builds may open a socket.
            _state.SyncObsPad();

            controller.Update((float)args.Time);
            DrawUi(controller);

            GL.Clear(ClearBufferMask.ColorBufferBit);
            controller.Render();
            SwapBuffers();

            SyncPadWindow((float)args.Time);
        }
        catch (Exception ex)
        {
            Failure = ex;
            Console.Error.WriteLine(ex);
            Close();
        }
    }

    /// <summary>
    /// Opens, draws and closes the pad's own OS window, after the main window's frame and on the
    /// same thread. The setting is the only switch: the panel ticks it, and the window's own close
    /// button unticks it. Whatever happens, the main window's GL context is current on the way out.
    /// </summary>
    private void SyncPadWindow(float deltaSeconds)
    {
        var settings = _state.Settings;
        bool wanted = settings.InputMode == InputDisplayMode.Window;
        if (!wanted && _pad is null) return;

        try
        {
            if (!wanted)
            {
                ClosePadWindow();
                return;
            }

            _pad ??= PadWindow.Create(settings, this);
            _pad.Render(_state, deltaSeconds);

            if (!_pad.Closed) return;

            settings.InputMode = InputDisplayMode.Panel;
            settings.Save();
            ClosePadWindow();
        }
        catch (Exception ex) when (ex is GLFWException or PlatformNotSupportedException
                                       or InvalidOperationException)
        {
            // A second window is a nicety: the client keeps working with the pad back inside the
            // panel, and the message says what went wrong rather than the window silently not
            // appearing.
            ClosePadWindow();
            settings.InputMode = InputDisplayMode.Panel;
            settings.Save();
            _state.AddToast($"Pad window: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            MakeCurrent();
        }
    }

    private void ClosePadWindow()
    {
        _pad?.Dispose();
        _pad = null;
    }

    private void DrawUi(ImGuiController controller)
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                                       | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse
                                       | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus;

        if (ImGui.Begin("##root", flags))
        {
            DrawUpdateBanner();
            DrawHeader();
            ImGui.Separator();

            if (ImGui.BeginChild("##nav", new Vector2(190, -1), ImGuiChildFlags.Borders))
            {
                // A section asked for on the command line opens on whichever panel draws it, as
                // soon as the descriptors say the running game has that section at all.
                RouteRequestedSection();

                // If the current panel is one the running game doesn't support, fall back to Game;
                // if it is one this console cannot drive at all, to Connection, which is the panel
                // that says which target is connected.
                if (!PanelVisible(_panel)) _panel = PanelNav.Game;
                if (DisabledReason(_panel) is not null) _panel = PanelNav.Connection;

                for (int i = 0; i < PanelNames.Length; i++)
                {
                    if (!PanelVisible(i)) continue;

                    // A thin line between the nav's groups, and none above the first of them.
                    if (PanelNav.StartsGroup(i)) ImGui.Separator();

                    // Game is the one entry with sub-pages indented under it, and it is selected
                    // only while its own page, rather than one of those, is the one showing.
                    bool isGame = i == PanelNav.Game;
                    bool selected = _panel == i && (!isGame || SubPageNav.Open is null);

                    string? disabled = DisabledReason(i);
                    ImGui.BeginDisabled(disabled is not null);
                    bool clicked = ImGui.Selectable(PanelNames[i], selected, ImGuiSelectableFlags.None, new Vector2(0, 26));
                    ImGui.EndDisabled();

                    if (disabled is not null)
                    {
                        // Greyed out and still listed, with the reason on hover: the panel is not
                        // gone, it is the console underneath that cannot do what it asks for.
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(disabled);
                        continue;
                    }

                    if (clicked)
                    {
                        _panel = i;
                        if (isGame) SubPageNav.Open = null;
                    }

                    if (!isGame) continue;

                    // The Game page's sub-pages: the sections the layout makes pages of their own,
                    // indented under it, and only those the running game has something for.
                    foreach (var section in GamePanel.SubPagesWithContent(_state))
                    {
                        bool onSub = _panel == PanelNav.Game && SubPageNav.Open == section;
                        ImGui.PushID("game-sub");
                        ImGui.Indent(18);
                        if (ImGui.Selectable(section, onSub, ImGuiSelectableFlags.None, new Vector2(0, 24)))
                        {
                            _panel = PanelNav.Game;
                            SubPageNav.Open = section;
                        }
                        ImGui.Unindent(18);
                        ImGui.PopID();
                    }
                }

                if (Ui.Debug)
                {
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.TextColored(Ui.Grey, $"{ImGui.GetIO().Framerate:0} fps");
                    if (_state.InFlight > 0) ImGui.TextColored(Ui.Yellow, $"{_state.InFlight} in flight");
                }
            }

            ImGui.EndChild();
            ImGui.SameLine();

            if (ImGui.BeginChild("##content", new Vector2(-1, -1), ImGuiChildFlags.Borders))
            {
                DrawPanel(controller);
            }

            ImGui.EndChild();
        }

        ImGui.End();

        // The moby inspectors and the modals are drawn outside the root window, so they survive a
        // panel switch. The inspectors go first, which leaves a modal on top of them.
        MobyInspector.Draw(_state);
        PreviousSessionModal.Draw(_state);
        FirewallModal.Draw(_state);
        LiveSplitModal.Draw(_state);
        DrawToasts();
    }

    /// <summary>
    /// Opens the panel that draws the section <c>--game-section</c> named, so a headless run lands
    /// on it wherever the layout put it: the Game page for a sub-page, the Unlocks panel for one of
    /// its tabs. Nothing happens while the running game has no such section, or while that panel is
    /// hidden or greyed out; the panel itself takes the request up (and clears it) the first time
    /// it draws.
    /// </summary>
    private void RouteRequestedSection()
    {
        if (SubPageNav.Requested is not { } wanted) return;
        if (GamePanel.PanelForSection(_state, wanted) is not { } panel) return;
        if (PanelVisible(panel) && DisabledReason(panel) is null) _panel = panel;
    }

    /// <summary>Hide panels the running game has no data for: Unlocks and Level flags.</summary>
    private bool PanelVisible(int panel) => panel switch
    {
        PanelNav.Unlocks => !_state.UnlocksUnsupported,
        PanelNav.LevelFlags => !_state.LevelFlagsUnsupported,
        _ => true,
    };

    /// <summary>
    /// Why the nav greys a panel out, or null when it is usable. The rule is
    /// <see cref="PanelNav.DisabledReason"/>; the state it reads is the session's.
    /// </summary>
    private string? DisabledReason(int panel) => PanelNav.DisabledReason(panel, _state.CodePatchesUnsupported);

    /// <summary>
    /// The one line above everything else, and only while there is something to do about it: a new
    /// version to fetch, one coming down, or one waiting for a restart. Later silences it until the
    /// client is started again, and the Settings panel keeps the buttons for anyone who changes
    /// their mind.
    /// </summary>
    private void DrawUpdateBanner()
    {
        var updates = _state.Updates;
        if (!updates.HasBanner) return;

        switch (updates.Stage)
        {
            case UpdateStage.Available:
                Ui.Text(Ui.Yellow, $"RaCMAN Reloaded {updates.AvailableVersion} is available");
                ImGui.SameLine();
                if (ImGui.SmallButton("Download")) updates.Download();
                break;

            case UpdateStage.Downloading:
                Ui.Text(Ui.Yellow, $"Downloading RaCMAN Reloaded {updates.AvailableVersion}...");
                ImGui.SameLine();
                ImGui.ProgressBar(Math.Clamp(updates.Percent / 100f, 0f, 1f), new Vector2(160, 0));
                break;

            case UpdateStage.Ready:
                Ui.Text(Ui.Green, $"RaCMAN Reloaded {updates.AvailableVersion} is ready");
                ImGui.SameLine();
                if (ImGui.SmallButton("Restart to update")) updates.RestartAndApply();
                break;
        }

        // Not offered mid-download: there is nothing to put off while the bytes are coming.
        if (updates.Stage != UpdateStage.Downloading)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Later")) updates.Dismiss();
        }

        ImGui.Separator();
    }

    private void DrawHeader()
    {
        var session = _state.Session;
        var colour = _state.Connected ? Ui.Green : _state.Client.WantsConnection ? Ui.Yellow : Ui.Grey;

        ImGui.TextColored(colour, _state.Connected ? "connected" : _state.Client.WantsConnection ? "reconnecting" : "offline");
        ImGui.SameLine();
        ImGui.TextUnformatted("|");
        ImGui.SameLine();
        ImGui.TextUnformatted(_state.StatusLine());

        // Visible from every panel: the Connection panel carries the explanation and the fix. Once
        // the new module is on the console the line says what is left to do rather than what is
        // wrong, and it stays there — over the restart's disconnect included — until the console
        // comes back reporting the build that was put on it.
        if (_state.QwarkUpdateStaged > 0)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("|");
            ImGui.SameLine();
            Ui.Text(Ui.Yellow, $"{_state.QwarkUpdateNotice}");
        }
        else if (_state.QwarkStale)
        {   
            ImGui.SameLine();
            ImGui.TextUnformatted("|");
            ImGui.SameLine();
            ImGui.TextColored(Ui.Yellow, "qwark.sprx is out of date");
        }

        // The same, for the fallback that is easy to run on for months without noticing: everything
        // works, so nothing says so except the Connection panel nobody has open.
        if (_state.Connected && _state.Client.TelemetryViaTcp)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("|");
            ImGui.SameLine();
            ImGui.TextColored(Ui.Yellow, "TCP fallback");
            Ui.Tooltip("The console's UDP telemetry is not reaching this PC, likely due to a "
                       + "firewall issue, so live state is being polled over TCP instead. "
                       + "Everything works; readouts and the input display just update more slowly. "
                       + "To update your firewall, go to the Connection panel.");
        }

        if (_state.Connected && session.PreviousPending)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Previous session...")) _state.PreviousModalRequested = true;
        }
    }

    private void DrawPanel(ImGuiController controller)
    {
        switch (_panel)
        {
            case PanelNav.Connection: ConnectionPanel.Draw(_state); break;
            case PanelNav.Game: GamePanel.Draw(_state); break;
            case PanelNav.Unlocks: UnlocksPanel.Draw(_state); break;
            case PanelNav.Mods: ModsPanel.Draw(_state); break;
            case PanelNav.SaveFiles: SaveFilesPanel.Draw(_state); break;
            case PanelNav.Positions: PositionsPanel.Draw(_state); break;
            case PanelNav.Autosplitter: AutosplitterPanel.Draw(_state); break;
            case PanelNav.InputDisplay: InputDisplayPanel.Draw(_state, controller); break;
            case PanelNav.LevelFlags: LevelFlagsPanel.Draw(_state); break;
            case PanelNav.Memory: MemoryPanel.Draw(_state); break;
            case PanelNav.Combos: CombosPanel.Draw(_state); break;
            case PanelNav.Settings: SettingsPanel.Draw(_state); break;
        }
    }

    private void DrawToasts()
    {
        var toasts = _state.Toasts;
        if (toasts.Count == 0) return;

        var viewport = ImGui.GetMainViewport();
        var position = viewport.WorkPos + new Vector2(viewport.WorkSize.X - 16, viewport.WorkSize.Y - 16);

        ImGui.SetNextWindowPos(position, ImGuiCond.Always, new Vector2(1f, 1f));
        ImGui.SetNextWindowBgAlpha(0.9f);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                                       | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize
                                       | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

        if (ImGui.Begin("##toasts", flags))
        {
            for (int i = 0; i < toasts.Count; i++)
            {
                var toast = toasts[i];
                var colour = toast.Kind switch
                {
                    ToastKind.Error => Ui.Red,
                    ToastKind.Success => Ui.Green,
                    _ => Ui.Neutral,
                };

                // A toast carries file names and run categories, and ImGui's Text is printf: an
                // "Any% (co-op)" would arrive on screen as "Any(co-op)".
                Ui.Text(colour, toast.Text);
            }
        }

        ImGui.End();
    }

    /// <summary>
    /// The size and corner the window is being left at, so the next run opens where this one ended.
    /// A minimised window is not a size anybody chose and neither is a collapsed one, so those are
    /// left as the file already has them.
    /// </summary>
    private void RememberGeometry()
    {
        if (WindowState == WindowState.Minimized) return;

        var client = ClientSize;
        if (client.X <= 0 || client.Y <= 0) return;

        var settings = _state.Settings;
        settings.WindowWidth = client.X;
        settings.WindowHeight = client.Y;
        settings.WindowX = Location.X;
        settings.WindowY = Location.Y;
    }

    protected override void OnUnload()
    {
        // While the window is still there to ask. Saving here rather than only leaving it to the
        // caller means a run that ended on a thrown frame still remembers where it was.
        RememberGeometry();
        _state.Settings.Save();

        // The pad window owns GL objects in its own context, so it goes first and puts this
        // window's context back before the main controller deletes anything.
        ClosePadWindow();
        MakeCurrent();

        InputDisplayPanel.Dispose();
        _controller?.Dispose();
        _controller = null;
        base.OnUnload();
    }
}
