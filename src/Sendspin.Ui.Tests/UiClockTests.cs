using Avalonia.Threading;
using Sendspin.Player.Threading;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Tests that the UI clock ticks through the dispatcher, and that ticks do not pile up behind
/// it.
/// </summary>
/// <remarks>
/// The headless dispatcher runs jobs only when asked (<see cref="Dispatcher.RunJobs"/>), which
/// is exactly a UI thread that is busy: the timer keeps firing on its own thread while nothing
/// drains the queue. The timings are generous so that a loaded machine cannot turn a real pass
/// into a false failure; the assertions are about counts, not intervals.
/// </remarks>
[Collection(HeadlessCollection.Name)]
public sealed class UiClockTests(HeadlessSession headless)
{
    [Fact]
    public void Start_TicksOnTheDispatcher() => headless.Run(() =>
    {
        using var clock = new UiClock(TimeSpan.FromMilliseconds(10));
        var ticks = 0;
        clock.Tick += (_, _) => ticks++;

        clock.Start();
        Assert.True(clock.IsRunning);

        WaitFor(() => ticks >= 3);

        clock.Stop();
        Assert.False(clock.IsRunning);
        Assert.True(ticks >= 3, $"{ticks} ticks");
    });

    [Fact]
    public void Ticks_DoNotPileUpBehindABusyUiThread() => headless.Run(() =>
    {
        using var clock = new UiClock(TimeSpan.FromMilliseconds(5));
        var ticks = 0;
        clock.Tick += (_, _) => ticks++;

        clock.Start();

        // The UI thread is "busy": the timer fires ~60 times and nothing runs the queue.
        Thread.Sleep(300);
        Assert.Equal(0, ticks);
        Assert.True(clock.DroppedTicks >= 10, $"{clock.DroppedTicks} dropped");

        // When it frees up there is exactly one tick waiting, not sixty.
        clock.Stop();
        var droppedAtStop = clock.DroppedTicks;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, ticks);

        // ...and that one did not fire, because the clock had been stopped before it ran.
        Assert.Equal(droppedAtStop, clock.DroppedTicks);
    });

    /// <remarks>
    /// The drain is not instantaneous, so a timer callback can land during it and be admitted as
    /// a second tick; what must not happen is the seven-or-so that fired during the stall.
    /// </remarks>
    [Fact]
    public void Ticks_ResumeOneAtATimeOnceTheThreadIsFree() => headless.Run(() =>
    {
        using var clock = new UiClock(TimeSpan.FromMilliseconds(40));
        var ticks = 0;
        clock.Tick += (_, _) => ticks++;

        clock.Start();
        Thread.Sleep(300);
        Assert.True(clock.DroppedTicks >= 4, $"{clock.DroppedTicks} dropped");

        Dispatcher.UIThread.RunJobs();

        Assert.InRange(ticks, 1, 2);

        clock.Stop();
    });

    [Fact]
    public void Elapsed_RunsFromConstructionRegardlessOfStartAndStop() => headless.Run(() =>
    {
        using var clock = new UiClock(TimeSpan.FromSeconds(1));

        var before = clock.Elapsed;
        Thread.Sleep(20);
        var later = clock.Elapsed;

        Assert.True(later > before);

        clock.Start();
        clock.Stop();

        Assert.True(clock.Elapsed >= later);
    });

    [Fact]
    public void Start_IsIdempotentAndStopIsSafeWhenNotRunning() => headless.Run(() =>
    {
        using var clock = new UiClock(TimeSpan.FromMilliseconds(50));

        clock.Stop();
        clock.Start();
        clock.Start();
        Assert.True(clock.IsRunning);

        clock.Stop();
        clock.Stop();
        Assert.False(clock.IsRunning);
    });

    [Fact]
    public void Dispose_StopsTheClockAndRefusesToRestart() => headless.Run(() =>
    {
        var clock = new UiClock(TimeSpan.FromMilliseconds(50));
        clock.Start();
        clock.Dispose();

        Assert.False(clock.IsRunning);
        Assert.Throws<ObjectDisposedException>(clock.Start);
        clock.Dispose();
    });

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsANonPositivePeriod(int milliseconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiClock(TimeSpan.FromMilliseconds(milliseconds)));

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(15);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
