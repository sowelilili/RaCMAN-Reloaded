using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>Where the input display draws.</summary>
public enum InputDisplayMode
{
    /// <summary>Inside the Input display panel, with the other controls.</summary>
    Panel,

    /// <summary>Its own OS window, which a capture tool can pick up on its own.</summary>
    Window,
}

/// <summary>Client preferences. Nothing about the game lives here: qwark owns all of that.</summary>
public sealed class Settings
{
    /// <summary>The PS3 the address box points at. Unused while <see cref="Rpcs3Target"/> is on.</summary>
    [JsonPropertyName("lastHost")]
    public string LastHost { get; set; } = "192.168.1.";

    /// <summary>
    /// Which qwark this client talks to: "ps3" (the module on a console, over the network) or
    /// "rpcs3" (qwark-rpcs3.exe on this PC, driving RPCS3 through its IPC server). Anything
    /// unrecognised reads as "ps3", so an older or hand-edited file still opens on the console.
    /// </summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = Ps3TargetName;

    public const string Ps3TargetName = "ps3";

    public const string Rpcs3TargetName = "rpcs3";

    [JsonIgnore]
    public bool Rpcs3Target
    {
        get => string.Equals(Target, Rpcs3TargetName, StringComparison.OrdinalIgnoreCase);
        set => Target = value ? Rpcs3TargetName : Ps3TargetName;
    }

    /// <summary>The port RPCS3's IPC server listens on, which qwark-rpcs3.exe is pointed at.</summary>
    [JsonPropertyName("rpcs3PinePort")]
    public int Rpcs3PinePort { get; set; } = Rpcs3Host.DefaultPinePort;

    /// <summary>
    /// An explicit qwark-rpcs3.exe, for a layout the search does not cover. Empty is the normal
    /// case: the helper ships beside this executable.
    /// </summary>
    [JsonPropertyName("rpcs3QwarkPath")]
    public string Rpcs3QwarkPath { get; set; } = string.Empty;

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

    public const float DefaultTableRefreshSeconds = 1f;

    public const float MaxTableRefreshSeconds = 10f;

    private float _tableRefreshSeconds = DefaultTableRefreshSeconds;

    /// <summary>
    /// How often the tables that watch live game state (Unlocks, Level flags) re-read themselves,
    /// in seconds. Zero means never: the panels' Refresh buttons are then the only thing that
    /// reads. Clamped to 0..<see cref="MaxTableRefreshSeconds"/> on the way in, because the file is
    /// hand-editable and a negative or infinite period would be a re-read every frame.
    /// </summary>
    [JsonPropertyName("tableRefreshSeconds")]
    public float TableRefreshSeconds
    {
        get => _tableRefreshSeconds;
        set => _tableRefreshSeconds = float.IsFinite(value)
            ? Math.Clamp(value, 0f, MaxTableRefreshSeconds)
            : DefaultTableRefreshSeconds;
    }

    /// <summary>
    /// True while those tables read themselves on a timer, which is also what decides whether the
    /// Unlocks and Level flags panels carry a Refresh button: with an interval set there is
    /// nothing to press, and with zero the button is the only thing that reads.
    /// </summary>
    [JsonIgnore]
    public bool AutoRefreshesTables => TableRefreshSeconds > 0f;

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

    /// <summary>
    /// The pad's scale, when there was a slider for it. Nothing reads it now: the embedded pad is
    /// drawn at the skin's own size, or smaller when the panel is, and the pad's own window sizes
    /// the skin to itself. The key is still read and written so an older file still loads.
    /// </summary>
    [Obsolete("The pad is sized by the panel or by its own window; nothing reads this.")]
    [JsonPropertyName("inputScale")]
    public float InputScale { get; set; } = 1f;

    /// <summary>
    /// The pad floated in a plain ImGui window inside the main window. That mode is gone: the pad's
    /// own OS window does everything it did and can be captured on its own. The key is still read,
    /// so a file that asks for it opens the pad window instead of nothing, and it is cleared the
    /// first time the mode is set.
    /// </summary>
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
    /// The two ways the pad can be shown, over the flags a settings file may hold. A file that asks
    /// for the old floating pad opens the pad's own window, so nobody loses their pad on an upgrade.
    /// </summary>
    [JsonIgnore]
    public InputDisplayMode InputMode
    {
        get => InputWindowed || InputFloating ? InputDisplayMode.Window : InputDisplayMode.Panel;
        set
        {
            InputWindowed = value == InputDisplayMode.Window;
            InputFloating = false;
        }
    }

    /// <summary>Set once the first-run "allow through the firewall" offer has been shown, so it never nags again.</summary>
    [JsonPropertyName("firewallOffered")]
    public bool FirewallOffered { get; set; }

