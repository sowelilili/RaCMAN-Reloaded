using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaCMAN.App;

/// <summary>Where the input display draws.</summary>
public enum InputDisplayMode
{
    /// <summary>Inside the Input display panel, with the other controls.</summary>
    Panel,

    /// <summary>A plain ImGui window that can be dragged anywhere inside the main window.</summary>
    Floating,

    /// <summary>Its own OS window, which a capture tool can pick up on its own.</summary>
    Window,
}

/// <summary>Client preferences. Nothing about the game lives here: qwark owns all of that.</summary>
public sealed class Settings
{
    [JsonPropertyName("lastHost")]
    public string LastHost { get; set; } = "192.168.1.";

    [JsonPropertyName("autoReconnect")]
    public bool AutoReconnect { get; set; } = true;

    /// <summary>UI theme, "light" (the default) or "dark". Anything unrecognised reads as light.</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "light";

    /// <summary>
    /// Shows the wire-level detail in the UI: versions, counters, opcode names and internal
    /// addresses. Off by default, because none of it means anything to someone playing a game.
    /// </summary>
    [JsonPropertyName("debugInfo")]
    public bool DebugInfo { get; set; }

    /// <summary>True unless <see cref="Theme"/> explicitly says "dark".</summary>
    [JsonIgnore]
    public bool LightTheme => !string.Equals(Theme, "dark", StringComparison.OrdinalIgnoreCase);

    [JsonPropertyName("webManSlot")]
    public int WebManSlot { get; set; } = 5;

    [JsonPropertyName("sprxPath")]
    public string SprxPath { get; set; } = "qwark.sprx";

    [JsonPropertyName("modsPath")]
    public string ModsPath { get; set; } = "mods";

    /// <summary>Root of the savefile library, <c>savefiles/&lt;TITLEID&gt;/&lt;category&gt;/</c>.</summary>
    [JsonPropertyName("saveFilesPath")]
    public string SaveFilesPath { get; set; } = "savefiles";

    [JsonPropertyName("lastZipPath")]
    public string LastZipPath { get; set; } = string.Empty;

    /// <summary>Folder name under controllerskins/ for the input display.</summary>
    [JsonPropertyName("inputSkin")]
    public string InputSkin { get; set; } = "DS3 Black";

    [JsonPropertyName("inputScale")]
    public float InputScale { get; set; } = 1f;

    /// <summary>The pad floats in a plain ImGui window inside the main window. Predates <see cref="InputWindowed"/>.</summary>
    [JsonPropertyName("inputFloating")]
    public bool InputFloating { get; set; }

    /// <summary>The pad has its own OS window, so a capture tool can take it as a source of its own.</summary>
    [JsonPropertyName("inputWindowed")]
    public bool InputWindowed { get; set; }

    /// <summary>Keeps the pad window above other windows. Only meaningful with <see cref="InputWindowed"/>.</summary>
    [JsonPropertyName("inputWindowOnTop")]
    public bool InputWindowOnTop { get; set; }

    /// <summary>
    /// Where the pad window was last left, in screen coordinates, and how big it was. Null until
    /// the window has been opened once, which is what centres it on the main window the first time.
    /// </summary>
    [JsonPropertyName("inputWindowX")]
    public int? InputWindowX { get; set; }

    [JsonPropertyName("inputWindowY")]
    public int? InputWindowY { get; set; }

    [JsonPropertyName("inputWindowW")]
    public int? InputWindowW { get; set; }

    [JsonPropertyName("inputWindowH")]
    public int? InputWindowH { get; set; }

    /// <summary>
    /// The three ways the pad can be shown, over the two flags an older settings file may hold:
    /// a file that only knows "inputFloating" still opens on the floating pad.
    /// </summary>
    [JsonIgnore]
    public InputDisplayMode InputMode
    {
        get => InputWindowed ? InputDisplayMode.Window
            : InputFloating ? InputDisplayMode.Floating
            : InputDisplayMode.Panel;
        set
        {
            InputWindowed = value == InputDisplayMode.Window;
            InputFloating = value == InputDisplayMode.Floating;
        }
    }

    /// <summary>Set once the first-run "allow through the firewall" offer has been shown, so it never nags again.</summary>
    [JsonPropertyName("firewallOffered")]
    public bool FirewallOffered { get; set; }

    [JsonIgnore]
    public string Path { get; private set; } = string.Empty;

    public static string DefaultPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "racman-reloaded.settings.json");

    public static Settings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    loaded.Path = path;
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file must never stop the client from starting.
        }

        return new Settings { Path = path };
    }

    public void Save()
    {
        try
        {
            if (string.IsNullOrEmpty(Path)) Path = DefaultPath;
            File.WriteAllText(Path, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are best effort.
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
}

/// <summary>One saved watch name, so a watchlist survives a restart even though qwark owns the watch.</summary>
public sealed class SavedWatch
{
    [JsonPropertyName("address")]
    public uint Address { get; set; }

    [JsonPropertyName("size")]
    public byte Size { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = "dec";
}

/// <summary>Per-title watchlist files, one of the few things the PC still owns.</summary>
public sealed class WatchlistStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public WatchlistStore(string folder)
    {
        Folder = folder;
    }

    public string Folder { get; }

    public string FileFor(string titleId, string? name = null) =>
        System.IO.Path.Combine(Folder, string.IsNullOrEmpty(name) ? $"{titleId}.json" : $"{titleId}.{name}.json");

    public List<SavedWatch> Load(string titleId, string? name = null)
    {
        var path = FileFor(titleId, name);
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<List<SavedWatch>>(File.ReadAllText(path)) ?? new List<SavedWatch>();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Treat an unreadable watchlist as empty.
        }

        return new List<SavedWatch>();
    }

    public void Save(string titleId, IEnumerable<SavedWatch> watches, string? name = null)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FileFor(titleId, name), JsonSerializer.Serialize(watches.ToList(), SerializerOptions));
    }

    public IEnumerable<string> ListFor(string titleId)
    {
        if (!Directory.Exists(Folder)) yield break;
        foreach (var file in Directory.EnumerateFiles(Folder, $"{titleId}*.json"))
        {
            yield return System.IO.Path.GetFileName(file);
        }
    }

    /// <summary>
    /// The name behind a file name from <see cref="ListFor"/>: null for the title's default list,
    /// the text between the title and ".json" for a named one. False when the file is not this
    /// title's at all, which <see cref="ListFor"/> can hand over because its pattern is a prefix
    /// match: "NPEA00386X.json" is another title's file, not a list named "X".
    /// </summary>
    public static bool TryGetName(string titleId, string fileName, out string? name)
    {
        name = null;
        if (string.IsNullOrEmpty(titleId) || string.IsNullOrEmpty(fileName)) return false;

        var stem = System.IO.Path.GetFileName(fileName);
        if (!stem.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
        stem = stem[..^".json".Length];

        if (string.Equals(stem, titleId, StringComparison.OrdinalIgnoreCase)) return true;

        if (stem.Length > titleId.Length + 1 && stem.StartsWith(titleId + ".", StringComparison.OrdinalIgnoreCase))
        {
            name = stem[(titleId.Length + 1)..];
            return true;
        }

        return false;
    }

    /// <summary>
    /// Removes one watchlist file. False when there was nothing to remove; an IO error is thrown
    /// rather than swallowed, because a delete that quietly did nothing is worse than a message.
    /// </summary>
    public bool Delete(string titleId, string? name = null)
    {
        var path = FileFor(titleId, name);
        if (!File.Exists(path)) return false;

        File.Delete(path);
        return true;
    }
}
