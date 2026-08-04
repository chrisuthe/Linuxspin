using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.Platform.Shared.Audio;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for the sample source that realises the SDK's correction decisions.
/// </summary>
/// <remarks>
/// These are accounting tests, and that is deliberate. The failure mode of this class is not an
/// exception, it is consuming more of the stream than it plays — which sounds like the music is
/// running fast and skipping, and is very hard to attribute after the fact. So each test feeds a
/// known ramp and asserts that what left the buffer equals what reached the output.
/// </remarks>
public sealed class SyncCorrectedSampleSourceTests
{
    private const int Channels = 2;
    private const int FrameCount = 240;
    private static readonly int BlockSamples = FrameCount * Channels;

    [Fact]
    public void Read_WithNoCorrection_PassesTheStreamThroughUntouched()
    {
        var (buffer, provider, source) = Build();
        provider.CurrentMode = SyncCorrectionMode.None;

        var output = new float[BlockSamples];
        Assert.Equal(BlockSamples, source.Read(output, 0, BlockSamples));

        // 1, 2, 3 … straight through.
        for (var i = 0; i < BlockSamples; i++)
        {
            Assert.Equal(i + 1, output[i]);
        }

        Assert.Equal(BlockSamples, buffer.TotalRead);
    }

    /// <summary>
    /// While resampling, the source must not take more from the buffer than it accounts for.
    /// </summary>
    /// <remarks>
    /// The resampler needs a lookahead frame, so a naive implementation over-reads and throws the
    /// surplus away with its scratch buffer. At a 480-sample block that is a couple of frames per
    /// call — roughly 4000 ppm, an order of magnitude larger than the ±500 ppm the mechanism exists
    /// to apply, plus a discontinuity at every block boundary.
    /// </remarks>
    [Fact]
    public void Read_WhileResampling_ConsumesOnlyWhatItPlays()
    {
        var (buffer, provider, source) = Build();
        provider.CurrentMode = SyncCorrectionMode.Resampling;
        provider.TargetPlaybackRate = 1.0005;

        const int blocks = 8;
        var output = new float[BlockSamples];

        for (var block = 0; block < blocks; block++)
        {
            source.Read(output, 0, BlockSamples);
        }

        // At 1.0005 the source should have taken about 0.05% more than it emitted. Anything much
        // beyond that is surplus being discarded.
        var emitted = blocks * BlockSamples;
        var expected = emitted * provider.TargetPlaybackRate;
        var slack = Channels * 4;

        Assert.InRange(buffer.TotalRead, expected - slack, expected + slack);
    }

    [Fact]
    public void Read_WhileResampling_ReportsTheRateItActuallyPlayedAt()
    {
        var (buffer, provider, source) = Build();
        provider.CurrentMode = SyncCorrectionMode.Resampling;
        provider.TargetPlaybackRate = 0.9995;

        source.Read(new float[BlockSamples], 0, BlockSamples);

        Assert.Contains(0.9995, buffer.ReportedRates);
    }

    /// <summary>
    /// Dropping frames must consume exactly the frames it drops, and no more.
    /// </summary>
    /// <remarks>
    /// A drop reads two input frames and emits one, so the worst-case input for a block is bounded
    /// and knowable. An implementation that reads a whole extra block "to be safe" and then
    /// discards the remainder plays every other block — the stream runs at nearly double speed.
    /// </remarks>
    [Fact]
    public void Read_WhileDropping_ConsumesOnlyWhatItPlaysPlusTheDroppedFrames()
    {
        var (buffer, provider, source) = Build();
        provider.CurrentMode = SyncCorrectionMode.Dropping;
        provider.DropEveryNFrames = 20;

        source.Read(new float[BlockSamples], 0, BlockSamples);

        // 240 output frames with one extra frame consumed every 20 → 252 frames of input.
        var expectedFrames = FrameCount + (FrameCount / provider.DropEveryNFrames);
        Assert.InRange(buffer.TotalRead, (expectedFrames - 2) * Channels, (expectedFrames + 2) * Channels);
    }

    [Fact]
    public void Read_WhileDropping_ReportsTheDropsToTheBuffer()
    {
        var (_, provider, source) = Build();
        provider.CurrentMode = SyncCorrectionMode.Dropping;
        provider.DropEveryNFrames = 20;

        source.Read(new float[BlockSamples], 0, BlockSamples);

        var buffer = (RampTimedAudioBuffer)source.Buffer;
        Assert.NotEmpty(buffer.Corrections);
        Assert.True(buffer.Corrections.Sum(c => c.Dropped) > 0);
    }

