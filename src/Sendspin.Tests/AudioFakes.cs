using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace Sendspin.Tests;

/// <summary>
/// A timed buffer holding a known sequence, so a reader can be checked for having consumed every
/// sample exactly once and in order.
/// </summary>
/// <remarks>
/// The whole point is the accounting. A sample source that reads more from the buffer than it
/// emits does not throw and does not sound broken in an obvious way — it just plays a fraction of
/// the stream — so the only way to catch it is to count what went in against what came out.
/// </remarks>
internal sealed class RampTimedAudioBuffer : ITimedAudioBuffer
{
    private readonly float[] _samples;
    private int _position;

    /// <param name="format">The stream format.</param>
    /// <param name="sampleCount">
    /// How many samples to hold. Values are 1, 2, 3 … so a gap or a repeat is obvious.
    /// </param>
    public RampTimedAudioBuffer(AudioFormat format, int sampleCount)
    {
        Format = format;
        _samples = new float[sampleCount];

        for (var i = 0; i < sampleCount; i++)
        {
            _samples[i] = i + 1;
        }
    }

    public AudioFormat Format { get; }

    public SyncCorrectionOptions SyncOptions { get; } = SyncCorrectionOptions.Default;

    public double BufferedMilliseconds => (_samples.Length - _position) * 1000.0
                                          / (Format.SampleRate * Math.Max(1, Format.Channels));

    public double TargetBufferMilliseconds { get; set; } = 150;

    public bool IsReadyForPlayback => true;

    public long OutputLatencyMicroseconds { get; set; }

    public long CalibratedStartupLatencyMicroseconds { get; set; }

    public string? TimingSourceName { get; set; } = "audio-clock";

    public long SyncErrorMicroseconds { get; set; }

    public double SmoothedSyncErrorMicroseconds { get; set; }

    public double TargetPlaybackRate { get; private set; } = 1.0;

    /// <summary>Total samples handed out through <see cref="ReadRaw"/>.</summary>
    public int TotalRead => _position;

    /// <summary>Rates reported back by the reader.</summary>
    public List<double> ReportedRates { get; } = [];

    /// <summary>External corrections the reader declared, as (dropped, inserted).</summary>
    public List<(int Dropped, int Inserted)> Corrections { get; } = [];

    public event Action<double>? TargetPlaybackRateChanged;

    public void Write(ReadOnlySpan<float> samples, long serverTimestamp) =>
        throw new NotSupportedException("This fake is pre-filled.");

    public int Read(Span<float> buffer, long currentLocalTime) =>
        throw new NotSupportedException("Obsolete on the real type; the source under test uses ReadRaw.");

    public int ReadRaw(Span<float> buffer, long currentLocalTime)
    {
        var available = Math.Min(buffer.Length, _samples.Length - _position);
        if (available <= 0)
        {
            return 0;
        }

        _samples.AsSpan(_position, available).CopyTo(buffer);
        _position += available;
        return available;
    }

    public void NotifyExternalCorrection(int samplesDropped, int samplesInserted) =>
        Corrections.Add((samplesDropped, samplesInserted));

    public void ReportExternalPlaybackRate(double rate)
    {
        TargetPlaybackRate = rate;
        ReportedRates.Add(rate);
        TargetPlaybackRateChanged?.Invoke(rate);
    }

    public void NotifyReconnect()
    {
    }

    public void Clear() => _position = 0;

    public AudioBufferStats GetStats() => new();

    public void Dispose()
    {
    }
}

/// <summary>
/// A correction provider whose mode and parameters the test sets directly, standing in for the
/// SDK's calculator so a single correction mode can be exercised in isolation.
/// </summary>
internal sealed class StubSyncCorrectionProvider : ISyncCorrectionProvider
{
    public SyncCorrectionMode CurrentMode { get; set; } = SyncCorrectionMode.None;

    public int DropEveryNFrames { get; set; }

    public int InsertEveryNFrames { get; set; }

    public double TargetPlaybackRate { get; set; } = 1.0;

    public event Action<ISyncCorrectionProvider>? CorrectionChanged;

    public void UpdateFromSyncError(long rawMicroseconds, double smoothedMicroseconds) =>
        CorrectionChanged?.Invoke(this);

    public void Reset()
    {
    }

    public void NotifyReconnect()
    {
    }
}
