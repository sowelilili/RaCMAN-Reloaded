using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;
using StbImageSharp;

namespace RaCMAN.App.Panels;

/// <summary>
/// The pad drawn from pad_mask and analog[] with the old client's controller skins: one sprite
/// sheet per skin, the base blitted first and then every pressed button's sprite on top. It can
/// be drawn in the panel or in its own OS window, so a capture tool can take it as a source of
/// its own.
/// </summary>
public static class InputDisplayPanel
{
    private sealed class LoadedSkin : IDisposable
    {
        public required ControllerSkin Skin { get; init; }

        public required int Texture { get; init; }

        public required int Width { get; init; }

        public required int Height { get; init; }

        public ImGuiController? Owner { get; init; }

        public void Dispose() => Owner?.DeleteTexture(Texture);
    }

    private static readonly (string Sprite, PadButton Button)[] ButtonSprites =
    {
        ("dpadUp", PadButton.Up),
        ("dpadRight", PadButton.Right),
        ("dpadDown", PadButton.Down),
        ("dpadLeft", PadButton.Left),
        ("triangle", PadButton.Triangle),
        ("circle", PadButton.Circle),
        ("cross", PadButton.Cross),
        ("square", PadButton.Square),
        ("select", PadButton.Select),
        ("start", PadButton.Start),
        ("l1", PadButton.L1),
        ("l2", PadButton.L2),
        ("r1", PadButton.R1),
        ("r2", PadButton.R2),
    };

    private static string[] _available = Array.Empty<string>();
    private static bool _scanned;
    private static string? _loadError;

    /// <summary>
    /// The sheet, once per controller: a texture belongs to the GL context that uploaded it, and
    /// the pad window has a context of its own. Both entries hold the same skin, and both are
    /// reloaded when the picker changes.
    /// </summary>
    private static readonly Dictionary<ImGuiController, LoadedSkin> Skins = new();

    private static readonly Dictionary<ImGuiController, string> Wanted = new();

    /// <summary>
    /// What the input display ended up drawing. Kept as a string rather than read off the
    /// loaded skin so it survives <see cref="Dispose"/>, which runs before the smoke-run
    /// summary is printed.
    /// </summary>
    public static string Status { get; private set; } = "none";

