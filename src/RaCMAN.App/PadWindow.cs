using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using RaCMAN.App.Panels;

namespace RaCMAN.App;

/// <summary>
/// The input display as a real OS window, so a capture tool can take it as a source of its own,
/// it can sit over the game feed, and it survives the main window being minimised.
///
/// There is still only one render loop: <see cref="AppWindow"/> drives this one from its own
/// frame, on the main thread, which is the only arrangement macOS accepts. The window has its own
/// GL context and its own <see cref="ImGuiController"/> - nothing is shared - so every call in
/// here runs between <see cref="NativeWindow.MakeCurrent"/> and the caller putting the main
/// context back.
/// </summary>
public sealed class PadWindow : IDisposable
{
    public const string WindowTitle = "RaCMAN input display";

    /// <summary>Dark grey, a shade off black, so a black skin still has an edge against it.</summary>
    private static readonly System.Numerics.Vector3 Background = new(0.08f, 0.08f, 0.09f);

    private readonly NativeWindow _window;
    private readonly ImGuiController _controller;
    private readonly Settings _settings;
    private bool _onTop;
    private bool _disposed;

    private PadWindow(NativeWindow window, ImGuiController controller, Settings settings)
    {
        _window = window;
        _controller = controller;
        _settings = settings;
    }

    /// <summary>True once the user has closed the window with its own close button.</summary>
    public bool Closed { get; private set; }

    /// <summary>
    /// Opens the window next to the main one. The caller owns making the main context current
    /// again: creating a GLFW window makes its context current as a side effect.
    /// </summary>
    public static PadWindow Create(Settings settings, NativeWindow main)
    {
        var (width, height) = StartSize(settings);
        var location = StartLocation(settings, main, width, height);

        var window = new NativeWindow(new NativeWindowSettings
        {
            ClientSize = new Vector2i(width, height),
            Location = location,
            Title = WindowTitle,
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.ForwardCompatible,

            // The main window is the one that paces the loop. A second vsynced swap chain would
            // wait for its own vblank as well and halve the frame rate of both windows.
            Vsync = VSyncMode.Off,

            // Opening a window the user did not click on must not take the keyboard away from
            // whatever they were doing, least of all from the game.
            StartFocused = false,
            StartVisible = true,
        });

        try
        {
            window.MakeCurrent();
            var controller = new ImGuiController(window, withClipboard: false);

            var pad = new PadWindow(window, controller, settings);
            pad.SetAlwaysOnTop(settings.InputWindowOnTop);
            return pad;
        }
        catch
        {
            // A window with no controller would sit there drawing nothing and never close.
            window.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The skin at its own size, clamped to 60% of the primary monitor so a big skin on a small
    /// screen still opens as a window the user can reach, then whatever size it was left at.
    /// </summary>
    private static (int Width, int Height) StartSize(Settings settings)
    {
        var (width, height) = InputDisplayPanel.NativeSize(settings);

        var monitor = Monitors.GetPrimaryMonitor();
        int maxWidth = Math.Max(200, (int)(monitor.ClientArea.Size.X * 0.6f));
        int maxHeight = Math.Max(150, (int)(monitor.ClientArea.Size.Y * 0.6f));

        float fit = Math.Min(1f, Math.Min((float)maxWidth / width, (float)maxHeight / height));
        width = Math.Max(200, (int)(width * fit));
        height = Math.Max(150, (int)(height * fit));

        return (settings.InputWindowW ?? width, settings.InputWindowH ?? height);
    }

    /// <summary>
    /// Where it was last left, or centred on the main window the first time - and also the first
    /// time after the monitor it was left on has gone, which would otherwise open it where nobody
    /// can reach it.
    /// </summary>
    private static Vector2i StartLocation(Settings settings, NativeWindow main, int width, int height)
    {
        if (settings.InputWindowX is { } x && settings.InputWindowY is { } y && OnAMonitor(x, y))
        {
            return new Vector2i(x, y);
        }

        var location = main.Location;
        var size = main.Size;
        return new Vector2i(
            location.X + (size.X - width) / 2,
            location.Y + (size.Y - height) / 2);
    }

    /// <summary>True when that screen point is on one of the monitors the desktop currently has.</summary>
    private static bool OnAMonitor(int x, int y)
    {
        foreach (var monitor in Monitors.GetMonitors())
        {
            var area = monitor.ClientArea;
            if (x >= area.Min.X && x < area.Max.X && y >= area.Min.Y && y < area.Max.Y) return true;
        }

        return false;
    }

    /// <summary>
    /// GLFW's floating attribute. Windows and X11 honour it; a Wayland compositor may not, which
    /// is why the pad window is worth having even without it.
    /// </summary>
    public void SetAlwaysOnTop(bool onTop)
    {
        if (_disposed) return;
        _onTop = onTop;

        unsafe
        {
            GLFW.SetWindowAttrib(_window.WindowPtr, WindowAttribute.Floating, onTop);
        }
    }

    /// <summary>
    /// One frame of the pad window, drawn after the main window's frame and leaving the pad's
    /// context current: the caller puts its own back.
    /// </summary>
    public void Render(AppState state, float deltaSeconds)
    {
        if (_disposed || Closed) return;

        _window.ProcessEvents(0);

        if (_window.IsExiting)
        {
            Closed = true;
            return;
        }

        if (_onTop != _settings.InputWindowOnTop) SetAlwaysOnTop(_settings.InputWindowOnTop);

        RememberGeometry();

        var client = _window.ClientSize;
        var framebuffer = _window.FramebufferSize;
        if (client.X <= 0 || client.Y <= 0 || framebuffer.X <= 0 || framebuffer.Y <= 0) return;

        _window.MakeCurrent();

        _controller.Update(deltaSeconds);
        InputDisplayPanel.DrawOwnWindow(state, _controller, new System.Numerics.Vector2(client.X, client.Y));

        GL.Viewport(0, 0, framebuffer.X, framebuffer.Y);
        GL.ClearColor(Background.X, Background.Y, Background.Z, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        _controller.Render();
        _window.Context.SwapBuffers();
    }

    /// <summary>
    /// Keeps the saved geometry in step with the live window. Only the fields are written here;
    /// the file is written when something else saves the settings, and on the way out.
    /// </summary>
    private void RememberGeometry()
    {
        if (_window.WindowState == WindowState.Minimized) return;

        var location = _window.Location;
        var client = _window.ClientSize;
        if (client.X <= 0 || client.Y <= 0) return;

        _settings.InputWindowX = location.X;
        _settings.InputWindowY = location.Y;
        _settings.InputWindowW = client.X;
        _settings.InputWindowH = client.Y;
    }

    /// <summary>
    /// Closes the window. The pad's context is made current first, because the controller's GL
    /// objects belong to it, and it is gone by the time this returns: the caller has to make the
    /// main window's context current again.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_window.IsExiting) RememberGeometry();
        _settings.Save();

        _window.MakeCurrent();
        InputDisplayPanel.Release(_controller);
        _controller.Dispose();
        _window.Dispose();
    }
}
