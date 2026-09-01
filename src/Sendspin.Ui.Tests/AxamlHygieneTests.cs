using System.Text.RegularExpressions;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins two rules the reskin rests on: no axaml in the app names a colour, and no view draws a
/// glyph with a character.
/// </summary>
/// <remarks>
/// Both are grep-shaped rules, and both are here rather than in a script because a script is
/// not run by CI and a test is (see <see cref="PlayerSource"/>).
/// </remarks>
public sealed partial class AxamlHygieneTests
{
    private static readonly string[] BrushProperties =
        ["Background", "Foreground", "BorderBrush", "Color", "Fill", "Stroke", "SelectionBrush", "CaretBrush"];

    [Fact]
    public void NoAxaml_NamesAColour()
    {
        var offenders = new List<string>();

        foreach (var file in PlayerSource.AxamlFiles())
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

        foreach (var file in PlayerSource.SourceFiles())
        {
            foreach (Match match in GlyphCharacter().Matches(File.ReadAllText(file)))
            {
                offenders.Add($"{Path.GetFileName(file)}: U+{char.ConvertToUtf32(match.Value, 0):X4}");
            }
        }

        Assert.True(offenders.Count == 0, "Glyph characters in the app:\n" + string.Join('\n', offenders));
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
