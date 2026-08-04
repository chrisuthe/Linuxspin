using Microsoft.Extensions.Logging;
using Silk.NET.OpenAL;

namespace Sendspin.Platform.Linux.Audio;

/// <summary>
/// The OpenAL Soft timing extensions, bound by hand.
/// </summary>
/// <remarks>
/// <para>
/// <c>Silk.NET.OpenAL</c> does not bind these at all — <c>Silk.NET.OpenAL.Extensions.Soft</c>
/// has neither <c>SourceLatency</c> nor <c>DeviceClock</c> — and they are the only honest
/// source of output latency on this platform. So the three entry points are resolved through
/// <c>alcGetProcAddress</c>/<c>alGetProcAddress</c> and called through
/// <c>delegate* unmanaged[Cdecl]</c>. Function pointers rather than
/// <see cref="System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer{TDelegate}"/>
/// — which is what OpenTK would give us — because the latter is neither trim- nor AOT-friendly.
/// </para>
/// <para>
/// <strong>The device clock is not <c>CLOCK_MONOTONIC</c>.</strong> It is OpenAL's own mixer
/// clock: it does not advance continuously but jumps forward by a whole mix period, hundreds of
/// samples at a time, whenever the mixer runs. A reading is therefore only meaningful when it
/// is paired with the host instant at which it was taken, and that pairing is what
/// <see cref="TryReadDeviceClock"/> establishes by sandwiching the call between two host clock
/// reads and taking the midpoint. When the two host reads straddle a scheduling delay the
/// midpoint would be wrong by up to half of it, so such a sample is discarded and the read is
/// retried.
/// </para>
/// <para>
/// Every member reports absence rather than failing: a driver without these extensions is a
/// supported configuration, just a less accurate one.
/// </para>
/// </remarks>
internal sealed unsafe class AlSoftExtensions
{
    /// <summary>
    /// How far the two host clock reads either side of a device clock read may be apart before
    /// the sample is rejected.
    /// </summary>
    /// <remarks>
    /// The midpoint is wrong by at most half the spread, so 250 µs bounds the host-time error
    /// at 125 µs — an eighth of the ±1 ms the specification asks of playback alignment, and
    /// well inside the accuracy the device clock itself offers.
    /// </remarks>
    private const long MaxHostReadSpreadNanoseconds = 250_000;

    /// <summary>
    /// Attempts before giving up on a stable pair. Each attempt costs one device-lock
    /// acquisition, so this bounds the cost of a heavily contended mixer.
    /// </summary>
    private const int MaxReadAttempts = 5;

    private const string DeviceClockExtension = "ALC_SOFT_device_clock";
    private const string SourceLatencyExtension = "AL_SOFT_source_latency";
    private const string SourceStartDelayExtension = "AL_SOFT_source_start_delay";

    /// <summary>Returns the device clock and its latency as two int64 nanosecond values.</summary>
    private const int AlcDeviceClockLatencySoft = 0x1602;

    /// <summary>Returns the source's 32.32 fixed-point sample offset and its latency in ns.</summary>
    private const int AlSampleOffsetLatencySoft = 0x1200;

    private readonly ILogger _logger;
    private readonly Device* _device;
    private readonly delegate* unmanaged[Cdecl]<Device*, int, int, long*, void> _alcGetInteger64v;
    private readonly delegate* unmanaged[Cdecl]<uint, int, long*, void> _alGetSourcei64v;
    private readonly delegate* unmanaged[Cdecl]<uint, long, void> _alSourcePlayAtTime;

    /// <summary>
    /// Resolves the extensions for an open device. Must be called with the device's context
    /// current, because <c>alIsExtensionPresent</c> and <c>alGetProcAddress</c> both answer for
    /// the current context rather than for the device.
    /// </summary>
    public AlSoftExtensions(AL al, ALContext alc, Device* device, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(al);
        ArgumentNullException.ThrowIfNull(alc);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _device = device;

        if (alc.IsExtensionPresent(device, DeviceClockExtension))
        {
            _alcGetInteger64v = (delegate* unmanaged[Cdecl]<Device*, int, int, long*, void>)
                alc.GetProcAddress(device, "alcGetInteger64vSOFT");
        }

        if (al.IsExtensionPresent(SourceLatencyExtension))
        {
            _alGetSourcei64v = (delegate* unmanaged[Cdecl]<uint, int, long*, void>)
                al.GetProcAddress("alGetSourcei64vSOFT");
        }

        if (al.IsExtensionPresent(SourceStartDelayExtension))
        {
            _alSourcePlayAtTime = (delegate* unmanaged[Cdecl]<uint, long, void>)
                al.GetProcAddress("alSourcePlayAtTimeSOFT");
        }

        _logger.LogInformation(
            "OpenAL Soft timing extensions: {DeviceClockExtension} {DeviceClock}, " +
            "{SourceLatencyExtension} {SourceLatency}, {StartDelayExtension} {StartDelay}",
            DeviceClockExtension, Available(HasDeviceClock),
            SourceLatencyExtension, Available(HasSourceLatency),
            SourceStartDelayExtension, Available(HasScheduledStart));
    }

