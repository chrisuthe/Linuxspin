using Sendspin.Core.Platform;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for the native-Wayland opt-in predicate.
/// </summary>
/// <remarks>
/// Worth a test precisely because it moved out of a per-TFM file: those cannot be reached from a
/// cross-platform test project, so logic there is logic nothing checks.
/// </remarks>
public sealed class WaylandOptInTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("yes")]
    public void IsTruthy_AcceptsTheSpellingsAUserWouldType(string value) =>
        Assert.True(WaylandOptIn.IsTruthy(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("maybe")]
    public void IsTruthy_RejectsEverythingElse(string? value) =>
        Assert.False(WaylandOptIn.IsTruthy(value));

    [Fact]
    public void VariableName_IsTheOneDocumented() =>
        Assert.Equal("SENDSPIN_WAYLAND", WaylandOptIn.VariableName);
}
