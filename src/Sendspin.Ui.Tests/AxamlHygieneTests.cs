using System.Text.RegularExpressions;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins two rules the reskin rests on: no axaml in the app names a colour, and no view draws a
/// glyph with a character.
/// </summary>
/// <remarks>
/// Both are grep-shaped rules, and both are here rather than in a script because a script is
/// not run by CI and a test is. The source tree is found from the test binary's location, which
/// is inside the repository for every way the suite is run.
/// </remarks>
public sealed partial class AxamlHygieneTests
{
    private static readonly string[] BrushProperties =
        ["Background", "Foreground", "BorderBrush", "Color", "Fill", "Stroke", "SelectionBrush", "CaretBrush"];

    [Fact]
    public void NoAxaml_NamesAColour()
    {
        var offenders = new List<string>();

        foreach (var file in PlayerAxamlFiles())
        {
            var text = StripComments(File.ReadAllText(file));
            var name = Path.GetFileName(file);

            foreach (Match match in HexColour().Matches(text))
            {
                offenders.Add($"{name}: {match.Value}");
            }

            foreach (Match match in NamedColourAttribute().Matches(text))
            {
                if (BrushProperties.Contains(match.Groups["property"].Value))
                {
                    offenders.Add($"{name}: {match.Value}");
                }
            }

            foreach (Match match in NamedColourSetter().Matches(text))
            {
                if (BrushProperties.Contains(match.Groups["property"].Value))
                {
                    offenders.Add($"{name}: {match.Value}");
                }
            }

            foreach (Match match in StaticColourClass().Matches(text))
            {
                offenders.Add($"{name}: {match.Value}");
            }
        }

        Assert.True(offenders.Count == 0, "Colour literals in axaml:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void NoView_DrawsAGlyphWithACharacter()
    {
        var offenders = new List<string>();

        foreach (var file in PlayerSourceFiles())
        {
            foreach (Match match in GlyphCharacter().Matches(File.ReadAllText(file)))
            {
                offenders.Add($"{Path.GetFileName(file)}: U+{char.ConvertToUtf32(match.Value, 0):X4}");
            }
        }

        Assert.True(offenders.Count == 0, "Glyph characters in the app:\n" + string.Join('\n', offenders));
    }

    private static IEnumerable<string> PlayerAxamlFiles() =>
        Directory.EnumerateFiles(PlayerDirectory(), "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static IEnumerable<string> PlayerSourceFiles() =>
        Directory.EnumerateFiles(PlayerDirectory(), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    /// <summary>The app project directory, found by walking up from the test binary to the solution.</summary>
    private static string PlayerDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sendspin.Player.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Source tree not found: the test binary is running outside the repository.");

        return Path.Combine(directory!.FullName, "src", "Sendspin.Player");
    }

    private static string StripComments(string xaml) => XmlComment().Replace(xaml, string.Empty);

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();

    /// <summary>A quoted <c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</summary>
    [GeneratedRegex(@"""#(?:[0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})""")]
    private static partial Regex HexColour();

    /// <summary>A brush property given a bare word rather than a markup extension.</summary>
    [GeneratedRegex(@"\b(?<property>[A-Za-z]+)=""(?<value>[A-Za-z]+)""")]
    private static partial Regex NamedColourAttribute();

    /// <summary>A setter for a brush property given a bare word rather than a markup extension.</summary>
    [GeneratedRegex(@"Property=""(?<property>[A-Za-z]+)""\s+Value=""(?<value>[A-Za-z]+)""")]
    private static partial Regex NamedColourSetter();

    /// <summary>A colour pulled from the <c>Brushes</c> or <c>Colors</c> statics.</summary>
    [GeneratedRegex(@"\{x:Static\s+(?:Brushes|Colors)\.")]
    private static partial Regex StaticColourClass();

    /// <summary>The characters the views used to draw with, plus the play and pause symbols.</summary>
    [GeneratedRegex(@"[♪⏮⏭▶⏸]|🔀|🔁|🔇")]
    private static partial Regex GlyphCharacter();
}
