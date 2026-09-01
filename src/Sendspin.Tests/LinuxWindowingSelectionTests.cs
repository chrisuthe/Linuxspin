using Sendspin.Core.Platform;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for the Linux windowing-backend choice.
/// </summary>
/// <remarks>
/// The predicate these replaced pinned only the spelling of an environment variable, and passed
/// green through a release in which setting that variable aborted the app on startup. So these
/// assert the choice itself, one row per environment a user can actually be in.
/// </remarks>
public sealed class LinuxWindowingSelectionTests
{
    [Theory]
    [InlineData("wayland-0")]
    [InlineData("wayland-1")]
    public void Select_DefaultsToWaylandUnderASession(string display) =>
        Assert.Equal(
            LinuxWindowingBackend.Wayland,
            LinuxWindowingSelection.Select(x11Request: null, waylandRequest: null, waylandDisplay: display));

    /// <remarks>
    /// The regression that matters most: an X11-only desktop, VNC, a forwarded <c>DISPLAY</c> or a
    /// CI container must not be handed a backend with no compositor to connect to.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Select_FallsBackToX11WithNoSession(string? display) =>
        Assert.Equal(
            LinuxWindowingBackend.X11,
            LinuxWindowingSelection.Select(x11Request: null, waylandRequest: null, waylandDisplay: display));

    [Fact]
    public void Select_HonoursTheX11EscapeHatchUnderASession() =>
        Assert.Equal(
            LinuxWindowingBackend.X11,
            LinuxWindowingSelection.Select(x11Request: "1", waylandRequest: null, waylandDisplay: "wayland-0"));

    /// <remarks>
    /// The escape hatch is the thing that makes defaulting to Wayland defensible, so it has to win
    /// even against someone's stale <c>SENDSPIN_WAYLAND</c>.
    /// </remarks>
    [Fact]
    public void Select_PrefersTheEscapeHatchOverAnExplicitWaylandRequest() =>
        Assert.Equal(
            LinuxWindowingBackend.X11,
            LinuxWindowingSelection.Select(x11Request: "1", waylandRequest: "1", waylandDisplay: "wayland-0"));

    [Fact]
    public void Select_ForcesWaylandPastAnUndetectedSession() =>
        Assert.Equal(
            LinuxWindowingBackend.Wayland,
            LinuxWindowingSelection.Select(x11Request: null, waylandRequest: "1", waylandDisplay: null));

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("")]
    public void Select_IgnoresAnEscapeHatchTurnedOff(string x11Request) =>
        Assert.Equal(
            LinuxWindowingBackend.Wayland,
            LinuxWindowingSelection.Select(x11Request, waylandRequest: null, waylandDisplay: "wayland-0"));

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("yes")]
    public void IsTruthy_AcceptsTheSpellingsAUserWouldType(string value) =>
        Assert.True(LinuxWindowingSelection.IsTruthy(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("maybe")]
    public void IsTruthy_RejectsEverythingElse(string? value) =>
        Assert.False(LinuxWindowingSelection.IsTruthy(value));

    [Fact]
    public void VariableNames_AreTheOnesDocumented()
    {
        Assert.Equal("SENDSPIN_X11", LinuxWindowingSelection.X11VariableName);
        Assert.Equal("SENDSPIN_WAYLAND", LinuxWindowingSelection.WaylandVariableName);
        Assert.Equal("WAYLAND_DISPLAY", LinuxWindowingSelection.SessionVariableName);
    }
}
