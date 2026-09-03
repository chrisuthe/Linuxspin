using Sendspin.Core.MediaSession;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for the spec's current-plus-one-pending model that scheduled artwork and metadata share.
/// </summary>
/// <remarks>
/// Times are local microseconds; the tests pick small round numbers. Each rule the spec states is
/// one test, so a reading of <c>roles/artwork/v1.md</c> or <c>roles/metadata/v1.md</c> can be
/// checked off against this file.
/// </remarks>
public sealed class ScheduledValueTests
{
    [Fact]
    public void APastValue_AppliesAtOnce()
    {
        var scheduled = new ScheduledValue<string>();

        var outcome = scheduled.Offer("a", dueLocalMicros: 900, nowLocalMicros: 1_000);

        Assert.Equal(ScheduledOffer.Applied, outcome);
        Assert.Equal("a", scheduled.Current);
        Assert.Equal(900, scheduled.CurrentSince);
        Assert.False(scheduled.HasPending);
        Assert.Null(scheduled.NextDue);
    }

    [Fact]
    public void APresentValue_AppliesAtOnce()
    {
        var scheduled = new ScheduledValue<string>();

        Assert.Equal(ScheduledOffer.Applied, scheduled.Offer("a", dueLocalMicros: 1_000, nowLocalMicros: 1_000));
        Assert.Equal("a", scheduled.Current);
    }

    [Fact]
    public void AFutureValue_WaitsAndLeavesCurrentAlone()
    {
        var scheduled = new ScheduledValue<string>();
        scheduled.Offer("a", 0, 1_000);

        var outcome = scheduled.Offer("b", dueLocalMicros: 4_000, nowLocalMicros: 1_000);

        Assert.Equal(ScheduledOffer.Held, outcome);
        Assert.Equal("a", scheduled.Current);
        Assert.True(scheduled.HasPending);
        Assert.Equal("b", scheduled.Pending);
        Assert.Equal(4_000, scheduled.NextDue);
    }

    [Fact]
    public void ASecondFutureValue_ReplacesTheFirstRatherThanQueueing()
    {
        var scheduled = new ScheduledValue<string>();
        scheduled.Offer("a", 0, 1_000);
        scheduled.Offer("b", 4_000, 1_000);

        scheduled.Offer("c", 6_000, 1_000);

        Assert.Equal("c", scheduled.Pending);
        Assert.Equal(6_000, scheduled.NextDue);

        // Promoting past both times yields "c" once; "b" never shows.
        Assert.True(scheduled.Promote(7_000));
        Assert.Equal("c", scheduled.Current);
        Assert.False(scheduled.Promote(8_000));
    }

    /// <summary>
    /// The spec's way to cancel a scheduled metadata update: re-send the current state with a past or
    /// present timestamp.
    /// </summary>
    [Fact]
    public void APastValueAfterAFutureOne_AppliesAndDiscardsThePendingOne()
    {
        var scheduled = new ScheduledValue<string>();
        scheduled.Offer("a", 0, 1_000);
        scheduled.Offer("b", 4_000, 1_000);

        var outcome = scheduled.Offer("a", dueLocalMicros: 1_000, nowLocalMicros: 1_000);

        Assert.Equal(ScheduledOffer.Applied, outcome);
        Assert.Equal("a", scheduled.Current);
        Assert.False(scheduled.HasPending);
        Assert.False(scheduled.Promote(5_000));
        Assert.Equal("a", scheduled.Current);
    }

    [Fact]
    public void Promote_BeforeTheDueTime_DoesNothing()
    {
        var scheduled = new ScheduledValue<string>();
        scheduled.Offer("a", 0, 1_000);
        scheduled.Offer("b", 4_000, 1_000);

        Assert.False(scheduled.Promote(3_999));
        Assert.Equal("a", scheduled.Current);
        Assert.True(scheduled.HasPending);
    }

    [Fact]
    public void Promote_AtExactlyTheDueTime_Promotes()
    {
        var scheduled = new ScheduledValue<string>();
        scheduled.Offer("a", 0, 1_000);
        scheduled.Offer("b", 4_000, 1_000);

        Assert.True(scheduled.Promote(4_000));
        Assert.Equal("b", scheduled.Current);
        Assert.Equal(4_000, scheduled.CurrentSince);
        Assert.False(scheduled.HasPending);
    }

    /// <summary>Artwork is never dropped for lateness: a promotion that runs late still promotes.</summary>
    [Fact]
    public void Promote_LongAfterTheDueTime_StillPromotes()
    {
        var scheduled = new ScheduledValue<string>();
        scheduled.Offer("b", 4_000, 1_000);

        Assert.True(scheduled.Promote(60_000_000));
        Assert.Equal("b", scheduled.Current);

        // The value took effect at its own time, not when it was noticed.
        Assert.Equal(4_000, scheduled.CurrentSince);
    }

    [Fact]
    public void Cancel_DiscardsThePendingValueAndLeavesCurrentAlone()
    {
        var scheduled = new ScheduledValue<string>();
        scheduled.Offer("a", 0, 1_000);
        scheduled.Offer("b", 4_000, 1_000);

        scheduled.Cancel();

        Assert.Equal("a", scheduled.Current);
        Assert.False(scheduled.HasPending);
        Assert.Null(scheduled.NextDue);
        Assert.False(scheduled.Promote(5_000));
    }

    /// <summary>A clear is an empty value with the same timing, so a future one is scheduled.</summary>
    [Fact]
    public void AFutureClear_IsScheduledLikeAnyOtherValue()
    {
        var scheduled = new ScheduledValue<string>();
        scheduled.Offer("a", 0, 1_000);

        Assert.Equal(ScheduledOffer.Held, scheduled.Offer(null, 4_000, 1_000));
        Assert.Equal("a", scheduled.Current);
        Assert.True(scheduled.HasPending);

        Assert.True(scheduled.Promote(4_000));
        Assert.Null(scheduled.Current);
        Assert.False(scheduled.HasPending);
    }

    [Fact]
    public void Reset_ForgetsBothValues()
    {
        var scheduled = new ScheduledValue<string>();
        scheduled.Offer("a", 0, 1_000);
        scheduled.Offer("b", 4_000, 1_000);

        scheduled.Reset();

        Assert.Null(scheduled.Current);
        Assert.Equal(0, scheduled.CurrentSince);
        Assert.False(scheduled.HasPending);
        Assert.False(scheduled.Promote(5_000));
    }
}
