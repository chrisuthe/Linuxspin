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
/// This is the SDK's sanctioned path: <see cref="ITimedAudioBuffer.Read"/> is obsolete in favour of
/// <see cref="ITimedAudioBuffer.ReadRaw"/> plus an external provider, so that the embedder controls
/// how corrections are realised.
/// </para>
/// <para>
/// It honours all three of the provider's modes. Rate changes go through
/// <see cref="DriftResampler"/>; frame drop and insert are reserved for errors too large for the
/// rate ceiling to close in reasonable time.
/// </para>
/// <para>
/// <strong>Every sample taken from the buffer is accounted for.</strong> Two of the three modes need
/// to read further ahead than they emit — the resampler needs a lookahead frame, and a drop consumes
/// two input frames per output frame — so a read cannot simply be sized to the output and it cannot
/// discard what it did not use. Anything left over stays in <see cref="_pending"/> and is the first
/// thing consumed next call. Getting this wrong does not throw and does not sound obviously broken:
/// it plays a fraction of the stream at the wrong speed, which is close to impossible to attribute
/// after the fact. <c>SyncCorrectedSampleSourceTests</c> asserts the accounting per mode.
/// </para>
/// <para>
/// <strong>Realtime discipline.</strong> <see cref="Read"/> runs on the render path. It allocates
/// nothing after the first call: both buffers are sized once from the first request and reused. It
/// takes no lock. Growing a buffer is logged, because on a fixed-size audio path it should happen
/// once and never again.
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

    /// <summary>
    /// Input read from the buffer but not yet played, kept across calls.
    /// </summary>
    private float[] _pending = [];
    private int _pendingCount;

    private float[] _correctionFrame;
    private int _framesSinceCorrection;
    private long _totalSamplesDropped;
    private long _totalSamplesInserted;
    private long _totalSamplesShort;
    private long _lastLogTimestamp;
    private bool _inUnderrun;
    private bool _disposed;

    /// <summary>
    /// How often correction state is logged. Once a second: often enough to watch a convergence,
    /// rare enough not to be the reason the render path misses a deadline.
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

    /// <summary>
    /// Gets the total samples this source has had to silence because the buffer ran dry.
    /// </summary>
    /// <remarks>
    /// Surfaced here because this is the only place that knows. <see cref="Read"/> always returns a
    /// full block — the device must never be handed a partly-filled buffer — so a backend cannot
    /// detect an underrun from the return value, and one that tries will silently never see one.
    /// </remarks>
    public long TotalSamplesShort => Volatile.Read(ref _totalSamplesShort);

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
            // Underrun. Silence rather than stale audio, and the device still gets a full buffer so
            // it does not glitch on top of the shortfall.
            output[written..].Clear();
            RecordUnderrun(count - written);
        }
        else if (_inUnderrun)
        {
            _inUnderrun = false;
            _logger.LogInformation("Audio buffer recovered; playing continuously again");
        }

        if (_correctionProvider is SyncCorrectionCalculator calculator)
        {
            calculator.NotifySamplesProcessed(written);
        }

        LogPeriodically(now, mode);

        // Always report the full request: the backend asked for a fixed-size buffer and has been
        // given one.
        return count;
    }

    /// <summary>
    /// Discards correction, interpolation and pending-input state. Call after a buffer clear or a
    /// re-anchor, so nothing afterwards is stitched onto audio from before the discontinuity.
    /// </summary>
    public void Reset()
    {
        _framesSinceCorrection = 0;
        _pendingCount = 0;
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
        _pending = [];
        _pendingCount = 0;
    }

    /// <summary>
    /// Copies straight through, at one input frame per output frame.
    /// </summary>
    private int ReadDirect(Span<float> output, long now)
    {
        var available = FillPending(output.Length, now);
        var toCopy = Math.Min(output.Length, available);

        if (toCopy <= 0)
        {
            return 0;
        }

        _pending.AsSpan(0, toCopy).CopyTo(output);
        RememberLastFrame(output[..toCopy]);
        ConsumePending(toCopy);

        return toCopy;
    }

    /// <summary>
    /// Reads at the provider's target rate, resampling to fill the request.
    /// </summary>
    private int ReadResampled(Span<float> output, long now)
    {
        var rate = _correctionProvider.TargetPlaybackRate;

        if (double.IsNaN(rate) || rate <= 0.0)
        {
            return ReadDirect(output, now);
        }

        var outputFrames = output.Length / _channels;
        var wanted = _resampler.RequiredInputFrames(outputFrames, rate) * _channels;
        var available = FillPending(wanted, now);

        if (available <= 0)
        {
            return 0;
        }

        var written = _resampler.Resample(
            _pending.AsSpan(0, available), output, rate, out var consumed);

        // Tell the buffer the rate actually played at, so its own sync accounting matches reality
        // rather than assuming 1.0.
        _buffer.ReportExternalPlaybackRate(rate);

        if (written > 0)
        {
            RememberLastFrame(output[..written]);
        }

        // Only the frames the resampler consumed leave the pending buffer; its lookahead stays for
        // the next call.
        ConsumePending(consumed);

        return written;
    }

    /// <summary>
    /// Reads with frame drop or insert, for errors the rate ceiling cannot close in reasonable time.
    /// </summary>
    /// <remarks>
    /// Both operations interpolate rather than duplicating or hard-cutting a frame: blending the two
    /// frames either side of the edit turns a step discontinuity into a much quieter one. It is
    /// still audible on a transient, which is why this path is reserved for large errors.
    /// </remarks>
    private int ReadWithDropInsert(Span<float> output, long now)
    {
        var dropEveryN = _correctionProvider.DropEveryNFrames;
        var insertEveryN = _correctionProvider.InsertEveryNFrames;

        if (dropEveryN == 0 && insertEveryN == 0)
        {
            return ReadDirect(output, now);
        }

        var outputFrames = output.Length / _channels;

        // A drop consumes two input frames for one output frame, so the worst case is one extra
        // frame per drop interval — not a whole extra block. Requesting only what can be consumed
        // is what keeps the accounting honest; an insert consumes less, and the surplus stays
        // pending either way.
        var extraFrames = dropEveryN > 0 ? (outputFrames / dropEveryN) + 1 : 0;
        var wanted = (outputFrames + extraFrames) * _channels;
        var available = FillPending(wanted, now);

        if (available <= 0)
        {
            return 0;
        }

        var pending = _pending;
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
                    target[channel] = (pending[inputPosition + channel]
                                       + pending[inputPosition + _channels + channel]) * 0.5f;
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
                        target[channel] = (_correctionFrame[channel] + pending[inputPosition + channel]) * 0.5f;
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

                pending.AsSpan(inputPosition, _channels).CopyTo(target);
                inputPosition += _channels;
            }

            target.CopyTo(_correctionFrame);
            outputPosition += _channels;
        }

        ConsumePending(inputPosition);

        if (dropped > 0 || inserted > 0)
        {
            _buffer.NotifyExternalCorrection(dropped, inserted);
            _totalSamplesDropped += dropped;
            _totalSamplesInserted += inserted;
        }

        return outputPosition;
    }

    /// <summary>
    /// Tops the pending buffer up to <paramref name="wanted"/> samples from the timed buffer, and
    /// returns how many are now available.
    /// </summary>
    private int FillPending(int wanted, long now)
    {
        EnsurePendingCapacity(wanted);

        if (_pendingCount < wanted)
        {
            var read = _buffer.ReadRaw(_pending.AsSpan(_pendingCount, wanted - _pendingCount), now);
            _pendingCount += read;
        }

        return _pendingCount;
    }

    /// <summary>
    /// Drops the first <paramref name="samples"/> samples from the pending buffer, keeping the rest
    /// for the next call.
    /// </summary>
    private void ConsumePending(int samples)
    {
        if (samples <= 0)
        {
            return;
        }

        var remaining = _pendingCount - samples;

        if (remaining > 0)
        {
            _pending.AsSpan(samples, remaining).CopyTo(_pending);
        }

        _pendingCount = Math.Max(0, remaining);
    }

    /// <summary>
    /// Keeps the last frame emitted, which an inserted frame interpolates from.
    /// </summary>
    private void RememberLastFrame(ReadOnlySpan<float> written)
    {
        if (written.Length >= _channels)
        {
            written[^_channels..].CopyTo(_correctionFrame);
        }
    }

    /// <summary>
    /// Grows the pending buffer, only when a request genuinely needs more.
    /// </summary>
    /// <remarks>
    /// Growth allocates on the render path, which is why it is logged: on a fixed-size audio path it
    /// should happen on the first call and never again. Repeated growth means the backend is varying
    /// its request size, and the buffer should be sized from the maximum instead.
    /// </remarks>
    private void EnsurePendingCapacity(int required)
    {
        if (_pending.Length >= required)
        {
            return;
        }

        _logger.LogInformation(
            "Growing sync input buffer from {Old} to {New} samples", _pending.Length, required);

        var grown = new float[required];
        _pending.AsSpan(0, _pendingCount).CopyTo(grown);
        _pending = grown;
    }

    /// <summary>
    /// Records a shortfall, logging the transition into underrun once rather than per block.
    /// </summary>
    private void RecordUnderrun(int missingSamples)
    {
        Volatile.Write(ref _totalSamplesShort, _totalSamplesShort + missingSamples);

        if (_inUnderrun)
        {
            return;
        }

        _inUnderrun = true;
        _logger.LogWarning(
            "Audio buffer underrun: {Missing} samples silenced, {Buffered:F1} ms buffered",
            missingSamples, _buffer.BufferedMilliseconds);
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
            "timing {TimingSource}, dropped {Dropped}, inserted {Inserted}, silenced {Silenced}",
            _buffer.SmoothedSyncErrorMicroseconds / 1000.0,
            mode,
            _correctionProvider.TargetPlaybackRate,
            _buffer.BufferedMilliseconds,
            _buffer.TimingSourceName,
            _totalSamplesDropped,
            _totalSamplesInserted,
            _totalSamplesShort);
    }
}