    /// <summary>
    /// Gets whether the device clock, and therefore a real latency figure, is available.
    /// </summary>
    public bool HasDeviceClock => _alcGetInteger64v is not null;

    /// <summary>
    /// Gets whether per-source latency is available as a second route to the same figure.
    /// </summary>
    public bool HasSourceLatency => _alGetSourcei64v is not null;

    /// <summary>
    /// Gets whether playback can be scheduled for an exact device-clock instant.
    /// </summary>
    public bool HasScheduledStart => _alSourcePlayAtTime is not null;

    /// <summary>
    /// Reads the device clock paired with the host instant it was taken at.
    /// </summary>
    /// <param name="clockNanoseconds">The device clock, in nanoseconds since device open.</param>
    /// <param name="latencyNanoseconds">
    /// The device's output latency at that instant, in nanoseconds. Read atomically with the
    /// clock, which is the reason for preferring <c>ALC_DEVICE_CLOCK_LATENCY_SOFT</c> over two
    /// separate queries.
    /// </param>
    /// <param name="hostTimeMicroseconds">
    /// Host monotonic time, in microseconds, at which the device recorded that clock value.
    /// </param>
    /// <returns>False when the extension is absent or no stable host pairing could be taken.</returns>
    public bool TryReadDeviceClock(out long clockNanoseconds, out long latencyNanoseconds, out long hostTimeMicroseconds)
    {
        clockNanoseconds = 0;
        latencyNanoseconds = 0;
        hostTimeMicroseconds = 0;

        var read = _alcGetInteger64v;
        if (read is null)
        {
            return false;
        }

        var values = stackalloc long[2];

        for (var attempt = 0; attempt < MaxReadAttempts; attempt++)
        {
            var before = MonotonicClock.Nanoseconds;
            read(_device, AlcDeviceClockLatencySoft, 2, values);
            var after = MonotonicClock.Nanoseconds;

            var spread = after - before;
            if (spread < 0 || spread > MaxHostReadSpreadNanoseconds)
            {
                continue;
            }

            clockNanoseconds = values[0];
            latencyNanoseconds = values[1];
            hostTimeMicroseconds = (before + (spread / 2)) / 1_000;
            return true;
        }

        _logger.LogDebug(
            "Discarded {Attempts} device clock reads: no host pairing within {SpreadUs} µs",
            MaxReadAttempts, MaxHostReadSpreadNanoseconds / 1_000);
        return false;
    }

    /// <summary>
    /// Reads just the device's output latency in nanoseconds.
    /// </summary>
    /// <remarks>
    /// No host pairing is taken, because latency is not a timestamp: it does not need to be
    /// tied to an instant to be meaningful.
    /// </remarks>
    public bool TryReadDeviceLatencyNanoseconds(out long latencyNanoseconds)
    {
        latencyNanoseconds = 0;

        var read = _alcGetInteger64v;
        if (read is null)
        {
            return false;
        }

        var values = stackalloc long[2];
        read(_device, AlcDeviceClockLatencySoft, 2, values);
        latencyNanoseconds = values[1];
        return true;
    }

    /// <summary>
    /// Reads a source's playback offset and the latency until that sample is audible.
    /// </summary>
    /// <param name="source">The source to query.</param>
    /// <param name="frameOffset">
    /// Frames played from the start of the source's current buffer queue.
    /// </param>
    /// <param name="latencyNanoseconds">
    /// Nanoseconds between that frame being read by the mixer and it leaving the device.
    /// </param>
    public bool TryReadSourceLatency(uint source, out long frameOffset, out long latencyNanoseconds)
    {
        frameOffset = 0;
        latencyNanoseconds = 0;

        var read = _alGetSourcei64v;
        if (read is null)
        {
            return false;
        }

        var values = stackalloc long[2];
        read(source, AlSampleOffsetLatencySoft, values);

        // The offset is 32.32 fixed point: whole frames in the high word, fraction in the low.
        frameOffset = values[0] >> 32;
        latencyNanoseconds = values[1];
        return true;
    }

    /// <summary>
    /// Starts a source when the device clock reaches <paramref name="deviceTimeNanoseconds"/>.
    /// </summary>
    /// <remarks>
    /// Works with a streaming buffer queue, and is the only way to make the start instant
    /// deterministic rather than "whenever the mixer next runs".
    /// </remarks>
    /// <returns>False when the extension is absent, in which case the caller must start now.</returns>
    public bool TryPlayAtDeviceTime(uint source, long deviceTimeNanoseconds)
    {
        var play = _alSourcePlayAtTime;
        if (play is null)
        {
            return false;
        }

        play(source, deviceTimeNanoseconds);
        return true;
    }

    private static string Available(bool present) => present ? "available" : "unavailable";
}
