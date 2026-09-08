using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaCMAN.App;

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

    [JsonPropertyName("inputFloating")]
    public bool InputFloating { get; set; }

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
}
