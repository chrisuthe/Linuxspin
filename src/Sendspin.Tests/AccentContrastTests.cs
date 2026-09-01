using Sendspin.Core.Theme;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for the glyph colour chosen over the system accent.
/// </summary>
/// <remarks>
/// The colours are the ones measured in the "Theme and accent on Linux" section of
/// <c>docs/ARCHITECTURE.md</c>, plus Fluent's built-in fallback: the point of the helper is that
/// both kinds of accent — a saturated pick and a light highlight — get a readable glyph.
/// </remarks>
public sealed class AccentContrastTests
{
    [Theory]
    [InlineData(0x00, 0x78, 0xD7)] // Fluent's fallback and Windows' default blue
    [InlineData(0xA9, 0x4C, 0x31)] // Plasma, BreezeDark highlight tinted by a coral accent
    [InlineData(0x35, 0x84, 0xE4)] // GNOME's blue accent
    public void PrefersBlackText_IsFalseOverADarkOrSaturatedAccent(byte r, byte g, byte b) =>
        Assert.False(AccentContrast.PrefersBlackText(r, g, b));

    [Theory]
    [InlineData(0x3D, 0xAE, 0xE9)] // Plasma, BreezeLight highlight
    [InlineData(0xEF, 0x92, 0x77)] // Plasma, BreezeLight highlight tinted by a coral accent
    [InlineData(0x8F, 0xBC, 0xBB)] // Plasma, Nordic's selection colour
    public void PrefersBlackText_IsTrueOverALightAccent(byte r, byte g, byte b) =>
        Assert.True(AccentContrast.PrefersBlackText(r, g, b));

    [Theory]
    [InlineData(0x00, 0x00, 0x00, 0.0)]
    [InlineData(0xFF, 0xFF, 0xFF, 1.0)]
    [InlineData(0x80, 0x80, 0x80, 0.2159)]
    [InlineData(0x00, 0x78, 0xD7, 0.1834)]
    public void RelativeLuminance_MatchesTheWcagFormula(byte r, byte g, byte b, double expected) =>
        Assert.Equal(expected, AccentContrast.RelativeLuminance(r, g, b), precision: 4);
}
