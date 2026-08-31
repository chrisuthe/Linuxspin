using System.Reflection;
using Sendspin.Core.Audio;
using Sendspin.SDK.Audio;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for the sync-correction limits and the derivation that keeps them coherent.
/// </summary>
public sealed class SyncCorrectionPolicyTests
{
    [Fact]
    public void DefaultCeiling_Is500Ppm() =>
        Assert.Equal(500.0, new SyncCorrectionPolicy().MaxSoftCorrectionPpm);

    /// <summary>
    /// The derivation is the whole point of the type, so it is asserted directly.
    /// </summary>
    /// <remarks>
    /// At 500 ppm over a 5 s target the reachable error is 500e-6 × 5 s = 2500 µs. Handing the SDK
    /// a wider resampling threshold than this asks the resampler to close an error it cannot
    /// reach, which is how a resampler ends up pinned at its bound while the error stays put.
    /// </remarks>
    [Theory]
    [InlineData(500.0, 5.0, 2_500)]
    [InlineData(500.0, 3.0, 1_500)]
    [InlineData(2_000.0, 5.0, 10_000)]
    [InlineData(100.0, 5.0, 500)]
    public void ResamplingThreshold_IsDerivedFromCeilingAndTarget(double ppm, double targetSeconds, long expected)
    {
        var policy = new SyncCorrectionPolicy
        {
            MaxSoftCorrectionPpm = ppm,
            CorrectionTargetSeconds = targetSeconds
        };

        Assert.Equal(expected, policy.ResamplingThresholdMicroseconds);
    }

    [Theory]
    [InlineData(0, SyncCorrectionBand.Deadband)]
    [InlineData(250, SyncCorrectionBand.Deadband)]
    [InlineData(-250, SyncCorrectionBand.Deadband)]
    [InlineData(1_000, SyncCorrectionBand.RateAdjust)]
    [InlineData(-1_000, SyncCorrectionBand.RateAdjust)]
    [InlineData(2_500, SyncCorrectionBand.RateAdjust)]
    [InlineData(2_501, SyncCorrectionBand.DropInsert)]
    [InlineData(5_000, SyncCorrectionBand.DropInsert)]
    [InlineData(-5_000, SyncCorrectionBand.DropInsert)]
    [InlineData(5_001, SyncCorrectionBand.HardSync)]
    [InlineData(50_000, SyncCorrectionBand.HardSync)]
    [InlineData(-50_000, SyncCorrectionBand.HardSync)]
    [InlineData(499_999, SyncCorrectionBand.HardSync)]
    [InlineData(500_000, SyncCorrectionBand.Reanchor)]
    [InlineData(-900_000, SyncCorrectionBand.Reanchor)]
    public void Classify_PlacesErrorsInTheRightBand(double errorMicroseconds, SyncCorrectionBand expected) =>
        Assert.Equal(expected, new SyncCorrectionPolicy().Classify(errorMicroseconds));

    /// <summary>
    /// The whole ladder, in order, at the thresholds this player actually configures.
    /// </summary>
    /// <remarks>
    /// Pinned as one test rather than left to the per-band cases above, because the property that
    /// matters is that the four boundaries are strictly increasing and cover the range without a
    /// gap. A band that silently swallowed its neighbour would still pass every individual case
    /// while making the diagnostics view lie about what the SDK is doing.
    /// </remarks>
    [Fact]
    public void Classify_CoversTheLadderInOrder()
    {
        var policy = new SyncCorrectionPolicy();

        Assert.Equal(250, policy.DeadbandMicroseconds);
        Assert.Equal(2_500, policy.ResamplingThresholdMicroseconds);
        Assert.Equal(5_000, policy.HardSyncThresholdMicroseconds);
        Assert.Equal(500_000, policy.ReanchorThresholdMicroseconds);

        Assert.True(
            policy.DeadbandMicroseconds
            < policy.ResamplingThresholdMicroseconds
            && policy.ResamplingThresholdMicroseconds < policy.HardSyncThresholdMicroseconds
            && policy.HardSyncThresholdMicroseconds < policy.ReanchorThresholdMicroseconds,
            "the ladder's thresholds must strictly increase");

        SyncCorrectionBand[] expected =
        [
            SyncCorrectionBand.Deadband,
            SyncCorrectionBand.RateAdjust,
            SyncCorrectionBand.DropInsert,
            SyncCorrectionBand.HardSync,
            SyncCorrectionBand.Reanchor
        ];

        // One sample just inside each band's upper edge, then one past the last.
        double[] samples = [250, 2_500, 5_000, 499_999, 500_000];

        Assert.Equal(expected, samples.Select(policy.Classify));
    }