    /// <summary>
    /// The autosplitter: whether it drives LiveSplit at all, where LiveSplit's server is, and what
    /// each game's run events should do. qwark reports the events; every choice here is the PC's.
    /// </summary>
    [JsonPropertyName("autosplit")]
    public AutosplitSettings Autosplit { get; set; } = new();

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
                    loaded.Autosplit.MigrateAll();
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

/// <summary>
/// What one game's run events should do. Keyed by the event's DESCRIBE label rather than its
/// reason code, because a label is what the user ticked and a code is a number qwark may reuse.
/// A label the file has never seen takes the default the console's descriptor carries, and a
/// label this build no longer knows is kept in the file rather than dropped on the next save.
/// </summary>
public sealed class AutosplitGameSettings
{
    [JsonPropertyName("events")]
    public Dictionary<string, bool> Events { get; set; } = new();

    /// <summary>Only split on a planet when its name matches the split the route expects next.</summary>
    [JsonPropertyName("planetRoute")]
    public bool PlanetRoute { get; set; }

    /// <summary>START events start the timer.</summary>
    [JsonPropertyName("start")]
    public bool Start { get; set; } = true;

    /// <summary>SPLIT events split, whatever their per-event checkboxes say.</summary>
    [JsonPropertyName("split")]
    public bool Split { get; set; } = true;

    /// <summary>
    /// RESET events reset the timer. Off is the All Exterminator Cards case, where a death is not
    /// the end of the run; it is what the old scripts' AEC setting did.
    /// </summary>
    [JsonPropertyName("reset")]
    public bool Reset { get; set; } = true;

    /// <summary>
    /// Read from files this client wrote before the three masters existed, then dropped: "never
    /// reset" is the Reset master turned off. Never written back.
    /// </summary>
    [JsonPropertyName("neverReset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NeverReset { get; set; }

    /// <summary>
    /// Read from older files and dropped. The split names are the planets of the run and the
    /// upcoming one is where you are going: there was never a second way round, and offering one
    /// only ever produced a route that silently never matched.
    /// </summary>
    [JsonPropertyName("namesAreDestination")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NamesAreDestination { get; set; }

    /// <summary>
    /// Folds whatever an older file said into the settings this build has. Idempotent, and called
    /// on the way out of <see cref="AutosplitSettings.For"/>, so every path into a game's entry —
    /// a loaded file, a hand-written one, a deserialized fragment — is migrated exactly once.
    /// </summary>
    public void Migrate()
    {
        if (NeverReset is true) Reset = false;
        NeverReset = null;
        NamesAreDestination = null;
    }

    /// <summary>Whether this event is on, falling back to the console's own default for the label.</summary>
    public bool EventEnabled(string label, bool byDefault)
    {
        foreach (var (key, value) in Events)
        {
            if (string.Equals(key, label, StringComparison.OrdinalIgnoreCase)) return value;
        }

        return byDefault;
    }

    /// <summary>Records a choice for one label, replacing whatever spelling of it the file had.</summary>
    public void SetEvent(string label, bool enabled)
    {
        foreach (var key in Events.Keys)
        {
            if (!string.Equals(key, label, StringComparison.OrdinalIgnoreCase) || key == label) continue;
            Events.Remove(key);
            break;
        }

        Events[label] = enabled;
    }
}

/// <summary>
/// Where LiveSplit is and what to tell it. The per-game entries are keyed by game ("rac1".."rac4")
/// rather than by title id, because BCES01503 hosts three games under one title id.
/// </summary>
public sealed class AutosplitSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("host")]
    public string Host { get; set; } = LiveSplitClient.DefaultHost;

    [JsonPropertyName("port")]
    public int Port { get; set; } = LiveSplitClient.DefaultPort;

    /// <summary>
    /// A <c>.lss</c> the user picked, which overrides discovery entirely. Empty is the normal case:
    /// the run is found in LiveSplit's own recent-splits list and confirmed against what its server
    /// says the current split is.
    /// </summary>
    [JsonPropertyName("splitsFile")]
    public string SplitsFile { get; set; } = string.Empty;

    /// <summary>
    /// Where LiveSplit is installed, for the case where its folder cannot be found from the running
    /// process (LiveSplit closed, or a platform with no process list). Empty means "ask the running
    /// LiveSplit", which is what works without anyone configuring anything.
    /// </summary>
    [JsonPropertyName("liveSplitFolder")]
    public string LiveSplitFolder { get; set; } = string.Empty;

    [JsonPropertyName("games")]
    public Dictionary<string, AutosplitGameSettings> Games { get; set; } = new();

    /// <summary>The key a game is stored under: the lower-case enum name, "rac1".."rac4".</summary>
    public static string KeyFor(GameId game) => game.ToString().ToLowerInvariant();

    /// <summary>
    /// One game's settings, created on first use so the panel can bind straight to it. Matching is
    /// case-insensitive because the file is hand-editable, but deserialization hands back a plain
    /// dictionary with its own comparer, so the scan does the work.
    /// </summary>
    public AutosplitGameSettings For(GameId game)
    {
        string key = KeyFor(game);
        foreach (var (existing, settings) in Games)
        {
            if (!string.Equals(existing, key, StringComparison.OrdinalIgnoreCase)) continue;

            settings.Migrate();
            return settings;
        }

        var created = new AutosplitGameSettings();
        Games[key] = created;
        return created;
    }

    /// <summary>Migrates every game's entry, so a file is brought forward even if nothing reads it.</summary>
    public void MigrateAll()
    {
        foreach (var settings in Games.Values) settings.Migrate();
    }
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

/// <summary>
/// One named set of COLOR feature values: the feature's exact DESCRIBE label against its colour as
/// a six-digit "RRGGBB" string, because a preset file is something people hand-edit.
/// </summary>
public sealed class ColourPreset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("colours")]
    public Dictionary<string, string> Colours { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The colour saved for one feature label, as 0xRRGGBB. The lookup is a scan rather than a
    /// dictionary hit because deserialization replaces <see cref="Colours"/> with a plain
    /// dictionary of its own, comparer and all, and a hand-typed label should still match.
    /// </summary>
    public bool TryGetColour(string label, out uint rgb)
    {
        foreach (var (key, text) in Colours)
        {
            if (string.Equals(key, label, StringComparison.OrdinalIgnoreCase)
                && ColourPresetStore.TryParseColour(text, out rgb))
            {
                return true;
            }
        }

        rgb = 0;
        return false;
    }
}

/// <summary>
/// Named colour presets, one JSON file per game under <c>colours/</c>. Keyed by game rather than by
/// title id on purpose: the BCES01503 disc hosts RaC1 to RaC3, and its RaC2 chargeboots are the
/// same chargeboots as the NPEA release's, so both see the same presets.
/// <para>
/// A missing or broken file reads as "no presets"; a save or a delete throws its IO error, because
/// a write that quietly did nothing is worse than a message.
/// </para>
/// </summary>
public sealed class ColourPresetStore
{
    /// <summary>The RaC2 and RaC3 chargeboot COLOR labels, as qwark's DESCRIBE spells them.</summary>
    public const string ChargebootFront = "Chargeboots primary front";

