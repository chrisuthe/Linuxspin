using Sendspin.Core.Platform;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Pins the macOS system-font rule: the name goes to Avalonia only when it resolves, because an
/// unresolvable <c>DefaultFamilyName</c> kills the process before a window appears.
/// </summary>
public sealed class MacSystemFontTests
{
    [Fact]
    public void Select_ReturnsTheSystemFontNameWhenItResolves()
    {
        string? asked = null;

        var family = MacSystemFont.Select(name =>
        {
            asked = name;
            return true;
        });

        Assert.Equal(".AppleSystemUIFont", family);
        Assert.Equal(MacSystemFont.FamilyName, asked);
    }

    [Fact]
    public void Select_ReturnsNullWhenItDoesNot() =>
        Assert.Null(MacSystemFont.Select(_ => false));

    [Fact]
    public void Select_RequiresAResolver() =>
        Assert.Throws<ArgumentNullException>(() => MacSystemFont.Select(null!));
}
