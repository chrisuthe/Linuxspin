using Sendspin.Core.Audio;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for which rates a CoreAudio device is reported to run natively.
/// </summary>
/// <remarks>
/// <para>
/// These pin the fix for a defect that shipped: the macOS enumerator filled
/// <see cref="AudioDeviceInfo.SupportedSampleRates"/> from
/// <c>DeviceAvailableNominalSampleRates</c>, which lists rates the device could be
/// <em>switched</em> to. Since <c>AuhalRenderPlayer</c> never sets the nominal rate, every rate in
/// that list except the current one goes through AUHAL's converter — so a Mac at 48 kHz whose
/// output also lists 96 kHz advertised <c>flac/96000/16</c> ahead of <c>flac/48000/16</c> and
/// CoreAudio resampled it back down.
/// </para>
/// <para>
/// The shared tiering cannot catch this on its own, which is why the fix is here. On Linux a rate
/// above the current one genuinely is native when the daemon's clock policy permits it, so
/// "listed but not the current rate" is legitimate there and illegitimate on macOS. Only the
/// enumerator knows which of the two it is, which is the whole reason
/// <see cref="AudioDeviceInfo.SupportedSampleRates"/> now carries a written contract.
/// </para>
/// </remarks>
public sealed class CoreAudioSampleRateTests
{
    /// <summary>
    /// A switchable range does not widen the answer beyond the rate actually in use.
    /// </summary>
    [Fact]
    public void ASwitchableRange_DoesNotWidenTheNativeSet() =>
        Assert.Equal(
            [48_000],
            CoreAudioSampleRates.ResolveNative(48_000, [new SampleRateRange(44_100, 192_000)]));

    /// <summary>
    /// Discrete rates are reported as degenerate ranges, and are treated the same way.
    /// </summary>
    [Fact]
    public void DiscreteRates_AreNarrowedToTheNominalOne() =>
        Assert.Equal(
            [96_000],
            CoreAudioSampleRates.ResolveNative(
                96_000,
                [
                    new SampleRateRange(44_100, 44_100),
                    new SampleRateRange(48_000, 48_000),
                    new SampleRateRange(96_000, 96_000)
                ]));

    /// <summary>
    /// A device that cannot report ranges still runs at its nominal rate.
    /// </summary>
    [Fact]
    public void NoRanges_StillReportsTheNominalRate() =>
        Assert.Equal([48_000], CoreAudioSampleRates.ResolveNative(48_000, []));

    /// <summary>
    /// With no nominal rate nothing is known, and nothing is claimed.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NoNominalRate_ClaimsNothing(int nominalRate) =>
        Assert.Empty(CoreAudioSampleRates.ResolveNative(nominalRate, [new SampleRateRange(44_100, 192_000)]));

    /// <summary>
    /// A device whose ranges exclude its own nominal rate is inconsistent; claim nothing.
    /// </summary>
    [Fact]
    public void RangesExcludingTheNominalRate_ClaimNothing() =>
        Assert.Empty(CoreAudioSampleRates.ResolveNative(96_000, [new SampleRateRange(44_100, 48_000)]));

    /// <summary>
    /// The bounds are compared with a tolerance, because CoreAudio carries them as doubles.
    /// </summary>
    [Fact]
    public void RangeBounds_AreComparedWithATolerance() =>
        Assert.Equal(
            [44_100],
            CoreAudioSampleRates.ResolveNative(44_100, [new SampleRateRange(44_100.0000001, 44_100.0000001)]));
}
