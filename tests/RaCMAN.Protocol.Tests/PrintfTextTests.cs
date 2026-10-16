using System.Text.RegularExpressions;
using Xunit;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// ImGui.NET passes the string of Text, TextColored, TextWrapped, TextDisabled, BulletText,
/// LabelText and SetTooltip to the native side as a printf format, so a per cent sign in a save's
/// name, a mod's description or a label the console sent is read as a conversion: "Any% (co-op)"
/// loses a piece and a "%s" reads a pointer nobody passed. The App draws text through the helpers
/// in <c>Ui</c> instead, which hand ImGui the string as it is. This reads the App's own source and
/// holds every file to that, so a call that slips back in fails here rather than on somebody's
/// "Any%" save.
/// </summary>
public class PrintfTextTests
{
    /// <summary>The calls whose string argument is a printf format.</summary>
    private static readonly Regex PrintfCall = new(
        @"\bImGui\.(Text|TextColored|TextWrapped|TextDisabled|BulletText|LabelText|SetTooltip|SetItemTooltip|LogText)\s*\(",
        RegexOptions.Compiled);

    private static string AppSourceFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "RaCMAN.App");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src/RaCMAN.App is not above the test's own folder");
    }

    [Fact]
    public void TheAppDrawsNoTextThroughAPrintfFormat()
    {
        string root = AppSourceFolder();
        var offenders = new List<string>();
        int files = 0;

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path);
            if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            files++;
            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                // Comments may name the calls; code may not.
                string line = lines[i].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)) continue;

                if (PrintfCall.IsMatch(line)) offenders.Add($"{relative}:{i + 1}: {line}");
            }
        }

        Assert.True(files > 50, $"only {files} source files found under {root}");
        Assert.True(offenders.Count == 0,
            "Use Ui.Text, Ui.TextWrapped, Ui.TextDisabled, Ui.BulletText, Ui.SetTooltip or ImGui.TextUnformatted:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void ThePatternFindsWhatItIsThereToFind()
    {
        Assert.Matches(PrintfCall, "ImGui.TextColored(Ui.Grey, _status);");
        Assert.Matches(PrintfCall, "if (hovered) ImGui.SetTooltip(text);");
        Assert.Matches(PrintfCall, "ImGui.Text ($\"{name}\");");
        Assert.DoesNotMatch(PrintfCall, "ImGui.TextUnformatted(text);");
        Assert.DoesNotMatch(PrintfCall, "Ui.Text(Ui.Grey, text);");
        Assert.DoesNotMatch(PrintfCall, "Ui.SetTooltip(text);");
    }
}
