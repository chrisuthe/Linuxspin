using Sendspin.Core.Visualization;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Pins the backdrop's maths, constants included: the WPF player's tests, ported with the code,
/// so the two apps breathe the same way.
/// </summary>
public sealed class AmbientMathTests
{
    [Fact]
    public void NormalizeLoudness_Null_ReturnsZero() =>
        Assert.Equal(0.0, AmbientMath.NormalizeLoudness(null));

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(65535, 1.0)]
    [InlineData(32767, 0.4999924, 1e-6)]
    public void NormalizeLoudness_MapsRawToUnitRange(int raw, double expected, double tolerance = 1e-9) =>
        Assert.Equal(expected, AmbientMath.NormalizeLoudness(raw), tolerance);

    [Theory]
    [InlineData(-100)]
    [InlineData(99999)]
    public void NormalizeLoudness_ClampsOutOfRange(int raw) =>
        Assert.InRange(AmbientMath.NormalizeLoudness(raw), 0.0, 1.0);

    [Fact]
    public void Ease_ZeroDt_ReturnsCurrent() =>
        Assert.Equal(0.2, AmbientMath.Ease(0.2, 1.0, dtSeconds: 0.0, timeConstantSeconds: 0.5));

    [Fact]
    public void Ease_NegativeDt_ReturnsCurrent() =>
        Assert.Equal(0.3, AmbientMath.Ease(0.3, 1.0, dtSeconds: -0.1, timeConstantSeconds: 0.5));

    [Fact]
    public void Ease_MovesTowardTarget()
    {
        // alpha = 1 - e^(-0.1/0.5) = 1 - e^(-0.2) = 0.1812692...
        var next = AmbientMath.Ease(0.0, 1.0, dtSeconds: 0.1, timeConstantSeconds: 0.5);
        Assert.Equal(0.1812692, next, 1e-6);
    }

    [Fact]
    public void Ease_LargeDt_ApproachesTarget()
    {
        var next = AmbientMath.Ease(0.0, 1.0, dtSeconds: 10.0, timeConstantSeconds: 0.5);
        Assert.True(next > 0.99, "after many time constants it should be near the target");
    }

    [Fact]
    public void Ease_ZeroTimeConstant_SnapsToTarget() =>
        Assert.Equal(1.0, AmbientMath.Ease(0.0, 1.0, dtSeconds: 0.016, timeConstantSeconds: 0.0));

    [Fact]
    public void Decay_AfterOneHalfLife_IsHalf() =>
        Assert.Equal(0.5, AmbientMath.Decay(1.0, dtSeconds: 0.25, halfLifeSeconds: 0.25), 0.001);

    [Fact]
    public void Decay_ZeroDt_ReturnsCurrent() =>
        Assert.Equal(0.8, AmbientMath.Decay(0.8, dtSeconds: 0.0, halfLifeSeconds: 0.3));

    [Fact]
    public void Decay_NegativeDt_ReturnsCurrent() =>
        Assert.Equal(0.8, AmbientMath.Decay(0.8, dtSeconds: -0.1, halfLifeSeconds: 0.3));

    [Fact]
    public void Decay_NonPositiveHalfLife_ReturnsZero() =>
        Assert.Equal(0.0, AmbientMath.Decay(1.0, dtSeconds: 0.016, halfLifeSeconds: 0.0));

    [Theory]
    [InlineData(0.0, 0.0, 0.82)]
    [InlineData(1.0, 0.0, 1.32)]
    [InlineData(1.0, 1.0, 1.67)]
    [InlineData(1.0, 2.0, 1.67)]
    public void BlobScale_MapsEnergyAndPulse(double energy, double pulse, double expected) =>
        Assert.Equal(expected, AmbientMath.BlobScale(energy, pulse), 0.0001);

    [Fact]
    public void BlobScale_ClampsNegativeInputs() =>
        Assert.Equal(0.82, AmbientMath.BlobScale(-1.0, -1.0), 0.0001);

    [Theory]
    [InlineData(0.0, 0.55)]
    [InlineData(1.0, 0.97)]
    public void BlobOpacity_MapsEnergy(double energy, double expected) =>
        Assert.Equal(expected, AmbientMath.BlobOpacity(energy), 0.0001);

    [Fact]
    public void BlobOpacity_ClampsOutOfRange()
    {
        Assert.Equal(0.55, AmbientMath.BlobOpacity(-1.0), 0.0001);
        Assert.Equal(0.97, AmbientMath.BlobOpacity(2.0), 0.0001);
    }

    /// <remarks>Intensity 0 leaves only ScaleMin: the floor is applied at the view model, not here.</remarks>
    [Theory]
    [InlineData(1.0, 0.0, 0.0, 0.82)]
    [InlineData(1.0, 0.0, 1.0, 1.32)]
    [InlineData(1.0, 0.0, 2.0, 1.82)]
    [InlineData(1.0, 1.0, 2.0, 2.52)]
    public void BlobScale_ScalesReactivityByIntensity(double energy, double pulse, double intensity, double expected) =>
        Assert.Equal(expected, AmbientMath.BlobScale(energy, pulse, intensity), 0.0001);

    [Theory]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(0.0, 1.0, 0.55)]
    [InlineData(0.0, 2.0, 1.0)]
    [InlineData(1.0, 2.0, 1.0)]
    public void BlobOpacity_ScalesPresenceByIntensity(double energy, double intensity, double expected) =>
        Assert.Equal(expected, AmbientMath.BlobOpacity(energy, intensity), 0.0001);

    [Fact]
    public void BlobScale_NegativeIntensity_ClampsToZeroReactivity() =>
        Assert.Equal(0.82, AmbientMath.BlobScale(1.0, 1.0, -5.0), 0.0001);

    [Fact]
    public void BlobOpacity_NegativeIntensity_ClampsToInvisible() =>
        Assert.Equal(0.0, AmbientMath.BlobOpacity(1.0, -5.0), 0.0001);

    [Fact]
    public void IntensityFloor_IsFaintButNonZero() =>
        Assert.InRange(AmbientMath.IntensityFloor, 0.05, 0.30);

    [Theory]
    [InlineData(0.0, 0.0, 1.0, 1.0)]
    [InlineData(1.0, 0.0, 1.0, 1.06)]
    [InlineData(1.0, 1.0, 1.0, 1.10)]
    [InlineData(1.0, 1.0, 2.0, 1.20)]
    [InlineData(1.0, 1.0, 0.0, 1.0)]
    public void BreathScale_RestsAtOneAndScalesByIntensity(double energy, double pulse, double intensity, double expected) =>
        Assert.Equal(expected, AmbientMath.BreathScale(energy, pulse, intensity), 0.0001);

    [Theory]
    [InlineData(0.0, 1.0, 0.15)]
    [InlineData(1.0, 1.0, 1.0)]
    [InlineData(1.0, 2.0, 1.0)]
    [InlineData(0.0, 0.0, 0.0)]
    public void BreathGlow_BaseAuraScalesAndClamps(double energy, double intensity, double expected) =>
        Assert.Equal(expected, AmbientMath.BreathGlow(energy, intensity), 0.0001);

    [Fact]
    public void BreathScale_NegativeIntensity_RestsAtOne() =>
        Assert.Equal(1.0, AmbientMath.BreathScale(1.0, 1.0, -3.0), 0.0001);

    [Fact]
    public void BreathScale_NegativeEnergyAndPulse_RestsAtOne() =>
        Assert.Equal(1.0, AmbientMath.BreathScale(-1.0, -1.0), 0.0001);

    [Fact]
    public void BreathGlow_NegativeIntensity_ReturnsZero() =>
        Assert.Equal(0.0, AmbientMath.BreathGlow(1.0, -3.0), 0.0001);
}
