using Sendspin.Core.Platform;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for the family extracted from a desktop's Pango-style font setting.
/// </summary>
public sealed class DesktopFontNameTests
{
    [Theory]
    [InlineData("Noto Sans  10", "Noto Sans")] // the KDE portal backend's double space
    [InlineData("Cantarell 11", "Cantarell")]
    [InlineData("Ubuntu Bold 11", "Ubuntu")]
    [InlineData("Noto Sans Semi-Bold Italic 10.5", "Noto Sans")]
    [InlineData("Inter 12px", "Inter")]
    [InlineData("Segoe UI, Noto Sans 10", "Segoe UI")]
    [InlineData("  Fira Sans   ", "Fira Sans")]
    [InlineData("Noto Sans", "Noto Sans")]
    public void ParseFamily_KeepsTheFamilyOnly(string description, string expected) =>
        Assert.Equal(expected, DesktopFontName.ParseFamily(description));

    /// <remarks>
    /// The same ambiguity Pango has: a family whose name ends in a style word loses it. "Roboto"
    /// is still a face fontconfig knows, where "Roboto Condensed Light" as a family name is not.
    /// </remarks>
    [Fact]
    public void ParseFamily_StripsTrailingStyleWordsEvenWhenTheyArePartOfAName() =>
        Assert.Equal("Roboto", DesktopFontName.ParseFamily("Roboto Condensed Light 10"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("10")]
    [InlineData("Bold 10")]
    public void ParseFamily_ReturnsNullWhenNoFamilyIsLeft(string? description) =>
        Assert.Null(DesktopFontName.ParseFamily(description));
}
