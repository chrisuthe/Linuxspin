namespace Sendspin.Core.Diagnostics;

/// <summary>
/// One reading of everything the diagnostics view shows.
/// </summary>
/// <remarks>
/// <para>
/// A single immutable snapshot rather than live-bound properties, so what the view renders is
/// internally consistent: a sync error read at one instant beside a playback rate read at
/// another invites the reader to draw a conclusion the numbers do not support.
/// </para>
/// <para>
/// This is the surface the sync-quality acceptance work is measured from, so it deliberately
/// includes the values needed to tell a real latency problem from a correction artefact:
/// the timing source actually in use, the measured output latency, and the clock uncertainty.
/// </para>
/// </remarks>
public sealed record PlayerDiagnosticsSnapshot
{
    /// <summary>Gets whether a server connection is up.</summary>
    public bool IsConnected { get; init; }

    /// <summary>Gets the connected server's name, or null.</summary>
    public string? ServerName { get; init; }

    /// <summary>Gets the negotiated codec, e.g. <c>flac</c>.</summary>
    public string? Codec { get; init; }

    /// <summary>Gets the negotiated sample rate in Hz.</summary>
    public int SampleRate { get; init; }

    /// <summary>Gets the negotiated channel count.</summary>
    public int Channels { get; init; }

    /// <summary>Gets the negotiated bit depth, or null when the codec does not specify one.</summary>
    public int? BitDepth { get; init; }

    /// <summary>Gets the raw sync error in microseconds.</summary>
    public long SyncErrorMicroseconds { get; init; }

    /// <summary>Gets the smoothed sync error in microseconds.</summary>
    public double SmoothedSyncErrorMicroseconds { get; init; }

    /// <summary>Gets the SDK's current correction mode.</summary>
    public string? CorrectionMode { get; init; }

    /// <summary>Gets the current playback rate, 1.0 being unmodified.</summary>
    public double PlaybackRate { get; init; } = 1.0;

    /// <summary>Gets the buffer depth in milliseconds.</summary>
    public double BufferedMilliseconds { get; init; }

    /// <summary>Gets the clock offset against the server, in milliseconds.</summary>
    public double ClockOffsetMilliseconds { get; init; }

    /// <summary>Gets the estimated clock drift in microseconds per second.</summary>
    public double ClockDriftMicrosecondsPerSecond { get; init; }

    /// <summary>Gets the clock offset uncertainty in microseconds.</summary>
    public double ClockOffsetUncertaintyMicroseconds { get; init; }

    /// <summary>Gets whether the clock filter has converged.</summary>
    public bool ClockConverged { get; init; }

    /// <summary>Gets the round-trip time to the server in microseconds.</summary>
    public double RoundTripMicroseconds { get; init; }

    /// <summary>Gets the measured output latency in milliseconds.</summary>
    public int OutputLatencyMs { get; init; }

    /// <summary>Gets the user's manual per-device latency offset in milliseconds.</summary>
    public double ManualLatencyOffsetMs { get; init; }

    /// <summary>Gets the effective static delay in milliseconds.</summary>
    public double StaticDelayMs { get; init; }

    /// <summary>
    /// Gets the timing source the SDK reports: <c>audio-clock</c>, <c>monotonic</c>, or
    /// <c>wall-clock</c>.
    /// </summary>
    /// <remarks>
    /// The most diagnostic single field here. Anything other than <c>audio-clock</c> means the
    /// platform backend is not supplying a hardware clock and every sync number below is
    /// resting on the OS timer instead.
    /// </remarks>
    public string? TimingSource { get; init; }

    /// <summary>Gets the active audio device's name.</summary>
    public string? AudioDeviceName { get; init; }

    /// <summary>Gets the platform name.</summary>
    public string? PlatformName { get; init; }

    /// <summary>Gets whether an OS media session is live.</summary>
    public bool MediaSessionActive { get; init; }

    /// <summary>An empty snapshot, shown before the first connection.</summary>
    public static PlayerDiagnosticsSnapshot Empty { get; } = new();
}

/// <summary>
/// Supplies the current diagnostics reading.
/// </summary>
public interface IDiagnosticsProvider
{
    /// <summary>
    /// Takes a reading. Cheap enough to call on a UI refresh timer; must not block.
    /// </summary>
    PlayerDiagnosticsSnapshot Capture();
}
