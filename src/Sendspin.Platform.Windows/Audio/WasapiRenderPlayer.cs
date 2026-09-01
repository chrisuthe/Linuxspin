using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sendspin.Core.Audio;
using Sendspin.Platform.Shared.Audio;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace Sendspin.Platform.Windows.Audio;

/// <summary>
/// Renders to a WASAPI shared-mode endpoint from a dedicated thread, and reports the endpoint's
/// own frame position paired with the QPC instant the endpoint recorded it at.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why not <c>WasapiOut</c>.</strong> The whole point of this backend is
/// <c>IAudioClock::GetPosition</c>, which returns the frame currently leaving the speakers
/// together with the performance-counter timestamp at which the endpoint observed it — the
/// "frame N hit the DAC at time T" anchor the sync loop needs. <c>WasapiOut</c> keeps its
/// <c>AudioClient</c> private and its own <c>GetPosition</c> discards the QPC half, so the
/// client has to be held here and the render loop written by hand.
/// </para>
/// <para>
/// <strong>Why shared mode.</strong> Exclusive mode locks every other application out of the
/// device, and the <c>IAudioClient3</c> low-latency path changes the audio engine's period
/// globally rather than for this stream. Neither is a decision a music player gets to make on
/// the user's behalf. Shared mode accepts IEEE float and will resample for us when the stream
/// rate differs from the engine's mix rate; the extra latency that costs is reported by the
/// clock rather than guessed at.
/// </para>
/// <para>
/// <strong>Latency.</strong> <c>IAudioClient::GetStreamLatency</c> is deliberately not the
/// source: it is documented as the <em>maximum</em> latency, it is constant for the object's
/// lifetime, and on Windows 10 and 11 it frequently returns zero. Latency here is
/// <c>framesWritten - framesPresented</c> over the sample rate, which is what Snapcast and
/// Mozilla's cubeb independently converge on, lightly smoothed. The stream-latency figure is
/// used only as a pre-playback estimate, and <see cref="TimingSourceName"/> says
/// <c>wall-clock</c> whenever the real clock could not be read so that the diagnostics view
/// shows the fallback instead of hiding it.
/// </para>
/// <para>
/// <strong>The render thread.</strong> Event-driven WASAPI: the engine signals an event each
/// period and the thread waits on it, so there is no polling and no sleep in the render path.
/// Every buffer is allocated in <see cref="OpenDeviceAsync"/>, the loop takes no lock, and
/// <c>GetPosition</c> — which blocks and crosses into the kernel — is only ever called from
/// <see cref="TryReadDeviceClock"/> on ordinary managed threads.
/// </para>
/// </remarks>
public sealed class WasapiRenderPlayer : AudioPlayerBase
{
    /// <summary>
    /// Engine periods to size the endpoint buffer at. Four gives a scheduling hiccup somewhere
    /// to be absorbed without adding latency the clock then has to report.
    /// </summary>
    private const int BufferPeriodCount = 4;

    /// <summary>
    /// Floor on the endpoint buffer, in 100-nanosecond units. A device reporting a very short
    /// default period would otherwise leave no headroom at all on a general-purpose OS.
    /// </summary>
    private const long MinimumBufferDuration = 30 * 10_000;

    /// <summary>
    /// Upper bound on a believable shared-mode output latency, in milliseconds. A larger figure
    /// means the frame counters disagree — a device reset, a format change — not a device that
    /// is really half a second behind.
    /// </summary>
    private const double MaxPlausibleLatencyMs = 500.0;

    /// <summary>
    /// Weight of each new latency sample. Light enough to ride out the sub-period jitter in
    /// pairing two counters, heavy enough to follow a real change within a second.
    /// </summary>
    private const double LatencySmoothingFactor = 0.15;

    /// <summary>
    /// Consecutive render-event timeouts tolerated before the device is declared lost.
    /// </summary>
    private const int MaxConsecutiveTimeouts = 5;

