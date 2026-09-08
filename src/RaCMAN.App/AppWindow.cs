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
        "Input display",
    };

    private readonly AppState _state;
    private readonly double _exitAfterSeconds;

    private ImGuiController? _controller;
    private int _panel;
    private double _elapsed;

    public AppWindow(AppState state, double exitAfterSeconds = 0, int startPanel = 0)
        : base(
            new GameWindowSettings { UpdateFrequency = 60 },
            new NativeWindowSettings
            {
                ClientSize = new OpenTK.Mathematics.Vector2i(1180, 760),
                Title = "RaCMAN Reloaded",
                APIVersion = new Version(3, 3),
                Profile = ContextProfile.Core,
                Flags = ContextFlags.ForwardCompatible,
                Vsync = VSyncMode.On,
            })
    {
        _state = state;
        _exitAfterSeconds = exitAfterSeconds;
        _panel = Math.Clamp(startPanel, 0, PanelNames.Length - 1);
    }

    /// <summary>Set when a frame threw, so the caller can report a non-zero exit code.</summary>
    public Exception? Failure { get; private set; }

    protected override void OnLoad()
    {
        base.OnLoad();
        _controller = new ImGuiController(this);
        GL.ClearColor(0.07f, 0.07f, 0.09f, 1f);

        // First-run only, and only on a published Windows build with the helper beside it.
        Panels.FirewallModal.MaybeOffer(_state);
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
            _state.Tick((float)args.Time);
            CombosPanel.Update(_state);

            controller.Update((float)args.Time);
            DrawUi(controller);

            GL.Clear(ClearBufferMask.ColorBufferBit);
            controller.Render();
            SwapBuffers();
        }
        catch (Exception ex)
        {
            Failure = ex;
            Console.Error.WriteLine(ex);
            Close();
        }
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
            DrawHeader();
            ImGui.Separator();

            if (ImGui.BeginChild("##nav", new Vector2(190, -1), ImGuiChildFlags.Borders))
            {
                // If the current panel is one the running game doesn't support, fall back to Game.
                if (!PanelVisible(_panel)) _panel = 1;

                for (int i = 0; i < PanelNames.Length; i++)
                {
                    if (!PanelVisible(i)) continue;

                    bool isGame = i == 1;
                    bool selected = _panel == i && (!isGame || GamePanel.SubPage is null);
                    if (ImGui.Selectable(PanelNames[i], selected, ImGuiSelectableFlags.None, new Vector2(0, 26)))
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

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextColored(Ui.Grey, $"{ImGui.GetIO().Framerate:0} fps");
                if (_state.InFlight > 0) ImGui.TextColored(Ui.Yellow, $"{_state.InFlight} in flight");
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

        // The floating pad is drawn outside the root window so it survives a panel switch.
        InputDisplayPanel.DrawFloating(_state, controller);
        PreviousSessionModal.Draw(_state);
        FirewallModal.Draw(_state);
        DrawToasts();
    }

    /// <summary>Hide panels the running game has no data for: Unlocks (index 3) and Level flags (index 4).</summary>
    private bool PanelVisible(int panel) => panel switch
    {
        3 => !_state.UnlocksUnsupported,
        4 => !_state.LevelFlagsUnsupported,
        _ => true,
    };

    private void DrawHeader()
    {
        var session = _state.Session;
        var colour = _state.Connected ? Ui.Green : _state.Client.WantsConnection ? Ui.Yellow : Ui.Grey;

        ImGui.TextColored(colour, _state.Connected ? "connected" : _state.Client.WantsConnection ? "reconnecting" : "offline");
        ImGui.SameLine();
        ImGui.TextUnformatted("|");
        ImGui.SameLine();
        ImGui.TextUnformatted(_state.StatusLine());

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
            case 9: InputDisplayPanel.Draw(_state, controller); break;
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
                    _ => new Vector4(0.85f, 0.85f, 0.9f, 1f),
                };

                ImGui.TextColored(colour, toast.Text);
            }
        }

        ImGui.End();
    }

    protected override void OnUnload()
    {
        InputDisplayPanel.Dispose();
        _controller?.Dispose();
        _controller = null;
        base.OnUnload();
    }
}