    /// <summary>
    /// Inserting frames must consume fewer input frames than it emits, by the number it invented.
    /// </summary>
    /// <remarks>
    /// Measured across several blocks rather than one. A single call legitimately reads further
    /// ahead than it consumes — the surplus is retained rather than discarded, which is the whole
    /// point — so the ratio only settles once that fixed read-ahead is amortised.
    /// </remarks>
    [Fact]
    public void Read_WhileInserting_ConsumesFewerFramesThanItPlays()
    {
        var (buffer, provider, source) = Build();
        provider.CurrentMode = SyncCorrectionMode.Inserting;
        provider.InsertEveryNFrames = 20;

        const int blocks = 20;
        var output = new float[BlockSamples];

        for (var block = 0; block < blocks; block++)
        {
            source.Read(output, 0, BlockSamples);
        }

        var emitted = blocks * BlockSamples;
        var expected = emitted - (emitted / provider.InsertEveryNFrames);

        // One block of read-ahead is the most that can legitimately be outstanding.
        Assert.InRange(buffer.TotalRead, expected - BlockSamples, expected + BlockSamples);

        // And it must genuinely be fewer than it played, not merely close to it.
        Assert.True(buffer.TotalRead < emitted, $"read {buffer.TotalRead} for {emitted} emitted");
    }

    /// <summary>
    /// Across a run of blocks in the mode that actually fires in normal use, the stream must
    /// advance at roughly real time rather than being raced through.
    /// </summary>
    /// <remarks>
    /// Drop/insert is the correction band for any error between the resampling threshold and the
    /// re-anchor threshold — 2.5 ms to 500 ms with the shipped policy — so it is reached by a
    /// startup, a device switch or any network hiccup, not only by something exotic.
    /// </remarks>
    [Fact]
    public void Read_OverManyBlocks_DoesNotRaceThroughTheStream()
    {
        var (buffer, provider, source) = Build(sampleCount: 200_000);
        provider.CurrentMode = SyncCorrectionMode.Dropping;
        provider.DropEveryNFrames = 50;

        const int blocks = 100;
        var output = new float[BlockSamples];

        for (var block = 0; block < blocks; block++)
        {
            source.Read(output, 0, BlockSamples);
        }

        var emitted = blocks * BlockSamples;

        // One frame dropped every 50 is 2% over, so the input taken should be about 1.02x the
        // output. Allow generous slack, but nothing close to the 2x a discarded-surplus bug gives.
        Assert.InRange(buffer.TotalRead, emitted * 1.0, emitted * 1.10);
    }

    /// <summary>
    /// A buffer that runs dry must produce silence and still return a full block, so the device is
    /// never handed a partially-filled buffer containing whatever was there before.
    /// </summary>
    [Fact]
    public void Read_OnExhaustedBuffer_ReturnsAFullBlockOfSilence()
    {
        var (_, provider, source) = Build(sampleCount: BlockSamples);
        provider.CurrentMode = SyncCorrectionMode.None;

        source.Read(new float[BlockSamples], 0, BlockSamples);

        var second = new float[BlockSamples];
        Array.Fill(second, 9f);

        Assert.Equal(BlockSamples, source.Read(second, 0, BlockSamples));
        Assert.All(second, sample => Assert.Equal(0f, sample));
    }

    /// <summary>
    /// The source writes into the caller's buffer at the requested offset and nowhere else.
    /// </summary>
    [Fact]
    public void Read_RespectsTheOffset()
    {
        var (_, provider, source) = Build();
        provider.CurrentMode = SyncCorrectionMode.None;

        var output = new float[BlockSamples + 8];
        Array.Fill(output, -1f);

        source.Read(output, 4, BlockSamples);

        Assert.Equal(-1f, output[0]);
        Assert.Equal(-1f, output[3]);
        Assert.Equal(1f, output[4]);
        Assert.Equal(-1f, output[^1]);
    }

    private static (RampTimedAudioBuffer Buffer, StubSyncCorrectionProvider Provider, SyncCorrectedSampleSource Source)
        Build(int sampleCount = 100_000)
    {
        var format = new AudioFormat
        {
            Codec = AudioCodecs.Pcm,
            SampleRate = 48_000,
            Channels = Channels,
            BitDepth = 16
        };

        var buffer = new RampTimedAudioBuffer(format, sampleCount);
        var provider = new StubSyncCorrectionProvider();
        var source = new SyncCorrectedSampleSource(
            buffer, provider, () => 0L, NullLogger<SyncCorrectedSampleSource>.Instance);

        return (buffer, provider, source);
    }
}
