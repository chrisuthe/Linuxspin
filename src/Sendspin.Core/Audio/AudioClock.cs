using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sendspin.Core.Audio;

/// <summary>
/// One atomically-observed pairing of a device frame position with the host time at
/// which the device reported it.
/// </summary>
/// <param name="FramesPresented">
/// Frames the device has actually presented to the DAC.
/// </param>
/// <param name="HostTimeMicroseconds">
/// Time, in microseconds on the <em>platform's own</em> timebase, at which the device recorded
/// <paramref name="FramesPresented"/>.
/// </param>
/// <remarks>
/// <para>
/// <strong>The two fields are on different timebases and that is deliberate.</strong>
/// <paramref name="HostTimeMicroseconds"/> is whatever the platform hands back — QPC on Windows,
/// <c>mach_absolute_time</c> on macOS, <c>CLOCK_MONOTONIC</c> on Linux — and all three are
/// measured from boot. The Sendspin SDK's timeline is <em>Unix-epoch</em> microseconds, so the
/// two differ by decades. Handing a boot-relative figure to the SDK as "now" would report a sync
/// error of about fifty-six years, and nothing would ever play.
/// </para>
/// <para>
/// So a platform backend reports its native timebase here and does not attempt to convert.
/// <see cref="DeviceAnchoredClock"/> owns the conversion, in one place, by anchoring the origin
/// on the SDK's clock and taking only the <em>rate</em> from the frame counter.
/// </para>
/// </remarks>
public readonly record struct AudioClockReading(long FramesPresented, long HostTimeMicroseconds);

/// <summary>
/// A monotonic microsecond clock derived from an audio device's own frame counter.
/// </summary>
/// <remarks>
/// <para>
/// The Sendspin sync loop compares a server timestamp against "now". Taking "now" from the OS
/// wall clock imports every scheduling hiccup into the sync error, and worse, hides the
/// difference between where the OS thinks audio is and where the DAC actually is. Feeding this
/// clock instead makes "now" advance at the rate the hardware really consumes samples.
/// </para>
/// <para>
/// <strong>Origin from the SDK, rate from the DAC.</strong> The clock anchors once — recording
/// the device's frame count alongside the SDK's own notion of now — and thereafter extrapolates
/// purely from the frame counter. That combination is the whole point: the origin has to be on
/// the SDK's Unix-epoch timeline or the sync error is nonsense, while the rate has to come from
/// the hardware or there was no reason to do any of this.
/// </para>
/// <para>
/// It deliberately does <em>not</em> use <see cref="AudioClockReading.HostTimeMicroseconds"/> for
/// the projection. That value is on the platform's boot-relative timebase, and mixing it into an
/// epoch-based result is the bug this design exists to make impossible. It stays on the reading
/// because a backend needs it for latency arithmetic and because comparing its delta against the
/// frame delta is how a discontinuity is spotted.
/// </para>
/// <para>
/// Anchoring costs a small constant offset, because the SDK's "now" is read a little after the
/// device recorded the frame. That is benign: the SDK self-measures and subtracts a residual
/// constant offset once its startup grace period ends, so a fixed bias is absorbed, whereas a
/// wrong <em>rate</em> would never be.
/// </para>
/// <para>
/// A device change, a format change or a reported discontinuity must call <see cref="Reset"/>
/// before the frame counter restarts, or the restart reads as a large jump backwards.
/// </para>
/// </remarks>
public sealed class DeviceAnchoredClock
{
    private readonly int _sampleRate;
    private readonly Lock _gate = new();

    private long _anchorFrames;
    private long _anchorHostMicroseconds;
    private bool _anchored;

    public DeviceAnchoredClock(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        _sampleRate = sampleRate;
    }

    /// <summary>
    /// Gets whether a reading has been observed and the clock is usable.
    /// </summary>
    public bool IsAnchored
    {
        get { lock (_gate) return _anchored; }
    }