    /// <summary>
    /// How long <see cref="StopRendering"/> waits for the render thread, in milliseconds.
    /// </summary>
    private const int ThreadJoinTimeoutMs = 2_000;

    private readonly Lock _deviceGate = new();

    private MMDevice? _device;
    private AudioClient? _audioClient;
    private AudioRenderClient? _renderClient;
    private AudioClockClient? _clockClient;
    private AutoResetEvent? _renderEvent;
    private Thread? _renderThread;

    private float[] _scratch = [];
    private int _bufferFrames;
    private int _renderChannels;
    private int _sampleRate;
    private int _waitTimeoutMs;
    private ulong _clockFrequency;
    private int _fallbackLatencyMs;

    private long _framesWritten;
    private double _smoothedLatencyMs;
    private int _measuredLatencyMs;
    private bool _hasLatencySample;
    private bool _clockUsable;
    private int _fallbackWarned;
    private volatile bool _stopRequested;

    public WasapiRenderPlayer(ILogger<WasapiRenderPlayer> logger)
        : base(logger)
    {
    }

    /// <inheritdoc/>
    public override string TimingSourceName => Volatile.Read(ref _clockUsable) ? "audio-clock" : "wall-clock";

    /// <inheritdoc/>
    /// <remarks>
    /// WASAPI's shared-mode render format here is IEEE float in both branches of
    /// <see cref="CreateRenderFormat"/>, so this is fixed rather than negotiated.
    /// </remarks>
    public override string NegotiatedSampleFormat => "float32";

    /// <inheritdoc/>
    protected override int MeasuredOutputLatencyMs
    {
        get
        {
            if (Volatile.Read(ref _hasLatencySample))
            {
                return Volatile.Read(ref _measuredLatencyMs);
            }

            // Before the first clock reading there is nothing to measure. Saying so out loud
            // only matters once frames are actually moving, because until then the estimate is
            // the honest answer rather than a fallback.
            if (State == AudioPlayerState.Playing && Interlocked.Exchange(ref _fallbackWarned, 1) == 0)
            {
                Logger.LogWarning(
                    "WASAPI audio clock produced no reading; output latency falls back to the {LatencyMs} ms " +
                    "engine estimate and the timing source is reported as {TimingSource}",
                    _fallbackLatencyMs, TimingSourceName);
            }

            return _fallbackLatencyMs;
        }
    }

    /// <inheritdoc/>
    protected override Task OpenDeviceAsync(AudioFormat format, string? deviceId, CancellationToken cancellationToken)
    {
        // COM activation and IAudioClient::Initialize both block for tens of milliseconds, and
        // InitializeAsync is awaited from the UI thread.
        return Task.Run(() => OpenDevice(format, deviceId), cancellationToken);
    }

    /// <inheritdoc/>
    protected override void CloseDevice()
    {
        lock (_deviceGate)
        {
            // The clients are owned by the AudioClient, which releases them on Dispose. Holding
            // them here is only so the render loop does not walk a property chain per period.
            _renderClient = null;
            _clockClient = null;

            try
            {
                _audioClient?.Dispose();
            }
            catch (COMException ex)
            {
                Logger.LogWarning(ex, "WASAPI client did not release cleanly");
            }

            _audioClient = null;

            _device?.Dispose();
            _device = null;

            _renderEvent?.Dispose();
            _renderEvent = null;

            _scratch = [];
            _bufferFrames = 0;
            _clockFrequency = 0;
            Volatile.Write(ref _clockUsable, false);
            Volatile.Write(ref _hasLatencySample, false);
            Interlocked.Exchange(ref _framesWritten, 0);
        }
    }