    private static readonly uint Lit = ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.85f, 0.45f, 1f));
    private static readonly uint Dim = ImGui.ColorConvertFloat4ToU32(new Vector4(0.28f, 0.28f, 0.32f, 1f));
    private static readonly uint Outline = ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.55f, 0.6f, 1f));
    private static readonly uint TextColour = ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.9f, 0.9f, 1f));

    public static void Draw(AppState state, ImGuiController controller)
    {
        Ui.Heading("Input display");

        Scan();
        var loaded = EnsureSkin(state, controller);

        var settings = state.Settings;
        var session = state.Session;

        ImGui.SetNextItemWidth(260);
        int index = Math.Max(0, Array.IndexOf(_available, settings.InputSkin));
        if (_available.Length > 0)
        {
            if (ImGui.Combo("Skin", ref index, _available, _available.Length))
            {
                settings.InputSkin = _available[index];
                settings.Save();
            }
        }
        else
        {
            Ui.Hint($"No {SkinLibrary.FolderName} folder found beside the executable or the solution.");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Rescan"))
        {
            _scanned = false;

            // Both controllers reload on the next frame they draw, the pad window's included.
            Wanted.Clear();
        }

        DrawModeChoice(settings);

        // The pad on the screen is the readout; the numbers behind it are only of use while
        // looking at the wire.
        Ui.DebugHint(
            $"mask 0x{session.PadMask:X4}   rx {Get(session.Analog, 0):0.00}  ry {Get(session.Analog, 1):0.00}  " +
            $"lx {Get(session.Analog, 2):0.00}  ly {Get(session.Analog, 3):0.00}");

        if (_loadError is not null) Ui.Error(_loadError);

        if (loaded is not null && loaded.Skin.Missing.Count > 0)
        {
            Ui.Warning($"skin.txt is missing: {string.Join(", ", loaded.Skin.Missing)}");
        }

        ImGui.Spacing();

        if (settings.InputMode == InputDisplayMode.Window)
        {
            Ui.Hint($"The pad is in its own window: capture \"{PadWindow.WindowTitle}\" as a window source, "
                    + "or close that window to bring the pad back here.");
        }
        else
        {
            DrawPad(state, loaded, FitScale(loaded));
        }

        if (!state.Connected) Ui.Hint("Not connected: the pad shows what the console last sent, if anything.");
    }

    /// <summary>
    /// The two places the pad can live, plus the one thing only the OS window can do. Switching
    /// is what opens and closes that window: the frame loop follows the setting.
    /// </summary>
    private static void DrawModeChoice(Settings settings)
    {
        var mode = settings.InputMode;
        var chosen = mode;

        if (ImGui.RadioButton("In the panel", mode == InputDisplayMode.Panel)) chosen = InputDisplayMode.Panel;
        ImGui.SameLine();
        if (ImGui.RadioButton("Own window", mode == InputDisplayMode.Window)) chosen = InputDisplayMode.Window;

        if (chosen != mode)
        {
            settings.InputMode = chosen;
            settings.Save();
        }

        ImGui.BeginDisabled(chosen != InputDisplayMode.Window);
        ImGui.SameLine();
        bool onTop = settings.InputWindowOnTop;
        if (ImGui.Checkbox("Always on top", ref onTop))
        {
            settings.InputWindowOnTop = onTop;
            settings.Save();
        }

        ImGui.EndDisabled();
    }

    /// <summary>
    /// The scale the embedded pad is drawn at: its own size, or less when the panel is too small
    /// for that. A skin is around 800 by 730 pixels, wider than the panel at the default window
    /// size, and the pad is drawn straight onto the window's draw list, so anything past the
    /// right-hand edge is simply not reachable. The pad's own window sizes the skin to itself.
    /// </summary>
    private static float FitScale(LoadedSkin? loaded)
    {
        float width = loaded?.Skin.Base.Width ?? FallbackWidth;
        float height = loaded?.Skin.Base.Height ?? FallbackHeight;

        // Reserve the scrollbar and the spacing below the pad in both directions. Sized to exactly
        // what is left, the pad would overflow by that spacing, raise a scrollbar, lose the width
        // the scrollbar takes, shrink, drop the scrollbar, and flicker between the two every frame.
        var style = ImGui.GetStyle();
        var available = ImGui.GetContentRegionAvail();
        return FitScale(width, height, available.X - style.ScrollbarSize, available.Y - style.ItemSpacing.Y * 2);
    }

    /// <summary>
    /// The fit itself, without ImGui in it: never larger than the skin was drawn at, and smaller
    /// by whichever of the two axes runs out of room first. A size that makes no sense, from a
    /// skin that failed to load or from a panel with nothing left in it, draws at 1x.
    /// </summary>
    public static float FitScale(float width, float height, float usableX, float usableY)
    {
        if (width <= 0 || height <= 0 || usableX <= 0 || usableY <= 0) return 1f;

        return Math.Min(1f, Math.Min(usableX / width, usableY / height));
    }

    /// <summary>
    /// The whole content of the pad's own OS window: one borderless ImGui window filling the
    /// client area, with the skin scaled to fit and centred. The window's own size is the scale,
    /// so the pad grows and shrinks with it and opens at the size the skin was drawn at.
    /// </summary>
    public static void DrawOwnWindow(AppState state, ImGuiController controller, Vector2 clientSize)
    {
        Scan();
        var loaded = EnsureSkin(state, controller);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                                       | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse
                                       | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                                       | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus
                                       | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings;

        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(clientSize);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        if (ImGui.Begin("##padwindow", flags))
        {
            float width = loaded?.Skin.Base.Width ?? FallbackWidth;
            float height = loaded?.Skin.Base.Height ?? FallbackHeight;

            // The vector fallback pad has its coordinates baked in and always draws at 1x, so
            // only a real skin is scaled to the window.
            float scale = loaded is not null && width > 0 && height > 0
                ? Math.Min(clientSize.X / width, clientSize.Y / height)
                : 1f;

            ImGui.SetCursorPos(new Vector2(
                Math.Max(0f, (clientSize.X - width * scale) / 2f),
                Math.Max(0f, (clientSize.Y - height * scale) / 2f)));

            DrawPad(state, loaded, scale);
        }

        ImGui.End();
        ImGui.PopStyleVar();
    }

    /// <summary>
    /// The skin's own pixel size, read without uploading anything, so the pad window can open at
    /// the size the skin was drawn at. Falls back to the vector pad's size when no skin loads.
    /// </summary>
    public static (int Width, int Height) NativeSize(Settings settings)
    {
        try
        {
            var skin = SkinLibrary.Load(settings.InputSkin);
            var sprite = skin.Base;
            if (sprite.Width > 0 && sprite.Height > 0) return (sprite.Width, sprite.Height);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or ArgumentException or DirectoryNotFoundException)
        {
            // The panel reports the load error; the window just needs a size.
        }

        return ((int)FallbackWidth, (int)FallbackHeight);
    }

    /// <summary>Drops one controller's copy of the sheet, before that controller is disposed.</summary>
    public static void Release(ImGuiController controller)
    {
        if (Skins.Remove(controller, out var loaded)) loaded.Dispose();
        Wanted.Remove(controller);
    }

    public static void Dispose()
    {
        foreach (var loaded in Skins.Values) loaded.Dispose();
        Skins.Clear();
        Wanted.Clear();
    }

    // ---------------------------------------------------------------- skin plumbing

    private static void Scan()
    {
        if (_scanned) return;
        _scanned = true;
        _available = SkinLibrary.List();
    }

    private static LoadedSkin? EnsureSkin(AppState state, ImGuiController controller)
    {
        string wanted = state.Settings.InputSkin;
        if (_available.Length > 0 && !_available.Contains(wanted, StringComparer.Ordinal))
        {
            wanted = _available[0];
            state.Settings.InputSkin = wanted;
        }

        Skins.TryGetValue(controller, out var current);

        bool upToDate = Wanted.TryGetValue(controller, out var already)
                        && string.Equals(wanted, already, StringComparison.Ordinal);
        if (wanted.Length == 0 || upToDate) return current;

        Release(controller);
        Wanted[controller] = wanted;
        _loadError = null;

        try
        {
            var skin = SkinLibrary.Load(wanted);
            using var stream = File.OpenRead(skin.ImagePath);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            if (image.Data is null || image.Width <= 0 || image.Height <= 0)
            {
                throw new InvalidDataException($"{skin.ImageFileName} decoded to nothing");
            }

            var loaded = new LoadedSkin
            {
                Skin = skin,
                Texture = controller.CreateTexture(image.Width, image.Height, image.Data),
                Width = image.Width,
                Height = image.Height,
                Owner = controller,
            };

            Skins[controller] = loaded;
            Status = $"{skin.Name} {image.Width}x{image.Height} pitch {skin.AnalogPitch}";
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or ArgumentException or DirectoryNotFoundException)
        {
            _loadError = $"skin '{wanted}': {ex.Message}";
            Status = _loadError;
            return null;
        }
    }

    // ---------------------------------------------------------------- drawing

    private static void DrawPad(AppState state, LoadedSkin? loaded, float scale)
    {
        if (loaded is null)
        {
            DrawFallbackPad(state);
            return;
        }

        var session = state.Session;
        uint mask = session.PadMask;
        var analog = session.Analog;
        var skin = loaded.Skin;

        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var baseSprite = skin.Base;

        Blit(draw, loaded, origin, baseSprite, scale, 0, 0);

        foreach (var (name, button) in ButtonSprites)
        {
            if ((mask & (uint)button) == 0) continue;
            if (skin.TryGet(name, out var sprite)) Blit(draw, loaded, origin, sprite, scale, 0, 0);
        }

        float pitch = skin.AnalogPitch;
        DrawStick(draw, loaded, origin, scale, "l3", (mask & (uint)PadButton.L3) != 0,
            Get(analog, 2) * pitch, Get(analog, 3) * pitch);
        DrawStick(draw, loaded, origin, scale, "r3", (mask & (uint)PadButton.R3) != 0,
            Get(analog, 0) * pitch, Get(analog, 1) * pitch);

        ImGui.Dummy(new Vector2(baseSprite.Width * scale, baseSprite.Height * scale));
    }

    private static void DrawStick(ImDrawListPtr draw, LoadedSkin loaded, Vector2 origin, float scale,
        string stick, bool pressed, float offsetX, float offsetY)
    {
        if (loaded.Skin.TryGetStick(stick, pressed, out var sprite))
        {
            Blit(draw, loaded, origin, sprite, scale, offsetX, offsetY);
        }
    }

    private static void Blit(ImDrawListPtr draw, LoadedSkin loaded, Vector2 origin, SkinSprite sprite,
        float scale, float offsetX, float offsetY)
    {
        if (sprite.Width <= 0 || sprite.Height <= 0) return;

        var min = origin + new Vector2((sprite.DrawX + offsetX) * scale, (sprite.DrawY + offsetY) * scale);
        var max = min + new Vector2(sprite.Width * scale, sprite.Height * scale);

        var uv0 = new Vector2((float)sprite.SpriteX / loaded.Width, (float)sprite.SpriteY / loaded.Height);
        var uv1 = new Vector2((float)(sprite.SpriteX + sprite.Width) / loaded.Width,
            (float)(sprite.SpriteY + sprite.Height) / loaded.Height);

        draw.AddImage(loaded.Texture, min, max, uv0, uv1);
    }

    private const float FallbackWidth = 460f;
    private const float FallbackHeight = 260f;

    /// <summary>The vector pad from milestone 1, used while no skin could be loaded.</summary>
    private static void DrawFallbackPad(AppState state)
    {
        const float width = FallbackWidth;
        const float height = FallbackHeight;

        var session = state.Session;
        uint mask = session.PadMask;
        var analog = session.Analog;

        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        draw.AddRectFilled(origin, origin + new Vector2(width, height),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.12f, 0.14f, 1f)), 8f);
        draw.AddRect(origin, origin + new Vector2(width, height), Outline, 8f);

        Rect(draw, origin, 55, 18, 70, 18, Down(mask, PadButton.L2), "L2");
        Rect(draw, origin, 335, 18, 70, 18, Down(mask, PadButton.R2), "R2");
        Rect(draw, origin, 55, 42, 70, 18, Down(mask, PadButton.L1), "L1");
        Rect(draw, origin, 335, 42, 70, 18, Down(mask, PadButton.R1), "R1");

        Rect(draw, origin, 85, 90, 26, 26, Down(mask, PadButton.Up), "");
        Rect(draw, origin, 85, 146, 26, 26, Down(mask, PadButton.Down), "");
        Rect(draw, origin, 57, 118, 26, 26, Down(mask, PadButton.Left), "");
        Rect(draw, origin, 113, 118, 26, 26, Down(mask, PadButton.Right), "");

        Circle(draw, origin, 366, 103, 15, Down(mask, PadButton.Triangle), "T");
        Circle(draw, origin, 396, 131, 15, Down(mask, PadButton.Circle), "O");
        Circle(draw, origin, 366, 159, 15, Down(mask, PadButton.Cross), "X");
        Circle(draw, origin, 336, 131, 15, Down(mask, PadButton.Square), "[]");

        Rect(draw, origin, 178, 118, 40, 14, Down(mask, PadButton.Select), "SEL");
        Rect(draw, origin, 242, 118, 40, 14, Down(mask, PadButton.Start), "ST");

        Stick(draw, origin, 172, 196, Get(analog, 2), Get(analog, 3), Down(mask, PadButton.L3), "L3");
        Stick(draw, origin, 288, 196, Get(analog, 0), Get(analog, 1), Down(mask, PadButton.R3), "R3");

        ImGui.Dummy(new Vector2(width, height + 8));
    }

    private static float Get(float[] values, int index) => index < values.Length ? values[index] : 0f;

    private static bool Down(uint mask, PadButton button) => (mask & (uint)button) != 0;

    private static void Rect(ImDrawListPtr draw, Vector2 origin, float x, float y, float w, float h, bool on, string label)
    {
        var min = origin + new Vector2(x, y);
        var max = min + new Vector2(w, h);
        draw.AddRectFilled(min, max, on ? Lit : Dim, 3f);
        draw.AddRect(min, max, Outline, 3f);
        if (label.Length > 0) draw.AddText(min + new Vector2(w / 2 - label.Length * 3.5f, h / 2 - 7), TextColour, label);
    }

    private static void Circle(ImDrawListPtr draw, Vector2 origin, float x, float y, float radius, bool on, string label)
    {
        var centre = origin + new Vector2(x, y);
        draw.AddCircleFilled(centre, radius, on ? Lit : Dim);
        draw.AddCircle(centre, radius, Outline);
        if (label.Length > 0) draw.AddText(centre - new Vector2(label.Length * 3.5f, 7), TextColour, label);
    }

    private static void Stick(ImDrawListPtr draw, Vector2 origin, float x, float y, float axisX, float axisY, bool pressed, string label)
    {
        const float well = 26f;
        var centre = origin + new Vector2(x, y);
        draw.AddCircleFilled(centre, well, pressed ? Lit : Dim);
        draw.AddCircle(centre, well, Outline);

        var dot = centre + new Vector2(Math.Clamp(axisX, -1f, 1f) * (well - 7), Math.Clamp(axisY, -1f, 1f) * (well - 7));
        draw.AddCircleFilled(dot, 6f, TextColour);
        draw.AddText(centre + new Vector2(-8, well + 4), TextColour, label);
    }
}
