using System.Globalization;

namespace RaCMAN.App;

/// <summary>One entry of a skin.txt: where to draw, and which rectangle of the sheet to draw.</summary>
public readonly record struct SkinSprite(int DrawX, int DrawY, int SpriteX, int SpriteY, int Width, int Height);

/// <summary>
/// A controllerskins/&lt;name&gt;/skin.txt, in the format the old client defined:
/// <c>name: drawX, drawY, spriteX, spriteY, spriteWidth, spriteHeight</c> lines, plus
/// <c>analogPitch: N</c> and <c>imageName: skin.png</c>. Lines that are blank or start with
/// '#' are comments. Nothing here touches OpenGL, so it is testable on its own.
/// </summary>
public sealed class ControllerSkin
{
    /// <summary>The sprite names the input display draws, in draw order after the base.</summary>
    public static readonly string[] ButtonSprites =
    {
        "dpadUp", "dpadRight", "dpadDown", "dpadLeft",
        "triangle", "circle", "cross", "square",
        "select", "start",
        "l1", "l2", "r1", "r2",
    };

    /// <summary>Every name a complete skin defines.</summary>
    public static readonly string[] RequiredSprites =
        new[] { "base", "l3", "l3Press", "r3", "r3Press" }.Concat(ButtonSprites).ToArray();

    private readonly Dictionary<string, SkinSprite> _sprites;

    private ControllerSkin(string name, string directory, string imageFileName, int analogPitch,
        Dictionary<string, SkinSprite> sprites)
    {
        Name = name;
        Directory = directory;
        ImageFileName = imageFileName;
        AnalogPitch = analogPitch;
        _sprites = sprites;
    }

    public string Name { get; }

    public string Directory { get; }

    public string ImageFileName { get; }

    public string ImagePath => Path.Combine(Directory, ImageFileName);

    public int AnalogPitch { get; }

    public IReadOnlyDictionary<string, SkinSprite> Sprites => _sprites;

    public SkinSprite Base => _sprites["base"];

    /// <summary>Names <see cref="RequiredSprites"/> lists that this skin does not define.</summary>
    public IReadOnlyList<string> Missing =>
        RequiredSprites.Where(name => !_sprites.ContainsKey(name)).ToArray();

    public bool TryGet(string name, out SkinSprite sprite) => _sprites.TryGetValue(name, out sprite);

    /// <summary>
    /// The sprite for a stick. The shipped skins name the highlighted cell "r3"/"l3" and the
    /// idle cell "r3Press"/"l3Press", the opposite way round from what the names suggest; the
    /// old client drew them that way and every skin was authored against it, so we match.
    /// </summary>
    public bool TryGetStick(string stick, bool pressed, out SkinSprite sprite) =>
        TryGet(pressed ? stick : stick + "Press", out sprite);

    public static ControllerSkin Load(string directory)
    {
        string file = Path.Combine(directory, "skin.txt");
        return Parse(Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            directory, File.ReadAllText(file));
    }

    public static ControllerSkin Parse(string name, string directory, string text)
    {
        var sprites = new Dictionary<string, SkinSprite>(StringComparer.Ordinal);
        string image = "skin.png";
        int pitch = 32;

        foreach (var rawLine in (text ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length < 2 || line[0] == '#') continue;

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (value.Length == 0) continue;

            if (key == "imageName")
            {
                image = value;
                continue;
            }

            if (key == "analogPitch")
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) pitch = parsed;
                continue;
            }

            var parts = value.Split(',');
            if (parts.Length < 6) continue;

            var numbers = new int[6];
            bool ok = true;
            for (int i = 0; i < 6 && ok; i++)
            {
                ok = int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]);
            }

            if (!ok) continue;

            sprites[key] = new SkinSprite(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]);
        }

        if (!sprites.ContainsKey("base"))
        {
            throw new InvalidDataException($"skin '{name}' has no 'base' line");
        }

        return new ControllerSkin(name, directory, image, pitch, sprites);
    }
}

/// <summary>
/// Finds the controllerskins folder and lists what is in it. The folder sits beside the
/// solution rather than in the build output, so this walks up from the executable.
/// </summary>
public static class SkinLibrary
{
    public const string FolderName = "controllerskins";

    /// <summary>The first controllerskins folder at or above the executable, or null.</summary>
    public static string? FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            for (int depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, FolderName);
                if (System.IO.Directory.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>Skin folder names, sorted, or empty when the library is missing.</summary>
    public static string[] List(string? root = null)
    {
        root ??= FindRoot();
        if (root is null || !System.IO.Directory.Exists(root)) return Array.Empty<string>();

        return System.IO.Directory.EnumerateDirectories(root)
            .Where(directory => File.Exists(Path.Combine(directory, "skin.txt")))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static ControllerSkin Load(string skinName, string? root = null)
    {
        root ??= FindRoot() ?? throw new DirectoryNotFoundException($"no {FolderName} folder found");
        return ControllerSkin.Load(Path.Combine(root, skinName));
    }
}
