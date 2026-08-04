using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.Core.Audio;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for the perceived-loudness volume mapping.
/// </summary>
public sealed class VolumeCurveTests
{
    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(25, 0.125)]
    [InlineData(50, 0.35355339)]
    [InlineData(75, 0.64951905)]
    [InlineData(100, 1.0)]
    public void ToAmplitude_MatchesTheSpecCurve(int volume, double expected) =>
        Assert.Equal(expected, VolumeCurve.ToAmplitude(volume), precision: 6);

    [Theory]
    [InlineData(-10)]
    [InlineData(150)]
    public void ToAmplitude_ClampsOutOfRangeVolume(int volume)
    {
        var amplitude = VolumeCurve.ToAmplitude(volume);
        Assert.InRange(amplitude, 0f, 1f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(75)]
    [InlineData(100)]
    public void ToVolume_RoundTripsToAmplitude(int volume) =>
        Assert.Equal(volume, VolumeCurve.ToVolume(VolumeCurve.ToAmplitude(volume)));

    /// <summary>
    /// Pins where the loudness curve is applied, by driving the SDK's own pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test that matters, and it deliberately does not assert our arithmetic against
    /// itself. <c>AudioPipeline.SetVolume(int)</c> applies the curve *inside the SDK*, so what
    /// reaches <see cref="IAudioPlayer.Volume"/> is already an amplitude. A platform player that
    /// raises it to 1.5 again halves the user's volume — which was live on Windows.
    /// </para>
    /// <para>
    /// So the assertion is that the SDK's conversion equals
    /// <see cref="VolumeCurve.ToAmplitude(int)"/>. It fails if a platform starts double-applying,
    /// and it fails if a future SDK moves the curve out of the pipeline and leaves every player
    /// silently applying none.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(75)]
    [InlineData(100)]
    public async Task AudioPipeline_AppliesTheCurveExactlyOnce(int volume)
    {
        var player = new RecordingAudioPlayer();
        await using var pipeline = BuildPipeline(player);

        await pipeline.StartAsync(
            new AudioFormat { Codec = AudioCodecs.Pcm, SampleRate = 48_000, Channels = 2, BitDepth = 16 },
            targetTimestamp: null,
            CancellationToken.None);

        pipeline.SetVolume(volume);

        Assert.Equal(VolumeCurve.ToAmplitude(volume), player.Volume, precision: 6);
    }

    private static AudioPipeline BuildPipeline(IAudioPlayer player) => new(
        NullLogger<AudioPipeline>.Instance,
        new AudioDecoderFactory(NullLoggerFactory.Instance),
        new ConvergedClockSynchronizer(),
        bufferFactory: (format, sync) => new TimedAudioBuffer(
            format, sync, 4_000, SyncCorrectionOptions.Default, NullLogger<TimedAudioBuffer>.Instance),
        playerFactory: () => player,
        sourceFactory: (buffer, now) => new BufferSampleSource(buffer, now),
        precisionTimer: null,
        waitForConvergence: false,
        convergenceTimeoutMs: 1,
        useMonotonicTimer: true);
}
