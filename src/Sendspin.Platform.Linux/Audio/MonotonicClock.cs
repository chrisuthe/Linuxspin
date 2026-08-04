using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sendspin.Platform.Linux.Audio;

/// <summary>
/// The host monotonic clock, in nanoseconds, on the same timebase the rest of the player
/// treats as "now".
/// </summary>
/// <remarks>
/// <para>
/// Reads <c>CLOCK_MONOTONIC</c> through <c>clock_gettime</c>. That is deliberately the same
/// clock .NET's <see cref="Stopwatch"/> uses on Linux, so a timestamp taken here and one
/// taken by managed code elsewhere in the pipeline share an origin and can be subtracted.
/// <c>CLOCK_MONOTONIC_RAW</c> would not: it is unslewed by NTP and drifts away from every
/// other timestamp in the process.
/// </para>
/// <para>
/// The <c>timespec</c> marshalling assumes a 64-bit <c>time_t</c>, which holds on every
/// architecture this app publishes for (x64, arm64). On a 32-bit runtime the struct layout would
/// be wrong, so the import is not used there. Nor is it used where <c>libc</c> cannot be resolved
/// — on a musl system, or on a glibc image with no <c>libc.so</c> development symlink, the name
/// does not load. Both cases fall back to <see cref="Stopwatch"/>, which on Linux is the same
/// <c>CLOCK_MONOTONIC</c> read by the runtime, so the timebase is unchanged either way.
/// </para>
/// </remarks>
internal static partial class MonotonicClock
{
    /// <summary>
    /// <c>CLOCK_MONOTONIC</c> from <c>bits/time.h</c>.
    /// </summary>
    private const int ClockMonotonic = 1;

    private const long NanosecondsPerSecond = 1_000_000_000L;

    private static readonly double NanosecondsPerStopwatchTick =
        (double)NanosecondsPerSecond / Stopwatch.Frequency;

    private static readonly bool UseNativeClock = ProbeNativeClock();

    /// <summary>
    /// Gets the current monotonic time in nanoseconds.
    /// </summary>
    public static long Nanoseconds
    {
        get
        {
            if (UseNativeClock && ClockGetTime(ClockMonotonic, out var time) == 0)
            {
                return (time.Seconds * NanosecondsPerSecond) + time.Nanoseconds;
            }

            return (long)(Stopwatch.GetTimestamp() * NanosecondsPerStopwatchTick);
        }
    }

    /// <summary>
    /// Establishes once whether <c>clock_gettime</c> can be called at all, so a system where the
    /// import cannot resolve fails here rather than on every timestamp.
    /// </summary>
    private static bool ProbeNativeClock()
    {
        if (IntPtr.Size != 8)
        {
            return false;
        }

        try
        {
            return ClockGetTime(ClockMonotonic, out _) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "clock_gettime")]
    private static partial int ClockGetTime(int clockId, out Timespec time);

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }
}
