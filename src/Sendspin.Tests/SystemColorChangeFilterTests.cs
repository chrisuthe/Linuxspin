using Sendspin.Core.Theme;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for the filter in front of the platform's colour-changed event.
/// </summary>
public sealed class SystemColorChangeFilterTests
{
    private static readonly SystemColors WindowsBlueLight = new(0xFF0076E4, IsDark: false);

    [Fact]
    public void Accept_TakesTheFirstReport() =>
        Assert.True(new SystemColorChangeFilter().Accept(WindowsBlueLight));

    /// <remarks>
    /// The measured Windows storm: about twenty reports for one change.
    /// </remarks>
    [Fact]
    public void Accept_TakesOneOfTwentyIdenticalReports()
    {
        var filter = new SystemColorChangeFilter();

        var accepted = Enumerable.Range(0, 20).Count(_ => filter.Accept(WindowsBlueLight));

        Assert.Equal(1, accepted);
    }

    [Fact]
    public void Accept_TakesAnAccentChange()
    {
        var filter = new SystemColorChangeFilter();
        filter.Accept(WindowsBlueLight);

        Assert.True(filter.Accept(WindowsBlueLight with { AccentArgb = 0xFF3DAEE9 }));
    }

    [Fact]
    public void Accept_TakesAVariantFlipWithTheSameAccent()
    {
        var filter = new SystemColorChangeFilter();
        filter.Accept(WindowsBlueLight);

        Assert.True(filter.Accept(WindowsBlueLight with { IsDark = true }));
        Assert.False(filter.Accept(WindowsBlueLight with { IsDark = true }));
    }

    [Fact]
    public void Accept_TakesAReturnToAnEarlierValue()
    {
        var filter = new SystemColorChangeFilter();
        filter.Accept(WindowsBlueLight);
        filter.Accept(WindowsBlueLight with { IsDark = true });

        Assert.True(filter.Accept(WindowsBlueLight));
    }
}
