using Sendspin.SDK.Audio;

namespace Sendspin.Core.Audio;

/// <summary>
/// Which band the current sync error falls in, and therefore which mechanism the SDK will
/// use to answer it.
/// </summary>
public enum SyncCorrectionBand
{
    /// <summary>Inside the deadband. Correcting here chases measurement noise.</summary>
    Deadband,

    /// <summary>Small enough to walk off by nudging the playback rate. Inaudible.</summary>
    RateAdjust,

    /// <summary>Too large to walk off at the rate ceiling; the SDK drops or inserts frames.</summary>
    DropInsert,

    /// <summary>A discontinuity rather than drift. The SDK clears and re-anchors.</summary>
    Reanchor
}

/// <summary>
/// The playback-rate limits this player runs the SDK's sync correction under, and the
/// derivation that keeps them coherent with each other.
/// </summary>
/// <remarks>
/// <para>
/// The correction itself belongs to the SDK: <see cref="ITimedAudioBuffer"/> raises
/// <c>ReanchorRequired</c>, <see cref="IAudioPipeline.ReanchorTiming"/> answers it, and
/// <see cref="SyncCorrectionCalculator"/> decides drop/insert. This type does not
/// reimplement any of that. What it does is choose the numbers, because the SDK's shipped
/// numbers are tuned for a 2% rate ceiling and this player runs at 0.05%.
/// </para>
/// <para>
/// Why tighter: the SDK's default <see cref="SyncCorrectionOptions.MaxSpeedCorrection"/> is
/// 0.02 and its docs note that up to 0.04 is "typically imperceptible" against a roughly 3%
/// pitch-perception threshold. That is true of a brief correction and false of a player
/// holding a correction for minutes to track a drifting clock. Snapcast holds soft-sync to
/// 500 ppm — about forty times tighter than the SDK default — and that is the figure here.
/// </para>
/// <para>
/// Why the other numbers have to move with it. The SDK's default resampling threshold is
/// 100 ms, chosen so that at 2% a 100 ms error closes in about 5 s. At 500 ppm the same
/// error would take 200 s, during which the player sits audibly out of step with the rest of
/// the group. So <see cref="ResamplingThresholdMicroseconds"/> is *derived* from the ceiling
/// and the correction target rather than being an independent constant: rate adjustment is
/// only ever asked to close an error it can actually reach.
/// </para>
/// <para>
/// None of these are user-facing. They are buffer internals; the only calibration this app
/// exposes is <c>static_delay_ms</c> and the per-device latency offset, which are physical
/// facts about a room rather than preferences.
/// </para>
/// </remarks>
public sealed class SyncCorrectionPolicy
{
    /// <summary>
    /// Soft-correction ceiling in parts per million. 500 ppm is 0.05% — two orders of
    /// magnitude below audibility, and well inside the spec's 0.5% speed-deviation limit.
    /// </summary>
    public const double DefaultMaxSoftCorrectionPpm = 500.0;

    /// <summary>
    /// Errors below this are ignored. Set under the ±1 ms steady-state target but above the
    /// jitter of a well-behaved clock filter, so the player settles instead of hunting.
    /// </summary>
    public long DeadbandMicroseconds { get; init; } = 250;

    /// <summary>
    /// Maximum playback-rate deviation, in parts per million.
    /// </summary>
    public double MaxSoftCorrectionPpm { get; init; } = DefaultMaxSoftCorrectionPpm;

    /// <summary>
    /// How long a rate correction is given to close the error. Larger is gentler; it also
    /// widens <see cref="ResamplingThresholdMicroseconds"/>, since the two are linked.
    /// </summary>
    public double CorrectionTargetSeconds { get; init; } = 5.0;

    /// <summary>
    /// Errors at or above this are treated as a discontinuity and re-anchored rather than
    /// corrected. Left at the SDK's 500 ms: a gap that large is a stream restart, a device
    /// change or a suspend, none of which are drift.
    /// </summary>
    public long ReanchorThresholdMicroseconds { get; init; } = 500_000;

    /// <summary>
    /// Gets the largest error rate adjustment can close within
    /// <see cref="CorrectionTargetSeconds"/> at <see cref="MaxSoftCorrectionPpm"/>.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the type: at 500 ppm over 5 s the reachable error is
    /// 500e-6 × 5 s = 2500 us. Handing the SDK a wider threshold than this asks the
    /// resampler to close something it cannot, which is how a resampler ends up permanently
    /// pinned at its bound while the error stays put.
    /// </remarks>
    public long ResamplingThresholdMicroseconds =>
        (long)(MaxSoftCorrectionPpm / 1_000_000.0 * CorrectionTargetSeconds * 1_000_000.0);

    /// <summary>
    /// Classifies an error into the band that determines how the SDK will answer it.
    /// </summary>
    /// <remarks>
    /// For diagnostics and tests. It reports what the configured thresholds imply; it does
    /// not drive correction, which stays in the SDK.
    /// </remarks>
    /// <param name="smoothedErrorMicroseconds">
    /// Smoothed sync error in microseconds. Positive means the player is behind the server.
    /// </param>
    public SyncCorrectionBand Classify(double smoothedErrorMicroseconds)
    {
        var magnitude = Math.Abs(smoothedErrorMicroseconds);

        if (magnitude >= ReanchorThresholdMicroseconds)
        {
            return SyncCorrectionBand.Reanchor;
        }

        if (magnitude <= DeadbandMicroseconds)
        {
            return SyncCorrectionBand.Deadband;
        }

        return magnitude <= ResamplingThresholdMicroseconds
            ? SyncCorrectionBand.RateAdjust
            : SyncCorrectionBand.DropInsert;
    }

    /// <summary>
    /// Builds the SDK options this policy implies, so the SDK's own correction runs inside
    /// these limits rather than being bypassed.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown by <see cref="SyncCorrectionOptions.Validate"/> when the derived numbers are
    /// not a coherent set.
    /// </exception>
    public SyncCorrectionOptions ToSdkOptions()
    {
        var options = new SyncCorrectionOptions
        {
            DeadbandMicroseconds = DeadbandMicroseconds,
            MaxSpeedCorrection = MaxSoftCorrectionPpm / 1_000_000.0,
            CorrectionTargetSeconds = CorrectionTargetSeconds,
            ResamplingThresholdMicroseconds = ResamplingThresholdMicroseconds,
            ReanchorThresholdMicroseconds = ReanchorThresholdMicroseconds
        };

        options.Validate();
        return options;
    }
}
