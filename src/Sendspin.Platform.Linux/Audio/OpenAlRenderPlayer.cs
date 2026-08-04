using Microsoft.Extensions.Logging;
using Sendspin.Core.Audio;
using Sendspin.Platform.Shared.Audio;
using Sendspin.SDK.Models;
using Silk.NET.OpenAL;

namespace Sendspin.Platform.Linux.Audio;

/// <summary>
/// Renders to OpenAL Soft from a dedicated thread, reporting the device's real latency and
/// clock through the hand-bound <see cref="AlSoftExtensions"/>.
/// </summary>
/// <remarks>
/// <para>
/// OpenAL has no callback: nothing asks this player for audio, so the player owns a thread that
/// keeps a streaming buffer queue topped up. That thread is an ordinary managed thread and is
/// therefore subject to garbage collection pauses, which makes queue depth the only mitigation
/// available — see <see cref="BufferCount"/>.
/// </para>
/// <para>
/// <strong>One player is current at a time.</strong> <c>alcMakeContextCurrent</c> sets a
/// process-wide current context, so opening a second device from a second instance takes the
/// context away from the first. The SDK's pipeline creates one player per stream and disposes it
/// before the next, which is why that is acceptable here rather than something to work around
/// with <c>ALC_EXT_thread_local_context</c> — a thread-local context would break every AL call
/// this class makes from outside the render thread.
/// </para>
/// <para>
/// <strong>Volume.</strong> <see cref="AudioPlayerBase.EffectiveGain"/> is already curved by the
/// SDK, so it goes straight into <c>AL_GAIN</c> as a linear multiplier. There is deliberately no
/// exponentiation anywhere in this file.
/// </para>
/// </remarks>
public sealed unsafe class OpenAlRenderPlayer : AudioPlayerBase
{
    /// <summary>
    /// Milliseconds of audio per queued buffer.
    /// </summary>
    /// <remarks>
    /// 20 ms is one refill decision per buffer and a granularity fine enough that the queue can
    /// be topped up long before it empties, while being coarse enough that the per-buffer cost
    /// (one <c>alBufferData</c> copy and two queue operations) stays irrelevant.
    /// </remarks>
    private const int BufferMilliseconds = 20;

    /// <summary>
    /// Buffers in the streaming queue: 6 × 20 ms = <strong>120 ms of queued audio</strong>.
    /// </summary>
    /// <remarks>
    /// The render thread is managed, so a Gen2 collection can stop it outright. Workstation
    /// background GC pauses are typically under 20 ms but are not bounded, and a compacting Gen2
    /// on a heap this size has been measured in the 30-60 ms range. 120 ms of queued audio is the
    /// mitigation: the device keeps draining that queue through the pause, so anything shorter
    /// than the full depth is inaudible rather than a dropout. Depth is not free — it is delay
    /// between reading a sample and hearing it — but it is reported honestly through
    /// <see cref="MeasuredOutputLatencyMs"/> rather than hidden, so the sync loop accounts for it.
    /// </remarks>
    private const int BufferCount = 6;

    /// <summary>
    /// How long the render thread waits between refill passes when there is nothing to do.
    /// </summary>
    /// <remarks>
    /// Half a buffer period, so at most half a period of refill opportunity can be missed. This
    /// is a bounded wait on an event rather than a sleep, which is what lets a stop take effect
    /// immediately instead of after the timeout.
    /// </remarks>
    private const int IdleWaitMilliseconds = BufferMilliseconds / 2;

    /// <summary>
    /// Refill passes between device-latency samples: 25 × 10 ms ≈ 250 ms.
    /// </summary>
    /// <remarks>
    /// Latency is re-read rather than cached because it is not a constant on this platform: any
    /// other client asking PipeWire for a smaller quantum re-negotiates it for everyone.
    /// </remarks>
    private const int LatencySampleInterval = 25;

    /// <summary>
    /// Weight given to a fresh latency sample. Smoothed because the figure feeds diagnostics and
    /// our own scheduling, both of which want a stable number rather than per-quantum jitter.
    /// </summary>
    private const double LatencySmoothing = 0.25;

    private const long NanosecondsPerSecond = 1_000_000_000L;
    private const long NanosecondsPerMillisecond = 1_000_000L;

