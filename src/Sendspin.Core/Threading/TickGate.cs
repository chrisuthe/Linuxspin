namespace Sendspin.Core.Threading;

/// <summary>
/// Admits one tick at a time: a tick that arrives while the previous one is still waiting to run
/// is dropped rather than queued behind it.
/// </summary>
/// <remarks>
/// A timer that posts to a UI thread has no way to know the thread is busy, and a busy thread
/// turns a steady period into a burst of late ticks once it frees up. Arming the gate on the
/// timer's thread and disarming it on the UI thread, when the tick actually runs, is what keeps
/// the two from piling up. Thread-safe; the counter is for tests and diagnostics.
/// </remarks>
public sealed class TickGate
{
    private int _armed;
    private long _dropped;

    /// <summary>Gets how many ticks arrived while one was already waiting.</summary>
    public long Dropped => Volatile.Read(ref _dropped);

    /// <summary>Gets whether a tick is waiting to run.</summary>
    public bool IsArmed => Volatile.Read(ref _armed) == 1;

    /// <summary>
    /// Claims the gate for a tick. Returns false, and counts the drop, when one is already waiting.
    /// </summary>
    public bool TryArm()
    {
        if (Interlocked.CompareExchange(ref _armed, 1, 0) == 0)
        {
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    /// <summary>Releases the gate once the waiting tick has run.</summary>
    public void Disarm() => Volatile.Write(ref _armed, 0);
}