    /// <summary>
    /// The drop/insert band exists here only because the resampling threshold is below the SDK's
    /// hard-sync threshold, which is unusual enough that the SDK documents the band as normally
    /// unreachable.
    /// </summary>
    [Fact]
    public void DropInsertBand_IsReachableOnlyBecauseTheResamplingThresholdIsLowered() =>
        Assert.True(
            new SyncCorrectionPolicy().ResamplingThresholdMicroseconds < 5_000,
            "with the SDK's own 100 ms resampling threshold the drop/insert band would be skipped");

    /// <summary>
    /// The SDK validates its own options, so a policy that produced an incoherent set would throw
    /// here rather than at the first connection.
    /// </summary>
    [Fact]
    public void ToSdkOptions_ProducesAValidCoherentSet()
    {
        var policy = new SyncCorrectionPolicy();
        var options = policy.ToSdkOptions();

        Assert.Equal(0.0005, options.MaxSpeedCorrection, precision: 8);
        Assert.Equal(0.9995, options.MinRate, precision: 8);
        Assert.Equal(1.0005, options.MaxRate, precision: 8);
        Assert.Equal(policy.DeadbandMicroseconds, options.DeadbandMicroseconds);
        Assert.Equal(policy.ResamplingThresholdMicroseconds, options.ResamplingThresholdMicroseconds);
        Assert.Equal(policy.ReanchorThresholdMicroseconds, options.ReanchorThresholdMicroseconds);

        // Reanchor must sit above the drop/insert band, or a moderate error would be treated as a
        // discontinuity and produce an audible restart instead of a correction.
        Assert.True(options.ReanchorThresholdMicroseconds > options.ResamplingThresholdMicroseconds);
    }

    /// <summary>
    /// The configured ceiling must sit inside the spec cap the SDK now enforces, so nothing this
    /// player asks for is silently clamped.
    /// </summary>
    /// <remarks>
    /// 9.3.0 made <c>MaxSpeedCorrection</c> a clamped value — corrections use
    /// <c>EffectiveMaxSpeedCorrection</c>, and the SDK logs a warning once when it sees a
    /// configured value above the cap. Both of those members are internal on the 9.x line, so this
    /// reads them by reflection rather than asserting the arithmetic and calling it confirmed. The
    /// lookups are asserted to resolve: if a later SDK renames them, this must fail loudly rather
    /// than quietly stop checking anything.
    /// </remarks>
    [Fact]
    public void ToSdkOptions_StaysInsideTheSpecSpeedCapWithoutBeingClamped()
    {
        var options = new SyncCorrectionPolicy().ToSdkOptions();
        var type = typeof(SyncCorrectionOptions);
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var exceeds = type.GetProperty("ExceedsSpecSpeedCap", Flags);
        var effective = type.GetProperty("EffectiveMaxSpeedCorrection", Flags);

        Assert.NotNull(exceeds);
        Assert.NotNull(effective);

        Assert.False((bool)exceeds.GetValue(options)!, "500 ppm must not be clamped by the 0.5% cap");
        Assert.Equal(options.MaxSpeedCorrection, (double)effective.GetValue(options)!, precision: 8);
    }

    /// <summary>
    /// The deadband must be tighter than the steady-state target the spec sets, or the player
    /// would never correct inside its own accuracy goal.
    /// </summary>
    /// <remarks>
    /// The SDK's shipped default is 1000 µs, which is exactly the ±1 ms target — so leaving it at
    /// the default would mean an error of 0.9 ms is both out of specification and ignored.
    /// </remarks>
    [Fact]
    public void Deadband_IsTighterThanTheOneMillisecondTarget() =>
        Assert.True(new SyncCorrectionPolicy().DeadbandMicroseconds < 1_000);
}
