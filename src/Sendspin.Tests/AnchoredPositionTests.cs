using Sendspin.Core.MediaSession;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests that the projected position advances by measured time, not by however often it is asked.
/// </summary>
/// <remarks>
/// The clock is a plain <see cref="TimeSpan"/> handed in, which is the whole fake: the tests
/// choose the readings, including the uneven ones the Wayland head's timer produces.
/// </remarks>
public sealed class AnchoredPositionTests
{
    private static readonly TimeSpan Anchor = TimeSpan.FromSeconds(30);

    [Fact]
    public void At_ReturnsTheAnchorAtTheMomentItWasTaken()
    {
        var position = new AnchoredPosition();
        position.Anchor(Anchor, TimeSpan.FromSeconds(100));

        Assert.Equal(Anchor, position.At(TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public void At_AdvancesByTheTimeSinceTheAnchor()
    {
        var position = new AnchoredPosition();
        position.Anchor(Anchor, TimeSpan.FromSeconds(100));

        Assert.Equal(Anchor + TimeSpan.FromMilliseconds(1250), position.At(TimeSpan.FromMilliseconds(101_250)));
    }

    /// <remarks>
    /// The measured Wayland pattern: a 500 ms timer landing at 618, 501, 618, 500 ms gaps. Read
    /// at those moments, the position tracks the clock exactly rather than stepping 500 ms per
    /// tick and drifting behind.
    /// </remarks>
    [Fact]
    public void At_IsRightHoweverUnevenlyItIsRead()
    {
        var position = new AnchoredPosition();
        position.Anchor(Anchor, TimeSpan.Zero);

        var now = TimeSpan.Zero;
        foreach (var gapMs in new[] { 618, 501, 618, 500, 619 })
        {
            now += TimeSpan.FromMilliseconds(gapMs);
            Assert.Equal(Anchor + now, position.At(now));
        }

        Assert.Equal(TimeSpan.FromMilliseconds(2856), now);
    }

    [Fact]
    public void Anchor_ReplacesTheProjectionWithTheServersReport()
    {
        var position = new AnchoredPosition();
        position.Anchor(Anchor, TimeSpan.FromSeconds(0));
        position.Anchor(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(7), position.At(TimeSpan.FromSeconds(12)));
    }

    [Fact]
    public void At_NeverRunsBackwardsBeforeTheAnchor()
    {
        var position = new AnchoredPosition();
        position.Anchor(Anchor, TimeSpan.FromSeconds(10));

        Assert.Equal(Anchor, position.At(TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void At_StartsFromZeroWhenNothingWasAnchored()
    {
        var position = new AnchoredPosition();

        Assert.Equal(TimeSpan.FromSeconds(3), position.At(TimeSpan.FromSeconds(3)));
    }
}
