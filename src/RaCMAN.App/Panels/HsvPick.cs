using System.Numerics;

namespace RaCMAN.App.Panels;

/// <summary>
/// RGB and HSV for the colour pickers, with the hue and saturation of a colour that has none kept
/// from the colour before it: a grey (S 0) has no hue and black (V 0) no hue or saturation, so a
/// conversion that started from scratch would hand back hue 0 and lose what the user picked.
/// </summary>
public static class ColourMath
{
    /// <summary>
    /// 0xRRGGBB as hue, saturation and value, each 0..1. Where the colour has no hue (a grey) the
    /// hue is <paramref name="previous"/>'s, and where it has no saturation either (black) so is
    /// the saturation.
    /// </summary>
    public static Vector3 ToHsv(uint rgb, Vector3 previous)
    {
        float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, b = (rgb & 0xFF) / 255f;
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b)), d = max - min;

        if (max <= 0f) return new Vector3(previous.X, previous.Y, 0f);
        if (d <= 0f) return new Vector3(previous.X, 0f, max);

        float h = max == r ? (g - b) / d : max == g ? 2f + (b - r) / d : 4f + (r - g) / d;
        h /= 6f;
        if (h < 0f) h += 1f;
        return new Vector3(h, d / max, max);
    }

    /// <summary>Hue, saturation and value, each 0..1, as 0xRRGGBB, each channel rounded.</summary>
    public static uint ToRgb(Vector3 hsv)
    {
        float h = hsv.X - MathF.Floor(hsv.X), s = Math.Clamp(hsv.Y, 0f, 1f), v = Math.Clamp(hsv.Z, 0f, 1f);
        float c = v * s, x = c * (1f - MathF.Abs(h * 6f % 2f - 1f)), m = v - c;
        (float r, float g, float b) = (int)(h * 6f) switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };

        static uint Channel(float f) => (uint)Math.Clamp(MathF.Round(f * 255f), 0f, 255f);
        return (Channel(r + m) << 16) | (Channel(g + m) << 8) | Channel(b + m);
    }
}

/// <summary>
/// One colour picker's own hue, saturation and value. The picker edits these and never the RGB, so
/// dragging the saturation or the value to 0 and back keeps the hue. The colour is worked out again
/// from RGB only when it changes from outside (the console's readout, a preset, "(none)"), and not
/// while the readout is still catching up with a pick: for <see cref="Hold"/> seconds after one it
/// is the picker's own value that counts.
/// </summary>
public sealed class HsvPick
{
    /// <summary>How long after a pick a readout that disagrees is taken for one not yet caught up.</summary>
    public const double Hold = 0.5;

    private bool _known;
    private double _pickedAt = double.NegativeInfinity;

    /// <summary>What the picker shows and edits: hue, saturation and value, each 0..1.</summary>
    public Vector3 Hsv { get; private set; }

    /// <summary>The 0xRRGGBB the picker last took or sent.</summary>
    public uint Rgb { get; private set; }

    /// <summary>The colour the outside world has now, at <paramref name="now"/> seconds.</summary>
    public void Sync(uint rgb, double now)
    {
        rgb &= 0xFFFFFFu;
        if (_known && (rgb == Rgb || now - _pickedAt < Hold)) return;

        Hsv = ColourMath.ToHsv(rgb, _known ? Hsv : Vector3.Zero);
        Rgb = rgb;
        _known = true;
    }

    /// <summary>The user picked <paramref name="hsv"/>; answers the 0xRRGGBB to send.</summary>
    public uint Pick(Vector3 hsv, double now)
    {
        Hsv = hsv;
        Rgb = ColourMath.ToRgb(hsv);
        _known = true;
        _pickedAt = now;
        return Rgb;
    }

    /// <summary>The colour was changed from outside by this client: take the next readout at once.</summary>
    public void Release() => _pickedAt = double.NegativeInfinity;
}
