using System.Text;

namespace RaCMAN.App;

/// <summary>
/// Whole-file text writes that leave either the old file or the new one on disk, never a cut
/// one: the text goes to a temporary file beside the target, which is then moved over it.
/// </summary>
internal static class AtomicFile
{
    /// <summary>What the half-written file is called while a write runs.</summary>
    public const string TemporarySuffix = ".racman-tmp";

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> as UTF-8 without a byte-order
    /// mark, creating the folder if need be. Throws what <see cref="File.WriteAllText(string, string?)"/>
    /// would, with the old file still whole.
    /// </summary>
    public static void WriteAllText(string path, string text)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);

        string temporary = path + TemporarySuffix;
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // The old file is still whole; only the half-made one is left to clear away.
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing more to be done about it; the error that got here is the one to report.
            }

            throw;
        }
    }
}