    /// <summary>
    /// How long <see cref="StopRendering"/> waits for the render thread before giving up on it.
    /// </summary>
    private const int ThreadJoinTimeoutMilliseconds = 1_000;

    /// <summary>
    /// <c>AL_DIRECT_CHANNELS_SOFT</c>. Routes a stereo buffer's channels straight to the output
    /// pair, bypassing the 3D panner, so playback is a bit-exact passthrough of what the server
    /// sent. Not in Silk.NET's enums; harmless on a driver that does not know it.
    /// </summary>
    private const int AlDirectChannelsSoft = 0x1033;

    /// <summary>
    /// <c>AL_SOURCE_SPATIALIZE_SOFT</c>. Same purpose: never spatialise a music stream.
    /// </summary>
    private const int AlSourceSpatializeSoft = 0x1214;

    private readonly Lock _deviceGate = new();
    private readonly ManualResetEventSlim _stopSignal = new(initialState: false, spinCount: 0);
    private readonly uint[] _buffers = new uint[BufferCount];
    private readonly uint[] _freeBuffers = new uint[BufferCount];

    private AL? _al;
    private ALContext? _alc;
    private Device* _device;
    private Context* _context;
    private uint _source;
    private int _freeBufferCount;

    private AlSoftExtensions? _extensions;
    private Thread? _renderThread;

    private float[] _sampleBuffer = [];
    private short[] _pcmBuffer = [];

    private int _sampleRate;
    private BufferFormat _bufferFormat;
    private int _deviceLatencyMilliseconds;
    private int _queuedBuffers;
    private double _smoothedDeviceLatencyMilliseconds;
    private bool _inUnderrun;