    /// <inheritdoc/>
    protected override void StartRendering()
    {
        lock (_deviceGate)
        {
            if (_audioClient is null || _renderClient is null || _renderEvent is null)
            {
                throw new InvalidOperationException("StartRendering called before the WASAPI device was opened.");
            }

            if (_renderThread is not null)
            {
                return;
            }

            _stopRequested = false;
            _renderThread = new Thread(RenderLoop)
            {
                Name = "sendspin-wasapi-render",
                IsBackground = true,

                // The engine will glitch audibly if this thread misses a period. Highest is as
                // far as a managed thread can go without MMCSS, which is not exposed here.
                Priority = ThreadPriority.Highest
            };

            _renderThread.Start();
        }
    }

    /// <inheritdoc/>
    protected override void StopRendering(bool flush)
    {
        Thread? thread;
        AutoResetEvent? renderEvent;

        lock (_deviceGate)
        {
            thread = _renderThread;
            renderEvent = _renderEvent;
            _renderThread = null;
            _stopRequested = true;
        }

        // Wake the loop out of its wait rather than letting it sit for a whole timeout.
        renderEvent?.Set();

        if (thread is not null && !thread.Join(ThreadJoinTimeoutMs))
        {
            Logger.LogWarning(
                "WASAPI render thread did not stop within {TimeoutMs} ms; the device may glitch on the next start",
                ThreadJoinTimeoutMs);
        }

        lock (_deviceGate)
        {
            var client = _audioClient;
            if (client is null)
            {
                return;
            }

            try
            {
                client.Stop();

                if (flush)
                {
                    // Reset discards whatever is still queued and returns the endpoint's frame
                    // counter to zero, so our own written-frame count has to go with it or the
                    // next latency measurement is a whole buffer out.
                    client.Reset();
                    Interlocked.Exchange(ref _framesWritten, 0);
                }
            }
            catch (COMException ex)
            {
                Logger.LogWarning(ex, "WASAPI device did not stop cleanly (flush: {Flush})", flush);
            }
        }
    }

    /// <inheritdoc/>
    protected override AudioClockReading? TryReadDeviceClock()
    {
        var clock = _clockClient;
        var frequency = _clockFrequency;

        if (clock is null || frequency == 0 || !Volatile.Read(ref _clockUsable))
        {
            return null;
        }

        ulong position;
        ulong qpcPosition;

        try
        {
            if (!clock.GetPosition(out position, out qpcPosition))
            {
                return null;
            }
        }
        catch (COMException ex)
        {
            Volatile.Write(ref _clockUsable, false);
            Logger.LogWarning(ex, "WASAPI audio clock stopped responding; timing falls back to the wall clock");
            return null;
        }

        if (qpcPosition == 0)
        {
            // Without the paired timestamp there is nothing to anchor a device clock to, and a
            // frame position on its own would quietly become a wall-clock reading.
            Volatile.Write(ref _clockUsable, false);
            Logger.LogWarning(
                "WASAPI audio clock returned no QPC timestamp; timing falls back to the wall clock");
            return null;
        }

        var framesPresented = ToFrames(position, frequency, _sampleRate);

        // Documented as the performance counter converted to 100-nanosecond units, so this is
        // the same timebase as Stopwatch's without depending on its tick frequency.
        var hostTimeMicroseconds = (long)(qpcPosition / 10);

        UpdateLatency(framesPresented);

        return new AudioClockReading(framesPresented, hostTimeMicroseconds);
    }

    /// <summary>
    /// Converts an <c>IAudioClock</c> position into frames.
    /// </summary>
    /// <remarks>
    /// The units of <paramref name="position"/> are the device's, not frames — all that is
    /// promised is that <c>position / frequency</c> is a time in seconds. Splitting the division
    /// into whole seconds and a remainder keeps that exact without overflowing on a stream that
    /// has been open for months.
    /// </remarks>
    private static long ToFrames(ulong position, ulong frequency, int sampleRate)
    {
        var rate = (ulong)sampleRate;
        var wholeSeconds = position / frequency;
        var remainder = position % frequency;

        return (long)((wholeSeconds * rate) + (remainder * rate / frequency));
    }

