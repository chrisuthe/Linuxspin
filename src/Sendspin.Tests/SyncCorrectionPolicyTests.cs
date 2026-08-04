using Sendspin.Core.Audio;
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
    [InlineData(10_000, SyncCorrectionBand.DropInsert)]
    [InlineData(-10_000, SyncCorrectionBand.DropInsert)]
    [InlineData(500_000, SyncCorrectionBand.Reanchor)]
    [InlineData(-900_000, SyncCorrectionBand.Reanchor)]
    public void Classify_PlacesErrorsInTheRightBand(double errorMicroseconds, SyncCorrectionBand expected) =>
        Assert.Equal(expected, new SyncCorrectionPolicy().Classify(errorMicroseconds));

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
