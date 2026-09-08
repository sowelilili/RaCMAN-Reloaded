using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;
using StbImageSharp;

namespace RaCMAN.App.Panels;

/// <summary>
/// The pad drawn from pad_mask and analog[] with the old client's controller skins: one sprite
/// sheet per skin, the base blitted first and then every pressed button's sprite on top. The
/// panel can also float in its own plain window so it can sit over a stream layout.
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
    private static LoadedSkin? _loaded;
    private static string? _loadError;
    private static string _wanted = string.Empty;

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
        EnsureSkin(state, controller);

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
            _wanted = string.Empty;
        }

        float scale = settings.InputScale;
        ImGui.SetNextItemWidth(260);
        if (ImGui.SliderFloat("Scale", ref scale, 0.25f, 2f, "%.2fx"))
        {
            settings.InputScale = Math.Clamp(scale, 0.25f, 2f);
        }

        if (ImGui.IsItemDeactivatedAfterEdit()) settings.Save();

        bool floating = settings.InputFloating;
        if (ImGui.Checkbox("Float in its own window", ref floating))
        {
            settings.InputFloating = floating;
            settings.Save();
        }

        ImGui.SameLine();
        Ui.Hint("A floating pad stays inside this window but can be dragged over any panel.");

        ImGui.TextColored(Ui.Grey,
            $"mask 0x{session.PadMask:X4}   rx {Get(session.Analog, 0):0.00}  ry {Get(session.Analog, 1):0.00}  " +
            $"lx {Get(session.Analog, 2):0.00}  ly {Get(session.Analog, 3):0.00}");

        if (_loadError is not null) Ui.Error(_loadError);

        if (_loaded is { } loaded && loaded.Skin.Missing.Count > 0)
        {
            Ui.Warning($"skin.txt is missing: {string.Join(", ", loaded.Skin.Missing)}");
        }

        ImGui.Spacing();

        if (settings.InputFloating)
        {
            Ui.Hint("The pad is in its own window; untick the box to bring it back here.");
        }
        else
        {
            float fit = FitScale(settings.InputScale);
            if (fit < settings.InputScale - 0.005f)
            {
                Ui.Hint($"Shown at {fit:0.00}x so the whole pad fits here; float it for the full size.");
            }

            DrawPad(state, fit);
        }

        if (!state.Connected) Ui.Hint("Not connected: the pad shows the last telemetry packet, if any.");
    }

    /// <summary>
    /// The largest scale the pad can be drawn at and still fit the panel. A skin is around 800 by
    /// 730 pixels, wider than the panel at the default window size, and the pad is drawn straight
    /// onto the window's draw list: anything past the right-hand edge is simply not reachable.
    /// The slider still owns the scale; this only caps it, and only for the embedded pad, because
    /// the floating window sizes itself to whatever it is given.
    /// </summary>
    private static float FitScale(float scale)
    {
        var loaded = _loaded;
        float width = loaded?.Skin.Base.Width ?? FallbackWidth;
        float height = loaded?.Skin.Base.Height ?? FallbackHeight;
        if (width <= 0 || height <= 0) return scale;

        // Reserve the scrollbar and the spacing below the pad in both directions. Sized to exactly
        // what is left, the pad would overflow by that spacing, raise a scrollbar, lose the width
        // the scrollbar takes, shrink, drop the scrollbar, and flicker between the two every frame.
        var style = ImGui.GetStyle();
        var available = ImGui.GetContentRegionAvail();
        float usableX = available.X - style.ScrollbarSize;
        float usableY = available.Y - style.ItemSpacing.Y * 2;
        if (usableX <= 0 || usableY <= 0) return scale;

        return Math.Min(scale, Math.Min(usableX / width, usableY / height));
    }

    /// <summary>
    /// The floating pad. Drawn from the frame loop rather than the panel so it stays up while
    /// another panel is on screen. It is a plain ImGui window inside the one OS window.
    /// </summary>
    public static void DrawFloating(AppState state, ImGuiController controller)
    {
        if (!state.Settings.InputFloating) return;

        Scan();
        EnsureSkin(state, controller);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDocking
                                       | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoNav
                                       | ImGuiWindowFlags.NoFocusOnAppearing;

        ImGui.SetNextWindowBgAlpha(0.85f);
        ImGui.SetNextWindowSize(Vector2.Zero, ImGuiCond.Always);

        bool open = true;
        if (ImGui.Begin("Input display##floating", ref open, flags))
        {
            DrawPad(state, state.Settings.InputScale);
        }

        ImGui.End();

        if (!open)
        {
            state.Settings.InputFloating = false;
            state.Settings.Save();
        }
    }

    public static void Dispose()
    {
        _loaded?.Dispose();
        _loaded = null;
    }

    // ---------------------------------------------------------------- skin plumbing

    private static void Scan()
    {
        if (_scanned) return;
        _scanned = true;
        _available = SkinLibrary.List();
    }

    private static void EnsureSkin(AppState state, ImGuiController controller)
    {
        string wanted = state.Settings.InputSkin;
        if (_available.Length > 0 && !_available.Contains(wanted, StringComparer.Ordinal))
        {
            wanted = _available[0];
            state.Settings.InputSkin = wanted;
        }

        if (wanted.Length == 0 || string.Equals(wanted, _wanted, StringComparison.Ordinal)) return;

        _wanted = wanted;
        _loadError = null;

        _loaded?.Dispose();
        _loaded = null;

        try
        {
            var skin = SkinLibrary.Load(wanted);
            using var stream = File.OpenRead(skin.ImagePath);
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            if (image.Data is null || image.Width <= 0 || image.Height <= 0)
            {
                throw new InvalidDataException($"{skin.ImageFileName} decoded to nothing");
            }

            int texture = controller.CreateTexture(image.Width, image.Height, image.Data);
            _loaded = new LoadedSkin
            {
                Skin = skin,
                Texture = texture,
                Width = image.Width,
                Height = image.Height,
                Owner = controller,
            };

            Status = $"{skin.Name} {image.Width}x{image.Height} pitch {skin.AnalogPitch}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or ArgumentException or DirectoryNotFoundException)
        {
            _loadError = $"skin '{wanted}': {ex.Message}";
            Status = _loadError;
        }
    }

    // ---------------------------------------------------------------- drawing

    private static void DrawPad(AppState state, float scale)
    {
        var loaded = _loaded;
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