    /// <summary>
    /// Builds the format to hand the engine: IEEE float at the stream's own rate, which shared
    /// mode accepts and will convert from where it has to.
    /// </summary>
    /// <remarks>
    /// Above two channels the extensible form with an explicit channel mask is what drivers
    /// expect; NAudio derives the IEEE float subformat from the 32-bit sample size.
    /// </remarks>
    private static WaveFormat CreateRenderFormat(int sampleRate, int channels) =>
        channels > 2
            ? new WaveFormatExtensible(sampleRate, 32, channels)
            : WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

    private void OpenDevice(AudioFormat format, string? deviceId)
    {
        CloseDevice();

        using var enumerator = new MMDeviceEnumerator();
        var device = ResolveDevice(enumerator, deviceId);
        var client = device.AudioClient;
        AutoResetEvent? renderEvent = null;
        var owned = false;

        try
        {
            var mixFormat = client.MixFormat;
            var renderFormat = CreateRenderFormat(format.SampleRate, format.Channels);
            var flags = AudioClientStreamFlags.EventCallback;

            var mismatched = format.SampleRate != mixFormat.SampleRate
                || format.Channels != mixFormat.Channels
                || !client.IsFormatSupported(AudioClientShareMode.Shared, renderFormat);

            if (mismatched)
            {
                // The engine's own resampler, rather than one of ours: it is already in the
                // path, and whatever latency it adds is included in the clock position the
                // endpoint reports, so it stays visible instead of becoming a hidden offset.
                flags |= AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;

                Logger.LogInformation(
                    "WASAPI engine will convert {StreamRate} Hz {StreamChannels} ch to the mix format " +
                    "{MixRate} Hz {MixChannels} ch; the resampler adds latency, which the audio clock reports",
                    format.SampleRate, format.Channels, mixFormat.SampleRate, mixFormat.Channels);
            }

            var bufferDuration = Math.Max(client.DefaultDevicePeriod * BufferPeriodCount, MinimumBufferDuration);

            Logger.LogDebug(
                "Initialising WASAPI shared-mode stream on {Device}: format {Format}, flags {Flags}, " +
                "buffer {BufferMs} ms",
                device.FriendlyName, renderFormat, flags, bufferDuration / 10_000.0);

            // Periodicity must be zero in shared mode; the engine picks its own period.
            client.Initialize(AudioClientShareMode.Shared, flags, bufferDuration, 0, renderFormat, Guid.Empty);

            renderEvent = new AutoResetEvent(false);
            client.SetEventHandle(renderEvent.SafeWaitHandle.DangerousGetHandle());

            lock (_deviceGate)
            {
                _device = device;
                _audioClient = client;
                _renderClient = client.AudioRenderClient;
                _clockClient = client.AudioClockClient;
                _renderEvent = renderEvent;

                _sampleRate = format.SampleRate;
                _renderChannels = format.Channels;
                _bufferFrames = client.BufferSize;
                _scratch = new float[_bufferFrames * _renderChannels];

                var bufferMs = _bufferFrames * 1000.0 / _sampleRate;

                // Long enough that a busy machine does not trip it, short enough that a device
                // which has silently stopped signalling is noticed rather than waited on.
                _waitTimeoutMs = Math.Max(100, (int)(bufferMs * 3));
                _fallbackLatencyMs = EstimateFallbackLatencyMs(client, bufferMs);

                Interlocked.Exchange(ref _framesWritten, 0);
                Volatile.Write(ref _hasLatencySample, false);
                Interlocked.Exchange(ref _fallbackWarned, 0);

                ProbeClock();
                owned = true;
            }
        }
        finally
        {
            // Until the fields take ownership these are ours, and a failure on the way through
            // must not leak a COM object or a kernel handle. The exception itself belongs to the
            // caller: AudioPlayerBase reports it through Fail(DeviceInitializationFailed).
            if (!owned)
            {
                renderEvent?.Dispose();
                client.Dispose();
                device.Dispose();
            }
        }
    }