    public OpenAlRenderPlayer(ILogger<OpenAlRenderPlayer> logger)
        : base(logger)
    {
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Honest about which clock is actually in use: <c>audio-clock</c> only when
    /// <c>ALC_SOFT_device_clock</c> resolved, because otherwise
    /// <see cref="TryReadDeviceClock"/> returns null and the SDK is running on a filtered wall
    /// clock whatever this property claims.
    /// </remarks>
    public override string TimingSourceName =>
        _extensions?.HasDeviceClock == true ? "audio-clock" : "wall-clock";

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Two parts, both measured. The device's own latency comes from
    /// <c>ALC_DEVICE_CLOCK_LATENCY_SOFT</c> (or <c>AL_SAMPLE_OFFSET_LATENCY_SOFT</c>), sampled
    /// continuously by the render thread. Added to it is the audio actually sitting in the source's
    /// queue ahead of the device, read from <c>AL_BUFFERS_QUEUED</c> on each pass.
    /// </para>
    /// <para>
    /// The queue depth is observed rather than assumed to be <see cref="BufferCount"/> - 1. In
    /// steady state it is that, so assuming it would usually be right — but it is wrong exactly
    /// when the number matters most: during prefill, and after an underrun, when the queue is not
    /// full and a fixed term would overstate the latency by up to the whole buffer depth.
    /// </para>
    /// <para>
    /// When the extensions are absent the device term falls back to one buffer period as a
    /// stand-in, which is logged as a fallback at open time and makes
    /// <see cref="TimingSourceName"/> report <c>wall-clock</c>.
    /// </para>
    /// </remarks>
    protected override int MeasuredOutputLatencyMs =>
        QueuedAheadMilliseconds + Volatile.Read(ref _deviceLatencyMilliseconds);

    /// <summary>
    /// Gets the audio sitting in the source queue ahead of the device, in milliseconds.
    /// </summary>
    /// <remarks>
    /// A queued-but-unplayed buffer is one period of audio the device has not reached yet, so the
    /// queue holds <c>queued - 1</c> periods beyond the one being played. Clamped at zero for the
    /// moment before the first buffer is queued.
    /// </remarks>
    private int QueuedAheadMilliseconds =>
        Math.Max(0, Volatile.Read(ref _queuedBuffers) - 1) * BufferMilliseconds;

    /// <inheritdoc/>
    protected override Task OpenDeviceAsync(AudioFormat format, string? deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (format.Channels is not (1 or 2))
        {
            throw new NotSupportedException(
                $"OpenAL can queue mono or stereo 16-bit buffers; the stream has {format.Channels} channels.");
        }

        lock (_deviceGate)
        {
            // Anything that throws part-way through leaves a device and possibly a context open,
            // and AudioPlayerBase.InitializeAsync reports the failure and rethrows without calling
            // CloseDevice — so the unwind has to happen here or the handles leak for the lifetime
            // of the process.
            var opened = false;

            try
            {
                _sampleRate = format.SampleRate;
                _bufferFormat = format.Channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16;

                var al = AL.GetApi();
                var alc = ALContext.GetApi();
                _al = al;
                _alc = alc;

                _device = alc.OpenDevice(deviceId);
                if (_device is null)
                {
                    throw new InvalidOperationException(
                        $"OpenAL could not open audio device '{deviceId ?? "system default"}'.");
                }

                // Ask the mixer to run at the stream's rate. When it can, OpenAL's own resampler is
                // out of the path entirely; when it cannot, the resampler's delay is included in the
                // latency the device reports, so either way the figure stays honest.
                var attributes = stackalloc int[] { (int)ContextAttributes.Frequency, format.SampleRate, 0 };
                _context = alc.CreateContext(_device, attributes);
                if (_context is null)
                {
                    var error = alc.GetError(_device);
                    throw new InvalidOperationException($"OpenAL could not create a context: {error}.");
                }

                if (!alc.MakeContextCurrent(_context))
                {
                    throw new InvalidOperationException("OpenAL could not make the new context current.");
                }

                _source = al.GenSource();
                fixed (uint* buffers = _buffers)
                {
                    al.GenBuffers(BufferCount, buffers);
                }

                _buffers.CopyTo(_freeBuffers, 0);
                _freeBufferCount = BufferCount;

                var framesPerBuffer = format.SampleRate * BufferMilliseconds / 1_000;
                var samplesPerBuffer = framesPerBuffer * format.Channels;
                _sampleBuffer = new float[samplesPerBuffer];
                _pcmBuffer = new short[samplesPerBuffer];

                DisableSpatialisation(al, format.Channels);
                ApplyGain(al);

                _extensions = new AlSoftExtensions(al, alc, _device, Logger);
                SeedDeviceLatency(_extensions);

                opened = true;
            }
            finally
            {
                if (!opened)
                {
                    ReleaseDeviceResources();
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override void CloseDevice()
    {
        lock (_deviceGate)
        {
            ReleaseDeviceResources();
        }
    }

    /// <summary>
    /// Releases whatever OpenAL handles are currently held. Safe to call on a partly-opened device,
    /// and safe to call twice.
    /// </summary>
    /// <remarks>
    /// Deliberately does not check the AL error state. This runs both on ordinary teardown and on
    /// the unwind from a failed open; an error reported while deleting a handle is not actionable,
    /// and raising it from the unwind path would replace the exception that actually explains the
    /// failure. The caller must hold <c>_deviceGate</c>.
    /// </remarks>
    private void ReleaseDeviceResources()
    {
        var al = _al;
        var alc = _alc;

        if (al is not null)
        {
            if (_source != 0)
            {
                al.DeleteSource(_source);
                _source = 0;
            }

            if (_buffers[0] != 0)
            {
                fixed (uint* buffers = _buffers)
                {
                    al.DeleteBuffers(BufferCount, buffers);
                }

                Array.Clear(_buffers);
            }

            _freeBufferCount = 0;
        }

        if (alc is not null)
        {
            if (_context is not null)
            {
                alc.MakeContextCurrent(null);
                alc.DestroyContext(_context);
                _context = null;
            }

            if (_device is not null)
            {
                alc.CloseDevice(_device);
                _device = null;
            }
        }

        _extensions = null;
        al?.Dispose();
        alc?.Dispose();
        _al = null;
        _alc = null;
    }

    /// <inheritdoc/>
    protected override void StartRendering()
    {
        lock (_deviceGate)
        {
            if (_renderThread is not null)
            {
                return;
            }

            if (_al is null || _source == 0)
            {
                throw new InvalidOperationException("StartRendering called before the device was opened.");
            }

            _stopSignal.Reset();

            _renderThread = new Thread(RenderLoop)
            {
                Name = "sendspin-openal-render",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };

            _renderThread.Start();
        }
    }

    /// <inheritdoc/>
    protected override void StopRendering(bool flush)
    {
        Thread? thread;

        lock (_deviceGate)
        {
            thread = _renderThread;
            _renderThread = null;
        }

        _stopSignal.Set();

        var stopped = true;
        if (thread is not null && thread.IsAlive)
        {
            stopped = thread.Join(ThreadJoinTimeoutMilliseconds);
            if (!stopped)
            {
                Logger.LogWarning(
                    "OpenAL render thread did not stop within {TimeoutMs} ms; leaving the queue " +
                    "untouched rather than racing it",
                    ThreadJoinTimeoutMilliseconds);
            }
        }

        if (!stopped)
        {
            return;
        }

        lock (_deviceGate)
        {
            var al = _al;
            if (al is null || _source == 0)
            {
                return;
            }

            if (flush)
            {
                al.SourceStop(_source);
                ReclaimQueuedBuffers(al);
            }
            else
            {
                // Paused, not stopped: the queue stays as it is so a resume continues from the
                // same sample rather than re-prefilling and losing the audio already buffered.
                al.SourcePause(_source);
            }

            CheckError(al, flush ? "alSourceStop" : "alSourcePause");
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Frames come from the <em>device clock</em> rather than from the source's play position,
    /// converted to frames through the stream's sample rate. The device clock advances at the
    /// rate the DAC consumes samples and keeps advancing through an underrun, which is what a
    /// clock has to do; a source offset stalls when the queue starves, and a stalled "now" would
    /// read as a growing sync error and provoke a resync that fixes nothing.
    /// </para>
    /// <para>
    /// The host time is the midpoint of two <c>CLOCK_MONOTONIC</c> reads taken either side of the
    /// device query, because OpenAL's clock is its mixer's and cannot be compared with a host
    /// instant it was not paired with. It is reported on <c>CLOCK_MONOTONIC</c> and left there:
    /// per <see cref="AudioClockReading"/>, the platform reports its own timebase and
    /// <see cref="DeviceAnchoredClock"/> owns the conversion to the SDK's timeline.
    /// </para>
    /// </remarks>
    protected override AudioClockReading? TryReadDeviceClock()
    {
        var extensions = _extensions;
        if (extensions is null || !extensions.HasDeviceClock)
        {
            return null;
        }

        if (!extensions.TryReadDeviceClock(out var clockNanoseconds, out _, out var hostTimeMicroseconds))
        {
            return null;
        }

        return new AudioClockReading(NanosecondsToFrames(clockNanoseconds, _sampleRate), hostTimeMicroseconds);
    }

    /// <inheritdoc/>
    protected override void OnGainChanged()
    {
        lock (_deviceGate)
        {
            var al = _al;
            if (al is not null && _source != 0)
            {
                ApplyGain(al);
            }
        }
    }

    /// <inheritdoc/>
    protected override ValueTask DisposeCoreAsync()
    {
        _stopSignal.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Converts a nanosecond instant to whole frames at <paramref name="sampleRate"/>.
    /// </summary>
    /// <remarks>
    /// Split into seconds and remainder rather than multiplying first, which would overflow
    /// <see cref="long"/> for any clock past a couple of days of uptime.
    /// </remarks>
    private static long NanosecondsToFrames(long nanoseconds, int sampleRate)
    {
        if (sampleRate <= 0)
        {
            return 0;
        }

        var (seconds, remainder) = Math.DivRem(nanoseconds, NanosecondsPerSecond);
        return (seconds * sampleRate) + (remainder * sampleRate / NanosecondsPerSecond);
    }

    private static void ConvertToInt16(ReadOnlySpan<float> samples, Span<short> destination)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            destination[i] = (short)(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
        }
    }

    /// <summary>
    /// Keeps the buffer queue full until asked to stop.
    /// </summary>
    private void RenderLoop()
    {
        AL al;
        uint source;
        AlSoftExtensions? extensions;

        lock (_deviceGate)
        {
            if (_al is null || _source == 0)
            {
                return;
            }

            al = _al;
            source = _source;
            extensions = _extensions;
        }

        try
        {
            // Everything the loop touches was allocated in OpenDeviceAsync, so from here on it
            // allocates nothing and takes no lock.
            PrefillQueue(al, source);
            StartSource(al, source, extensions);

            var passesUntilLatencySample = LatencySampleInterval;

            while (!_stopSignal.IsSet)
            {
                RecycleProcessedBuffers(al, source);
                ObserveQueueAndResume(al, source);

                if (--passesUntilLatencySample <= 0)
                {
                    passesUntilLatencySample = LatencySampleInterval;
                    SampleDeviceLatency(extensions, al, source);
                }

                _stopSignal.Wait(IdleWaitMilliseconds);
            }
        }
        catch (Exception ex)
        {
            // Broad on purpose: an exception escaping a background thread ends the process, so
            // the render thread reports and exits instead. The failure is surfaced through
            // ErrorOccurred, not swallowed.
            Fail(AudioPlayerErrorCode.DeviceLost, "The OpenAL render loop stopped unexpectedly", ex);
        }
    }

    private void PrefillQueue(AL al, uint source)
    {
        while (_freeBufferCount > 0)
        {
            var buffer = _freeBuffers[--_freeBufferCount];
            FillAndQueue(al, source, buffer);
        }
    }

    /// <summary>
    /// Starts playback, scheduled to an exact device-clock instant where the driver supports it.
    /// </summary>
    /// <remarks>
    /// <c>AL_SOFT_source_start_delay</c> lets the start be a known instant one buffer period
    /// ahead instead of "whenever the mixer next runs", which removes up to a full mix period of
    /// startup jitter. The lead is a fixed period rather than an alignment to a server timestamp
    /// because the render contract hands this player no target time; the SDK measures the
    /// residual startup offset itself. Without the extension the source simply starts now.
    /// </remarks>
    private void StartSource(AL al, uint source, AlSoftExtensions? extensions)
    {
        if (extensions is not null &&
            extensions.HasScheduledStart &&
            extensions.TryReadDeviceClock(out var clockNanoseconds, out _, out _) &&
            extensions.TryPlayAtDeviceTime(source, clockNanoseconds + (BufferMilliseconds * NanosecondsPerMillisecond)))
        {
            return;
        }

        al.SourcePlay(source);
    }

    private void RecycleProcessedBuffers(AL al, uint source)
    {
        al.GetSourceProperty(source, GetSourceInteger.BuffersProcessed, out var processed);

        while (processed-- > 0 && !_stopSignal.IsSet)
        {
            uint buffer;
            al.SourceUnqueueBuffers(source, 1, &buffer);
            FillAndQueue(al, source, buffer);
        }
    }

    /// <summary>
    /// Publishes the queue depth, and restarts a source the device stopped because the queue ran
    /// dry.
    /// </summary>
    /// <remarks>
    /// The depth is read on every pass, not only when starved: it is one of the two terms in
    /// <see cref="MeasuredOutputLatencyMs"/>, so a value that only refreshed on an underrun would
    /// report a stale latency for the whole of normal playback.
    /// </remarks>
    private void ObserveQueueAndResume(AL al, uint source)
    {
        al.GetSourceProperty(source, GetSourceInteger.BuffersQueued, out var queued);
        Volatile.Write(ref _queuedBuffers, queued);

        al.GetSourceProperty(source, GetSourceInteger.SourceState, out var state);
        if ((SourceState)state == SourceState.Playing)
        {
            return;
        }

        if (queued > 0 && !_stopSignal.IsSet)
        {
            al.SourcePlay(source);
        }
    }

    private void FillAndQueue(AL al, uint source, uint buffer)
    {
        var read = SampleSource?.Read(_sampleBuffer, 0, _sampleBuffer.Length) ?? 0;

        if (read < _sampleBuffer.Length)
        {
            // Short read: pad with silence rather than queueing a shorter buffer, so one starved
            // pass costs a gap of known length instead of shifting every later buffer earlier.
            _pcmBuffer.AsSpan(read).Clear();
        }

        if (read > 0)
        {
            ConvertToInt16(_sampleBuffer.AsSpan(0, read), _pcmBuffer);
        }

        ReportUnderrun(read == 0);

        fixed (short* pcm = _pcmBuffer)
        {
            al.BufferData(buffer, _bufferFormat, pcm, _pcmBuffer.Length * sizeof(short), _sampleRate);
        }

        al.SourceQueueBuffers(source, 1, &buffer);
    }

    private void ReportUnderrun(bool starved)
    {
        if (starved == _inUnderrun)
        {
            return;
        }

        _inUnderrun = starved;

        if (starved)
        {
            Logger.LogWarning("Audio source produced no samples; queueing {BufferMs} ms of silence",
                BufferMilliseconds);
        }
        else
        {
            Logger.LogInformation("Audio source recovered; queue refilling normally");
        }
    }

    /// <summary>
    /// Takes a fresh device-latency sample and folds it into the reported figure.
    /// </summary>
    private void SampleDeviceLatency(AlSoftExtensions? extensions, AL al, uint source)
    {
        if (extensions is null)
        {
            return;
        }

        long latencyNanoseconds;

        if (extensions.HasDeviceClock)
        {
            if (!extensions.TryReadDeviceLatencyNanoseconds(out latencyNanoseconds))
            {
                return;
            }
        }
        else if (extensions.HasSourceLatency)
        {
            if (!extensions.TryReadSourceLatency(source, out _, out latencyNanoseconds))
            {
                return;
            }
        }
        else
        {
            return;
        }

        CheckError(al, "latency query");

        var milliseconds = latencyNanoseconds / (double)NanosecondsPerMillisecond;
        _smoothedDeviceLatencyMilliseconds =
            (_smoothedDeviceLatencyMilliseconds * (1 - LatencySmoothing)) + (milliseconds * LatencySmoothing);

        Volatile.Write(ref _deviceLatencyMilliseconds, (int)Math.Round(_smoothedDeviceLatencyMilliseconds));
    }

    /// <summary>
    /// Establishes the first latency figure so the value logged at initialisation is real, and
    /// records plainly when it is not.
    /// </summary>
    private void SeedDeviceLatency(AlSoftExtensions extensions)
    {
        if (extensions.TryReadDeviceLatencyNanoseconds(out var latencyNanoseconds))
        {
            SetDeviceLatency(latencyNanoseconds / (double)NanosecondsPerMillisecond);
            return;
        }

        // One buffer period stands in for the device tail nobody will tell us about. It is a
        // stand-in, not a measurement, so it is logged as one.
        SetDeviceLatency(BufferMilliseconds);

        if (extensions.HasSourceLatency)
        {
            Logger.LogInformation(
                "No ALC_SOFT_device_clock on this driver: latency will come from " +
                "AL_SOFT_source_latency once playback starts, and timing falls back to the wall clock");
            return;
        }

        Logger.LogWarning(
            "No OpenAL Soft latency extension on this driver: reporting the buffer-derived estimate " +
            "of {LatencyMs} ms and timing from the wall clock",
            QueuedAheadMilliseconds + BufferMilliseconds);
    }

    private void SetDeviceLatency(double milliseconds)
    {
        _smoothedDeviceLatencyMilliseconds = milliseconds;
        Volatile.Write(ref _deviceLatencyMilliseconds, (int)Math.Round(milliseconds));
    }

    private void ReclaimQueuedBuffers(AL al)
    {
        al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out var queued);

        while (queued-- > 0)
        {
            uint buffer;
            al.SourceUnqueueBuffers(_source, 1, &buffer);

            var error = al.GetError();
            if (error != AudioError.NoError)
            {
                Logger.LogDebug("Stopped unqueueing OpenAL buffers after {Error}", error);
                break;
            }

            _freeBuffers[_freeBufferCount++] = buffer;
        }
    }

    /// <summary>
    /// Takes the 3D pipeline out of the path for a stereo stream.
    /// </summary>
    /// <remarks>
    /// Only for stereo: a mono buffer with direct channels set is routed to front-centre, which
    /// on a two-speaker output is not what a listener expects.
    /// </remarks>
    private void DisableSpatialisation(AL al, int channels)
    {
        al.SetSourceProperty(_source, (SourceInteger)AlSourceSpatializeSoft, 0);

        if (channels == 2)
        {
            al.SetSourceProperty(_source, (SourceInteger)AlDirectChannelsSoft, 1);
        }

        // Both are extensions; a driver without them answers AL_INVALID_ENUM, which is not a
        // failure worth reporting, only worth clearing so it is not mistaken for a later error.
        CheckError(al, "spatialisation hints");
    }

    private void ApplyGain(AL al)
    {
        al.SetSourceProperty(_source, SourceFloat.Gain, EffectiveGain);
    }

    private void CheckError(AL al, string operation)
    {
        var error = al.GetError();
        if (error != AudioError.NoError)
        {
            Logger.LogDebug("OpenAL reported {Error} after {Operation}", error, operation);
        }
    }
}