    /// <summary>
    /// Returns the instant, on the SDK's timeline, that the frame now leaving the DAC
    /// corresponds to.
    /// </summary>
    /// <param name="reading">The device reading, whose frame count supplies the rate.</param>
    /// <param name="hostNowMicroseconds">
    /// The SDK's current time in microseconds, which supplies the origin. Must come from the same
    /// source the SDK compares against — <c>HighPrecisionTimer</c>, i.e. Unix-epoch microseconds
    /// — and not from the platform's own clock.
    /// </param>
    public long Project(AudioClockReading reading, long hostNowMicroseconds)
    {
        lock (_gate)
        {
            if (!_anchored)
            {
                _anchorFrames = reading.FramesPresented;
                _anchorHostMicroseconds = hostNowMicroseconds;
                _anchored = true;
                return hostNowMicroseconds;
            }

            var elapsedFrames = reading.FramesPresented - _anchorFrames;
            var elapsedMicroseconds = elapsedFrames * 1_000_000L / _sampleRate;
            return _anchorHostMicroseconds + elapsedMicroseconds;
        }
    }

    /// <summary>
    /// Drops the anchor. Call on device switch, format change, or any reported discontinuity,
    /// before the frame counter restarts.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _anchored = false;
            _anchorFrames = 0;
            _anchorHostMicroseconds = 0;
        }
    }
}

/// <summary>
/// A seqlock-protected clock cell in unmanaged memory, written by a realtime audio
/// callback and read by ordinary managed code.
/// </summary>
/// <remarks>
/// <para>
/// A realtime callback may not take a lock, allocate, or call into managed code that
/// might. It can, however, write two longs into a fixed address and bump a sequence
/// counter either side of the write. Readers retry while the counter is odd or changed
/// mid-read, so they never observe a half-updated pair. This is the standard seqlock,
/// and it is why the cell has to live outside the GC heap: the writer holds a raw
/// pointer that must stay valid across collections.
/// </para>
/// <para>
/// Only one writer is permitted. Readers may be concurrent.
/// </para>
/// </remarks>
public sealed unsafe class SeqLockedAudioClockCell : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Cell
    {
        public long Sequence;
        public long FramesPresented;
        public long HostTimeMicroseconds;
    }

    /// <summary>
    /// Bound on reader retries. A correct writer holds the lock for three stores, so a
    /// reader that cannot get a stable pair within this many attempts is looking at a
    /// wedged or torn-down writer rather than losing a race.
    /// </summary>
    private const int MaxReadAttempts = 64;

    private Cell* _cell;
    private bool _disposed;

    public SeqLockedAudioClockCell()
    {
        // 64-byte alignment keeps the cell on its own cache line: the writer is a
        // realtime thread and must not contend with whatever the allocator put next to it.
        _cell = (Cell*)NativeMemory.AlignedAlloc((nuint)sizeof(Cell), 64);
        *_cell = default;
    }

    /// <summary>
    /// Publishes a reading. Realtime-safe: three stores and two fences, no allocation,
    /// no locking, no managed calls. Must be called from at most one thread.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Publish(long framesPresented, long hostTimeMicroseconds)
    {
        var cell = _cell;
        if (cell is null)
        {
            return;
        }

        var next = Volatile.Read(ref cell->Sequence) + 1;
        Volatile.Write(ref cell->Sequence, next);          // now odd: write in progress
        Volatile.Write(ref cell->FramesPresented, framesPresented);
        Volatile.Write(ref cell->HostTimeMicroseconds, hostTimeMicroseconds);
        Volatile.Write(ref cell->Sequence, next + 1);      // now even: readable
    }

    /// <summary>
    /// Reads the most recent complete reading, or null when the writer has not published
    /// yet or is being torn down.
    /// </summary>
    public AudioClockReading? TryRead()
    {
        var cell = _cell;
        if (cell is null)
        {
            return null;
        }

        for (var attempt = 0; attempt < MaxReadAttempts; attempt++)
        {
            var before = Volatile.Read(ref cell->Sequence);
            if (before == 0 || (before & 1) != 0)
            {
                continue;
            }

            var frames = Volatile.Read(ref cell->FramesPresented);
            var hostTime = Volatile.Read(ref cell->HostTimeMicroseconds);

            if (Volatile.Read(ref cell->Sequence) == before)
            {
                return new AudioClockReading(frames, hostTime);
            }
        }

        return null;
    }

    /// <summary>
    /// Releases the cell. The writer must have stopped first; a callback still running
    /// against the freed pointer is a use-after-free that no amount of managed care fixes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var cell = _cell;
        _cell = null;

        if (cell is not null)
        {
            NativeMemory.AlignedFree(cell);
        }
    }

    /// <summary>
    /// Gets the raw cell address, for handing to a native render callback.
    /// </summary>
    public nint Address => (nint)_cell;
}
