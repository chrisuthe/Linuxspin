using System.Diagnostics;
using Avalonia.Threading;
using Sendspin.Core.Threading;

namespace Sendspin.Player.Threading;

/// <summary>
/// The one timer the UI uses: a thread-pool timer that posts each tick to the dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a <see cref="DispatcherTimer"/>, and a hygiene test in
/// <c>Sendspin.Ui.Tests</c> keeps it the only file in the app that names one. On the Wayland head
/// <c>Avalonia.Wayland</c> 12.1.1 fires a <see cref="DispatcherTimer"/> on a coarse quantum of
/// 100–140 ms — a 16 ms timer ticks at the quantum, a 500 ms one on time or one quantum late
/// depending on the run — while a <see cref="Timer"/> posting to <see cref="Dispatcher.UIThread"/>
/// runs at 62 Hz on Wayland, X11 and macOS alike. The measurements are in the "UI shell" section
/// of <c>docs/ARCHITECTURE.md</c>; re-check them on every Avalonia bump with
/// <c>dotnet run --project scripts/spike/ShellSpike -- clock</c>.
/// </para>
/// <para>
/// A tick that arrives while the previous one is still queued is dropped, not queued behind it,
/// so a busy UI thread gets one catch-up tick rather than a burst. <see cref="Elapsed"/> is a
/// <see cref="Stopwatch"/> reading, monotonic for the clock's lifetime and independent of
/// <see cref="Start"/> and <see cref="Stop"/>, so a handler that wants real time between ticks
/// reads it on each tick instead of trusting the period.
/// </para>
/// <para>
/// <see cref="Start"/>, <see cref="Stop"/> and <see cref="Tick"/> are UI-thread affairs;
/// <see cref="Dispose"/> may be called from anywhere.
/// </para>
/// </remarks>
public sealed class UiClock : IDisposable
{
    private readonly Timer _timer;
    private readonly TickGate _gate = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private bool _isRunning;
    private bool _isDisposed;

    public UiClock(TimeSpan period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);

        Period = period;
        _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised on the UI thread, at render priority, once per admitted tick.</summary>
    public event EventHandler? Tick;

    /// <summary>Gets the period the timer was asked for.</summary>
    public TimeSpan Period { get; }

    /// <summary>Gets the time since the clock was created.</summary>
    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <summary>Gets whether the clock is ticking.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Gets how many ticks were dropped because the previous one had not run yet.</summary>
    public long DroppedTicks => _gate.Dropped;

    /// <summary>Starts ticking. A no-op while already running.</summary>
    public void Start()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        _timer.Change(Period, Period);
    }

    /// <summary>Stops ticking. A tick already posted when this is called does not fire.</summary>
    public void Stop()
    {
        Dispatcher.UIThread.VerifyAccess();

        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _isRunning = false;
        _timer.Dispose();
    }

    private void OnTimer(object? state)
    {
        if (!_gate.TryArm())
        {
            return;
        }

        Dispatcher.UIThread.Post(OnDispatcherTick, DispatcherPriority.Render);
    }

    private void OnDispatcherTick()
    {
        _gate.Disarm();

        if (!_isRunning)
        {
            return;
        }

        Tick?.Invoke(this, EventArgs.Empty);
    }
}