    private MMDevice ResolveDevice(MMDeviceEnumerator enumerator, string? deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                return enumerator.GetDevice(deviceId);
            }
            catch (COMException ex)
            {
                // A remembered device that has since been unplugged is ordinary, and falling
                // back to the default output is better than refusing to play.
                Logger.LogWarning(ex, "Audio device {DeviceId} is not available; using the system default", deviceId);
            }
        }

        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            throw new InvalidOperationException("Windows reports no default audio output device.");
        }

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    /// <summary>
    /// Reads the clock once so <see cref="TimingSourceName"/> is honest from the moment the device
    /// is open, rather than optimistic until the first failure.
    /// </summary>
    /// <remarks>
    /// Only the frequency and a successful call are checked. The paired QPC timestamp is not
    /// required here, because the stream has not been started yet and a device that only supplies
    /// one while running must not be written off before it has had the chance;
    /// <see cref="TryReadDeviceClock"/> makes that judgement on a live stream instead.
    /// </remarks>
    private void ProbeClock()
    {
        var clock = _clockClient;
        if (clock is null)
        {
            Logger.LogWarning("WASAPI endpoint exposed no audio clock; timing will use the wall clock");
            return;
        }

        try
        {
            _clockFrequency = clock.Frequency;

            if (_clockFrequency == 0)
            {
                Logger.LogWarning("WASAPI audio clock reported a zero frequency; timing will use the wall clock");
                return;
            }

            if (!clock.GetPosition(out _, out _))
            {
                Logger.LogWarning("WASAPI audio clock refused a position read; timing will use the wall clock");
                return;
            }

            Volatile.Write(ref _clockUsable, true);
            Logger.LogDebug("WASAPI audio clock available at {Frequency} units/s", _clockFrequency);
        }
        catch (COMException ex)
        {
            Logger.LogWarning(ex, "WASAPI audio clock is unavailable; timing will use the wall clock");
        }
    }

    /// <summary>
    /// Estimates output latency for use before the clock has produced a reading.
    /// </summary>
    private int EstimateFallbackLatencyMs(AudioClient client, double bufferMs)
    {
        try
        {
            var streamLatencyMs = client.StreamLatency / 10_000.0;
            if (streamLatencyMs > 0)
            {
                return (int)Math.Round(streamLatencyMs);
            }

            Logger.LogDebug(
                "WASAPI reported a stream latency of zero, which is common on Windows 10 and 11; " +
                "the {BufferMs} ms endpoint buffer is the pre-playback estimate instead",
                bufferMs);
        }
        catch (COMException ex)
        {
            Logger.LogDebug(ex, "WASAPI stream latency could not be read; using the endpoint buffer size");
        }

        return (int)Math.Round(bufferMs);
    }

    /// <summary>
    /// Folds one clock reading into the smoothed output latency.
    /// </summary>
    /// <remarks>
    /// Frames handed to the engine but not yet presented by the endpoint <em>are</em> the output
    /// latency, so the reading the sync loop already asked for pays for the measurement too and
    /// no extra kernel transition is needed.
    /// </remarks>
    private void UpdateLatency(long framesPresented)
    {
        var pendingFrames = Interlocked.Read(ref _framesWritten) - framesPresented;
        if (pendingFrames < 0)
        {
            return;
        }

        var sampleMs = pendingFrames * 1000.0 / _sampleRate;
        if (sampleMs > MaxPlausibleLatencyMs)
        {
            return;
        }

        var smoothed = Volatile.Read(ref _hasLatencySample)
            ? _smoothedLatencyMs + (LatencySmoothingFactor * (sampleMs - _smoothedLatencyMs))
            : sampleMs;

        _smoothedLatencyMs = smoothed;
        Volatile.Write(ref _measuredLatencyMs, (int)Math.Round(smoothed));
        Volatile.Write(ref _hasLatencySample, true);
    }

    /// <summary>
    /// The render loop. Allocates nothing, takes no lock, and never sleeps.
    /// </summary>
    private void RenderLoop()
    {
        var client = _audioClient;
        var renderClient = _renderClient;
        var renderEvent = _renderEvent;
        var source = SampleSource;

        if (client is null || renderClient is null || renderEvent is null || source is null)
        {
            return;
        }

        var timeouts = 0;

        try
        {
            // Prefill whatever the endpoint has room for before starting, so the first period
            // is music rather than silence. After a pause the buffer still holds frames, hence
            // the padding query rather than a full buffer.
            FillBuffer(renderClient, source, _bufferFrames - client.CurrentPadding);
            client.Start();

            while (!_stopRequested)
            {
                if (!renderEvent.WaitOne(_waitTimeoutMs))
                {
                    if (++timeouts >= MaxConsecutiveTimeouts)
                    {
                        StopClientQuietly(client);
                        Fail(
                            AudioPlayerErrorCode.DeviceLost,
                            $"WASAPI device stopped signalling for {MaxConsecutiveTimeouts} consecutive periods");
                        return;
                    }

                    continue;
                }

                timeouts = 0;

                if (_stopRequested)
                {
                    return;
                }

                var framesAvailable = _bufferFrames - client.CurrentPadding;
                if (framesAvailable > 0)
                {
                    FillBuffer(renderClient, source, framesAvailable);
                }
            }
        }
        catch (COMException ex)
        {
            StopClientQuietly(client);
            Fail(AudioPlayerErrorCode.DeviceLost, "WASAPI device faulted while rendering", ex);
        }
        catch (ObjectDisposedException ex)
        {
            // The device was closed underneath the loop, which is a teardown ordering bug
            // rather than a device fault. Recorded so it cannot hide as silence.
            Logger.LogWarning(ex, "WASAPI render loop ran after the device was disposed");
        }
        catch (Exception ex)
        {
            // An exception escaping a background thread terminates the process. Reporting it
            // through Fail keeps the player in a diagnosable error state instead, and the
            // player is finished either way.
            StopClientQuietly(client);
            Fail(AudioPlayerErrorCode.Unknown, "WASAPI render loop failed", ex);
        }
    }

    /// <summary>
    /// Pulls <paramref name="frames"/> frames from the source into the endpoint buffer, applying
    /// gain on the way through.
    /// </summary>
    /// <remarks>
    /// <see cref="AudioPlayerBase.EffectiveGain"/> is already the SDK's curved amplitude, so it
    /// is a plain multiplier here. Raising it to any power would apply the loudness curve twice.
    /// </remarks>
    private unsafe void FillBuffer(AudioRenderClient renderClient, IAudioSampleSource source, int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        var samples = frames * _renderChannels;
        var read = source.Read(_scratch, 0, samples);

        if (read < samples)
        {
            // Short read means the buffer ran dry. Silence for the shortfall, and the endpoint
            // still gets a full period so it does not glitch on top of the underrun.
            Array.Clear(_scratch, read, samples - read);
        }

        var destination = new Span<float>((void*)renderClient.GetBuffer(frames), samples);
        var gain = EffectiveGain;

        if (gain == 1f)
        {
            _scratch.AsSpan(0, samples).CopyTo(destination);
        }
        else
        {
            var scratch = _scratch;
            for (var i = 0; i < samples; i++)
            {
                destination[i] = scratch[i] * gain;
            }
        }

        renderClient.ReleaseBuffer(frames, AudioClientBufferFlags.None);
        Interlocked.Add(ref _framesWritten, frames);
    }

    private void StopClientQuietly(AudioClient client)
    {
        try
        {
            client.Stop();
        }
        catch (COMException ex)
        {
            Logger.LogDebug(ex, "WASAPI device would not stop after a render fault");
        }
    }
}
