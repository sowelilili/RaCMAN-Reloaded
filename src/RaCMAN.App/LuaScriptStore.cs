using System.Text;

namespace RaCMAN.App;

/// <summary>
/// The Lua console's scripts on disk (2026-10-31): plain <c>.lua</c> files, UTF-8 without a byte
/// order mark, in a folder of the data folder (<see cref="AppPaths.LuaScripts"/>) unless the user
/// opens one from elsewhere. A script is saved by name into that folder; any <c>.lua</c> file can
/// be read. Line endings are read as they come and kept as LF in the editor, and written as the
/// platform's own, so the file opens cleanly in Notepad as well as anywhere else.
/// </summary>
public sealed class LuaScriptStore
{
    public const string Extension = ".lua";

    public LuaScriptStore(string folder)
    {
        Folder = folder;
    }

    public string Folder { get; }

    /// <summary>
    /// The name a script is saved under, made safe for a file name: trimmed, a trailing ".lua"
    /// dropped (it is added back), and every character no file system takes replaced with '_'.
    /// Empty for a name with nothing left in it.
    /// </summary>
    public static string CleanName(string? name)
    {
        string text = (name ?? string.Empty).Trim();
        if (text.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) text = text[..^Extension.Length].TrimEnd();

        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var builder = new StringBuilder(text.Length);
        foreach (char c in text) builder.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);

        // "." and ".." name folders, and a name of dots alone is nothing Windows keeps.
        string cleaned = builder.ToString().Trim();
        return cleaned.Trim('.').Length == 0 ? string.Empty : cleaned;
    }

    /// <summary>The file a name is saved to. Throws <see cref="ArgumentException"/> for a name with nothing usable in it.</summary>
    public string FileFor(string name)
    {
        string cleaned = CleanName(name);
        if (cleaned.Length == 0) throw new ArgumentException("give the script a name", nameof(name));
        return Path.Combine(Folder, cleaned + Extension);
    }

    /// <summary>The names of the scripts in the folder, without ".lua", in alphabetical order; none when it does not exist.</summary>
    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(Folder)) return Array.Empty<string>();

        return Directory.EnumerateFiles(Folder, "*" + Extension)
            .Where(path => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Saves the text under a name in the folder, making the folder first; answers the file's path.</summary>
    public string Save(string name, string text)
    {
        string path = FileFor(name);
        Directory.CreateDirectory(Folder);
        Write(path, text);
        return path;
    }

    /// <summary>A script saved under a name in the folder.</summary>
    public string Load(string name) => Read(FileFor(name));

    /// <summary>Any script file: its text with LF line endings and no byte order mark.</summary>
    public static string Read(string path) => Normalise(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>Writes a script to any path, with the platform's line endings and no byte order mark.</summary>
    public static void Write(string path, string text)
    {
        string body = Normalise(text);
        if (Environment.NewLine != "\n") body = body.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        File.WriteAllText(path, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>LF line endings, as the editor keeps them, and no stray byte order mark.</summary>
    public static string Normalise(string text) =>
        (text ?? string.Empty).TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
