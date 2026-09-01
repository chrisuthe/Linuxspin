using Sendspin.Core.Threading;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for the gate that keeps a timer's ticks from piling up behind a busy UI thread.
/// </summary>
public sealed class TickGateTests
{
    [Fact]
    public void TryArm_AdmitsTheFirstTick()
    {
        var gate = new TickGate();

        Assert.True(gate.TryArm());
        Assert.True(gate.IsArmed);
        Assert.Equal(0, gate.Dropped);
    }

    [Fact]
    public void TryArm_DropsATickWhileOneIsStillWaiting()
    {
        var gate = new TickGate();
        gate.TryArm();

        Assert.False(gate.TryArm());
        Assert.False(gate.TryArm());
        Assert.Equal(2, gate.Dropped);
    }

    [Fact]
    public void TryArm_AdmitsAgainOnceTheWaitingTickHasRun()
    {
        var gate = new TickGate();
        gate.TryArm();
        gate.Disarm();

        Assert.True(gate.TryArm());
        Assert.Equal(0, gate.Dropped);
    }

    /// <remarks>
    /// The shape that matters: a UI thread stalls for many periods, and when it frees up there is
    /// exactly one tick waiting for it, not one per period missed.
    /// </remarks>
    [Fact]
    public void TryArm_LeavesOneTickWaitingHoweverManyArrive()
    {
        var gate = new TickGate();

        var admitted = Enumerable.Range(0, 50).Count(_ => gate.TryArm());

        Assert.Equal(1, admitted);
        Assert.Equal(49, gate.Dropped);
    }

    [Fact]
    public void TryArm_IsSafeFromManyThreadsAtOnce()
    {
        var gate = new TickGate();
        var admitted = 0;

        Parallel.For(0, 1000, _ =>
        {
            if (gate.TryArm())
            {
                Interlocked.Increment(ref admitted);
            }
        });

        Assert.Equal(1, admitted);
        Assert.Equal(999, gate.Dropped);
    }
}
