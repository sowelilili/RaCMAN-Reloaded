using System.Numerics;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using RaCMAN.App.Panels;

namespace RaCMAN.App;

public sealed class AppWindow : GameWindow
{
    private static readonly string[] PanelNames =
    {
        "Connection",
        "Game",
        "Positions",
        "Unlocks",
        "Level flags",
        "Memory",
        "Mods",
        "Save files",
        "Combos",
        "Autosplitter",
        "Input display",
        "Settings",
    };

    private readonly AppState _state;
    private readonly double _exitAfterSeconds;

    private ImGuiController? _controller;
    private PadWindow? _pad;
    private int _panel;
    private double _elapsed;

    public AppWindow(AppState state, double exitAfterSeconds = 0, int startPanel = 0)
        : base(
            new GameWindowSettings { UpdateFrequency = 60 },
            new NativeWindowSettings
            {
                ClientSize = new OpenTK.Mathematics.Vector2i(940, 580),

                // The version is in the title because it is the first thing anyone is asked for
                // when they report something, and the Settings panel is two clicks away.
                Title = $"RaCMAN Reloaded {AppVersion.Current}",
                APIVersion = new Version(3, 3),
                Profile = ContextProfile.Core,
                Flags = ContextFlags.ForwardCompatible,
                Vsync = VSyncMode.On,
            })
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

    protected override void OnLoad()
    {
        base.OnLoad();
        _controller = new ImGuiController(this);
        ApplyTheme();

        // First-run only, and only on a published Windows build with the helper beside it.
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
                // If the current panel is one the running game doesn't support, fall back to Game;
                // if it is one this console cannot drive at all, to Connection, which is the panel
                // that says which target is connected.
                if (!PanelVisible(_panel)) _panel = 1;
                if (DisabledReason(_panel) is not null) _panel = 0;

                for (int i = 0; i < PanelNames.Length; i++)
                {
                    if (!PanelVisible(i)) continue;

                    bool isGame = i == 1;
                    bool selected = _panel == i && (!isGame || GamePanel.SubPage is null);

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
                        if (isGame) GamePanel.SubPage = null;
                    }

                    if (!isGame) continue;

                    // The Game page's sub-pages: the sections the layout marks as "side", indented
                    // under Game, and only those the running game has something for.
                    foreach (var section in GamePanel.SideSectionsWithContent(_state))
                    {
                        bool onSub = _panel == 1 && GamePanel.SubPage == section;
                        ImGui.PushID("game-sub");
                        ImGui.Indent(18);
                        if (ImGui.Selectable(section, onSub, ImGuiSelectableFlags.None, new Vector2(0, 24)))
                        {
                            _panel = 1;
                            GamePanel.SubPage = section;
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

        // The modals are drawn outside the root window, so they survive a panel switch.
        PreviousSessionModal.Draw(_state);
        FirewallModal.Draw(_state);
        LiveSplitModal.Draw(_state);
        DrawToasts();
    }

    /// <summary>Hide panels the running game has no data for: Unlocks (index 3) and Level flags (index 4).</summary>
    private bool PanelVisible(int panel) => panel switch
    {
        3 => !_state.UnlocksUnsupported,
        4 => !_state.LevelFlagsUnsupported,
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

        // Visible from every panel: the Connection panel carries the explanation and the fix.
        if (_state.QwarkStale)
        {
            ImGui.SameLine();
            ImGui.TextColored(Ui.Yellow, "| qwark.sprx is out of date");
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
            case 0: ConnectionPanel.Draw(_state); break;
            case 1: GamePanel.Draw(_state); break;
            case 2: PositionsPanel.Draw(_state); break;
            case 3: UnlocksPanel.Draw(_state); break;
            case 4: LevelFlagsPanel.Draw(_state); break;
            case 5: MemoryPanel.Draw(_state); break;
            case 6: ModsPanel.Draw(_state); break;
            case 7: SaveFilesPanel.Draw(_state); break;
            case 8: CombosPanel.Draw(_state); break;
            case 9: AutosplitterPanel.Draw(_state); break;
            case 10: InputDisplayPanel.Draw(_state, controller); break;
            case 11: SettingsPanel.Draw(_state); break;
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

    protected override void OnUnload()
    {
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
