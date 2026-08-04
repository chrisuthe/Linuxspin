using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace Sendspin.Platform.Shared.Audio;

/// <summary>
/// Bridges <see cref="ITimedAudioBuffer"/> to <see cref="IAudioSampleSource"/>, applying the
/// correction an <see cref="ISyncCorrectionProvider"/> asks for.
/// </summary>
/// <remarks>
/// <para>
/// This is the SDK's sanctioned path: <see cref="ITimedAudioBuffer.Read"/> is obsolete in
/// favour of <see cref="ITimedAudioBuffer.ReadRaw"/> plus an external provider, so that the
/// embedder controls how corrections are realised.
/// </para>
/// <para>
/// It honours all three of the provider's modes. That matters: an earlier version implemented
/// only frame drop and insert, and silently ignored
/// <see cref="ISyncCorrectionProvider.TargetPlaybackRate"/>. The consequence was that every
/// correction, however small, was realised by discarding or duplicating a frame — audible as
/// a tick — when the provider was asking for an inaudible rate nudge. Rate changes now go
/// through <see cref="DriftResampler"/>, and drop/insert is reserved for errors too large for
/// the rate ceiling to close.
/// </para>
/// <para>
/// <strong>Realtime discipline.</strong> <see cref="Read"/> runs on the render path. It
/// allocates nothing after the first call: scratch buffers are sized once from the first
/// request and reused. It takes no lock. Growing a buffer is logged, because on a fixed-size
/// audio path it should happen once and never again.
/// </para>
/// </remarks>
public sealed class SyncCorrectedSampleSource : IAudioSampleSource, IDisposable
{
    private readonly ITimedAudioBuffer _buffer;
    private readonly ISyncCorrectionProvider _correctionProvider;
    private readonly Func<long> _currentTimeMicroseconds;
    private readonly DriftResampler _resampler;
    private readonly ILogger _logger;
    private readonly int _channels;

    private float[] _scratch = [];
    private float[] _correctionFrame;
    private int _framesSinceCorrection;
    private long _totalSamplesDropped;
    private long _totalSamplesInserted;
    private long _lastLogTimestamp;
    private bool _disposed;

    /// <summary>
    /// How often correction state is logged. Once a second: often enough to watch a
    /// convergence, rare enough not to be the reason the render path misses a deadline.
    /// </summary>
    private const long LogIntervalMicroseconds = 1_000_000;

    public SyncCorrectedSampleSource(
        ITimedAudioBuffer buffer,
        ISyncCorrectionProvider correctionProvider,
        Func<long> currentTimeMicroseconds,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(correctionProvider);
        ArgumentNullException.ThrowIfNull(currentTimeMicroseconds);
        ArgumentNullException.ThrowIfNull(logger);

        _buffer = buffer;
        _correctionProvider = correctionProvider;
        _currentTimeMicroseconds = currentTimeMicroseconds;
        _logger = logger;
        _channels = Math.Max(1, buffer.Format.Channels);
        _resampler = new DriftResampler(_channels);
        _correctionFrame = new float[_channels];
    }

    /// <inheritdoc/>
    public AudioFormat Format => _buffer.Format;

    /// <summary>
    /// Gets the buffer being read, for the diagnostics view.
    /// </summary>
    public ITimedAudioBuffer Buffer => _buffer;

    /// <summary>
    /// Gets the correction provider, for the diagnostics view.
    /// </summary>
    public ISyncCorrectionProvider CorrectionProvider => _correctionProvider;

    /// <inheritdoc/>
    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (count <= 0)
        {
            return 0;
        }

        var now = _currentTimeMicroseconds();
        var output = buffer.AsSpan(offset, count);

        _correctionProvider.UpdateFromSyncError(
            _buffer.SyncErrorMicroseconds,
            _buffer.SmoothedSyncErrorMicroseconds);

        var mode = _correctionProvider.CurrentMode;
        var written = mode switch
        {
            SyncCorrectionMode.Resampling => ReadResampled(output, now),
            SyncCorrectionMode.Dropping or SyncCorrectionMode.Inserting => ReadWithDropInsert(output, now),
            _ => ReadDirect(output, now)
        };

        if (written < count)
        {
            // Underrun. Silence rather than stale audio, and the device still gets a full
            // buffer so it does not glitch on top of the shortfall.
            output[written..].Clear();
        }

        if (_correctionProvider is SyncCorrectionCalculator calculator)
        {
            calculator.NotifySamplesProcessed(written);
        }

        LogPeriodically(now, mode);

