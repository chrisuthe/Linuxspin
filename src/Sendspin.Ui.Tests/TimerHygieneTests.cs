using System.Text.RegularExpressions;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins two rules the shell rests on: the app has one timer, and the main window reads nothing
/// at <c>Opened</c> that can still be the fallback then.
/// </summary>
/// <remarks>
/// Grep-shaped, like <see cref="AxamlHygieneTests"/>, and here for the same reason. The
/// measurements behind both rules are in the "UI shell" section of <c>docs/ARCHITECTURE.md</c>.
/// </remarks>
public sealed partial class TimerHygieneTests
{
    /// <summary>The one file allowed to name the type: the helper that replaces it.</summary>
    private static readonly string ClockFile = Path.Combine("Threading", "UiClock.cs");

    /// <remarks>
    /// The identifier anywhere, comments included, not just <c>new DispatcherTimer</c>: a type
    /// alias or a factory would slip past the narrower rule, and a comment recommending one is
    /// the start of the same regression.
    /// </remarks>
    [Fact]
    public void NoFile_NamesDispatcherTimer_ExceptTheClock()
    {
        var offenders = PlayerSource.CSharpFiles()
            .Where(f => !f.EndsWith(ClockFile, StringComparison.Ordinal))
            .Where(f => DispatcherTimer().IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, "DispatcherTimer outside UiClock.cs:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void TheClock_IsWhereTheRuleSaysItIs() =>
        Assert.True(File.Exists(Path.Combine(PlayerSource.Directory(), ClockFile)));

    /// <remarks>
    /// On the Wayland head both are still the fallback in <c>OnOpened</c> — the variant until the
    /// portal answers, the scaling until the first configure. The code-behind names neither
    /// anywhere, which is simpler to pin than "not inside that one method" and no less true.
    /// </remarks>
    [Fact]
    public void TheMainWindow_ReadsNeitherThemeVariantNorRenderScaling()
    {
        var text = File.ReadAllText(Path.Combine(PlayerSource.Directory(), "Views", "MainWindow.axaml.cs"));

        Assert.DoesNotMatch(@"\bActualThemeVariant\b", text);
        Assert.DoesNotMatch(@"\bRenderScaling\b", text);
    }

    [GeneratedRegex(@"\bDispatcherTimer\b")]
    private static partial Regex DispatcherTimer();
}