    public const string ChargebootBack = "Chargeboots primary back";

    public const string ChargebootTint = "Chargeboots tint";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public ColourPresetStore(string folder)
    {
        Folder = folder;
    }

    public string Folder { get; }

    /// <summary>The file's stem: the GameId's own name in lower case, "rac1" to "rac4".</summary>
    public static string KeyFor(GameId game) => game.ToString().ToLowerInvariant();

    public string FileFor(GameId game) => System.IO.Path.Combine(Folder, KeyFor(game) + ".json");

    public List<ColourPreset> List(GameId game) => List(game, out _);

    /// <summary>Every preset for a game, by name. <paramref name="problem"/> says why an existing file was ignored.</summary>
    public List<ColourPreset> List(GameId game, out string? problem)
    {
        problem = null;
        var path = FileFor(game);
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<List<ColourPreset>>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    var kept = loaded.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();
                    Sort(kept);
                    return kept;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            problem = ex.Message;
        }

        return new List<ColourPreset>();
    }

    public ColourPreset? Load(GameId game, string name) =>
        List(game).FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes one preset, replacing any that already had that name (names differ by more than case).</summary>
    public void Save(GameId game, string name, IEnumerable<KeyValuePair<string, uint>> colours)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) throw new ArgumentException("A colour preset needs a name", nameof(name));

        var presets = List(game);
        presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        var preset = new ColourPreset { Name = name };
        foreach (var (label, rgb) in colours) preset.Colours[label] = FormatColour(rgb);
        presets.Add(preset);

        Write(game, presets);
    }

    /// <summary>Drops one preset. False when the game had no preset by that name.</summary>
    public bool Delete(GameId game, string name)
    {
        var presets = List(game);
        if (presets.RemoveAll(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return false;
        }

        Write(game, presets);
        return true;
    }

    private void Write(GameId game, List<ColourPreset> presets)
    {
        Sort(presets);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FileFor(game), JsonSerializer.Serialize(presets, SerializerOptions));
    }

    private static void Sort(List<ColourPreset> presets) =>
        presets.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

    public static string FormatColour(uint rgb) => (rgb & 0xFFFFFFu).ToString("X6", CultureInfo.InvariantCulture);

    /// <summary>"RRGGBB", with a leading "#" or "0x" tolerated because the file is hand-editable.</summary>
    public static bool TryParseColour(string? text, out uint rgb)
    {
        rgb = 0;
        var value = (text ?? string.Empty).Trim();
        if (value.StartsWith('#')) value = value[1..];
        else if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];

        if (value.Length == 0 || value.Length > 6) return false;
        if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed)) return false;

        rgb = parsed & 0xFFFFFFu;
        return true;
    }
}