        // Always report the full request: the backend asked for a fixed-size buffer and has
        // been given one.
        return count;
    }

    /// <summary>
    /// Discards correction and interpolation state. Call after a buffer clear or a re-anchor,
    /// so the first frame afterwards is not interpolated against audio from before it.
    /// </summary>
    public void Reset()
    {
        _framesSinceCorrection = 0;
        _totalSamplesDropped = 0;
        _totalSamplesInserted = 0;
        _resampler.Reset();
        _correctionProvider.Reset();
        Array.Clear(_correctionFrame);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scratch = [];
    }

    private int ReadDirect(Span<float> output, long now) => _buffer.ReadRaw(output, now);

    /// <summary>
    /// Reads at the provider's target rate, resampling to fill the request exactly.
    /// </summary>
    private int ReadResampled(Span<float> output, long now)
    {
        var rate = _correctionProvider.TargetPlaybackRate;

        if (rate is <= 0.0 or double.NaN)
        {
            return ReadDirect(output, now);
        }

        var outputFrames = output.Length / _channels;
        var requiredFrames = _resampler.RequiredInputFrames(outputFrames, rate);
        var scratch = EnsureScratch(requiredFrames * _channels);

        var available = _buffer.ReadRaw(scratch.AsSpan(0, requiredFrames * _channels), now);
        if (available <= 0)
        {
            return 0;
        }

        var written = _resampler.Resample(
            scratch.AsSpan(0, available), output, rate, out var consumed);

        // Tell the buffer the rate we actually played at, so its own sync accounting matches
        // reality rather than assuming 1.0.
        _buffer.ReportExternalPlaybackRate(rate);

        if (consumed > available)
        {
            _logger.LogWarning("Resampler reported consuming {Consumed} of {Available} samples", consumed, available);
        }

        return written;
    }

    /// <summary>
    /// Reads with frame drop or insert, for errors the rate ceiling cannot close in reasonable
    /// time.
    /// </summary>
    /// <remarks>
    /// Both operations interpolate rather than duplicating or hard-cutting a frame: blending
    /// the two frames either side of the edit turns a step discontinuity into a much quieter
    /// one. It is still audible on a transient, which is exactly why this path is reserved for
    /// large errors.
    /// </remarks>
    private int ReadWithDropInsert(Span<float> output, long now)
    {
        var dropEveryN = _correctionProvider.DropEveryNFrames;
        var insertEveryN = _correctionProvider.InsertEveryNFrames;

        if (dropEveryN == 0 && insertEveryN == 0)
        {
            return ReadDirect(output, now);
        }

        // A drop consumes two input frames per output frame, so at worst the input needed is
        // double the output. Sizing for that avoids a second read part-way through.
        var scratch = EnsureScratch(output.Length * 2);
        var available = _buffer.ReadRaw(scratch.AsSpan(0, output.Length * 2), now);
        if (available <= 0)
        {
            return 0;
        }

        var inputPosition = 0;
        var outputPosition = 0;
        var dropped = 0;
        var inserted = 0;

        while (outputPosition + _channels <= output.Length)
        {
            _framesSinceCorrection++;

            var remaining = available - inputPosition;
            var target = output.Slice(outputPosition, _channels);

            if (dropEveryN > 0 && _framesSinceCorrection >= dropEveryN && remaining >= _channels * 2)
            {
                _framesSinceCorrection = 0;

                for (var channel = 0; channel < _channels; channel++)
                {
                    target[channel] = (scratch[inputPosition + channel]
                                       + scratch[inputPosition + _channels + channel]) * 0.5f;
                }

                inputPosition += _channels * 2;
                dropped += _channels;
            }
            else if (insertEveryN > 0 && _framesSinceCorrection >= insertEveryN)
            {
                _framesSinceCorrection = 0;

                if (remaining >= _channels)
                {
                    for (var channel = 0; channel < _channels; channel++)
                    {
                        target[channel] = (_correctionFrame[channel] + scratch[inputPosition + channel]) * 0.5f;
                    }
                }
                else
                {
                    _correctionFrame.CopyTo(target);
                }

                inserted += _channels;
            }
            else
            {
                if (remaining < _channels)
                {
                    break;
                }

                scratch.AsSpan(inputPosition, _channels).CopyTo(target);
                inputPosition += _channels;
            }

            target.CopyTo(_correctionFrame);
            outputPosition += _channels;
        }

        if (dropped > 0 || inserted > 0)
        {
            _buffer.NotifyExternalCorrection(dropped, inserted);
            _totalSamplesDropped += dropped;
            _totalSamplesInserted += inserted;
        }

        return outputPosition;
    }

    /// <summary>
    /// Returns the scratch buffer, growing it only when a request genuinely needs more.
    /// </summary>
    /// <remarks>
    /// Growth allocates on the render path, which is why it is logged: on a fixed-size audio
    /// path it should happen on the first call and never again. Repeated growth means the
    /// backend is varying its request size and the buffer should be sized from the maximum.
    /// </remarks>
    private float[] EnsureScratch(int required)
    {
        if (_scratch.Length >= required)
        {
            return _scratch;
        }

        _logger.LogInformation(
            "Growing sync scratch buffer from {Old} to {New} samples", _scratch.Length, required);
        _scratch = new float[required];
        return _scratch;
    }

    private void LogPeriodically(long now, SyncCorrectionMode mode)
    {
        if (now - _lastLogTimestamp < LogIntervalMicroseconds)
        {
            return;
        }

        _lastLogTimestamp = now;

        _logger.LogDebug(
            "Sync: error {Error:+0.000;-0.000} ms, mode {Mode}, rate {Rate:F6}, buffered {Buffered:F1} ms, " +
            "timing {TimingSource}, dropped {Dropped}, inserted {Inserted}",
            _buffer.SmoothedSyncErrorMicroseconds / 1000.0,
            mode,
            _correctionProvider.TargetPlaybackRate,
            _buffer.BufferedMilliseconds,
            _buffer.TimingSourceName,
            _totalSamplesDropped,
            _totalSamplesInserted);
    }
}
