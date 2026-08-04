using Microsoft.Extensions.Logging;
using Sendspin.Core.Audio;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;

namespace Sendspin.Platform.Shared.Audio;

/// <summary>
/// The parts of <see cref="IAudioPlayer"/> that are identical on every backend: state and its
/// events, volume and mute, latency reporting, and projecting a device clock reading onto the
/// host timebase.
/// </summary>
/// <remarks>
/// <para>
/// What differs between WASAPI, OpenAL and AUHAL is how frames reach the device and how the
/// device reports its position — those are the abstract members. Everything else belongs here, so
/// that there is one state machine, one volume path and one clock projection rather than three.
/// </para>
/// <para>
/// <strong>Volume.</strong> <see cref="Volume"/> arrives already curved, because the SDK's
/// <c>AudioPipeline.SetVolume(int)</c> applies <c>(v/100)^1.5</c> before it gets here. It is a
/// plain amplitude multiplier. Do not raise it to any power in a subclass.
/// </para>
/// </remarks>
public abstract class AudioPlayerBase : IAudioPlayer
{
    private readonly Lock _stateGate = new();

    /// <summary>
    /// Held while the device's clock is read, and while the device is torn down.
    /// </summary>
    /// <remarks>
    /// The SDK reads the audio clock on every buffer read, from its own thread, so a device switch
    /// or a disposal is genuinely concurrent with a clock read. Every backend's reader dereferences
    /// something the teardown frees — an unmanaged seqlock cell on macOS, a COM object on Windows, a
    /// raw <c>ALCdevice*</c> on Linux — so without this the read is a use-after-free rather than a
    /// stale value. It lives here because the base class owns the ordering: no backend can fix it
    /// alone, since none of them is called for teardown except through this class.
    /// </remarks>
    private readonly Lock _clockGate = new();

    private AudioPlayerState _state = AudioPlayerState.Uninitialized;
    private DeviceAnchoredClock? _clock;
    private float _volume = 1.0f;
    private bool _isMuted;
    private bool _isDisposed;

    protected AudioPlayerBase(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Logger = logger;
    }

    /// <inheritdoc/>
    public event EventHandler<AudioPlayerState>? StateChanged;

    /// <inheritdoc/>
    public event EventHandler<AudioPlayerError>? ErrorOccurred;

    /// <inheritdoc/>
    public AudioPlayerState State
    {
        get { lock (_stateGate) return _state; }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// An amplitude in 0.0-1.0 that the SDK has already passed through the loudness curve.
    /// Applied linearly.
    /// </remarks>
    public float Volume
    {
        get => Volatile.Read(ref _volume);
        set
        {
            Volatile.Write(ref _volume, Math.Clamp(value, 0f, 1f));
            OnGainChanged();
        }
    }

    /// <inheritdoc/>
    public bool IsMuted
    {
        get => Volatile.Read(ref _isMuted);
        set
        {
            Volatile.Write(ref _isMuted, value);
            OnGainChanged();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The device's own measured latency plus the user's manual offset for this device. The
    /// offset is not a fudge factor: no platform API reports the Bluetooth, AirPlay or analog
    /// tail, so for those outputs it is the only route to a correct figure.
    /// </remarks>
    public int OutputLatencyMs => Math.Max(0, MeasuredOutputLatencyMs + (int)Math.Round(ManualLatencyOffsetMs));

    /// <inheritdoc/>
    /// <remarks>
    /// Zero unless a backend has actually measured its prefill. The SDK self-measures the
    /// residual constant offset after its startup grace period, so an honest zero costs
    /// nothing, whereas a guess here is a permanent bias on every chunk.
    /// </remarks>
    public virtual int CalibratedStartupLatencyMs => 0;

    /// <inheritdoc/>
    public AudioFormat? OutputFormat { get; private set; }

    /// <summary>
    /// Gets or sets the manual per-device latency offset in milliseconds.
    /// </summary>
    public double ManualLatencyOffsetMs { get; set; }

    /// <summary>
    /// Gets the id of the device currently open, or null for the system default.
    /// </summary>
    public string? CurrentDeviceId { get; private set; }

    /// <summary>
    /// Gets the name this backend reports as its timing source, for
    /// <see cref="ITimedAudioBuffer.TimingSourceName"/> and the diagnostics view.
    /// </summary>
    public abstract string TimingSourceName { get; }

    /// <summary>
    /// Gets the logger for this player.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Gets the sample source to pull from, or null before one is set.
    /// </summary>
    protected IAudioSampleSource? SampleSource { get; private set; }

    /// <summary>
    /// Gets the effective gain to multiply samples by: the amplitude, or zero when muted.
    /// </summary>
    protected float EffectiveGain => IsMuted ? 0f : Volume;

    /// <summary>
    /// Gets the device's measured output latency in milliseconds, excluding the manual offset.
    /// </summary>
    /// <remarks>
    /// Must be a real figure queried from the device, against a ±1 ms synchronisation budget. A
    /// constant derived from the buffer size is not acceptable here, however plausible it looks.
    /// </remarks>
    protected abstract int MeasuredOutputLatencyMs { get; }

    /// <inheritdoc/>
    public async Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(format);
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await OpenDeviceAsync(format, CurrentDeviceId, cancellationToken).ConfigureAwait(false);

            OutputFormat = format;
            _clock = new DeviceAnchoredClock(format.SampleRate);
            OnGainChanged();
            SetState(AudioPlayerState.Stopped);

            Logger.LogInformation(
                "Audio output ready: {Rate} Hz, {Channels} ch, measured latency {LatencyMs} ms " +
                "(+{OffsetMs} ms manual), timing source {TimingSource}",
                format.SampleRate, format.Channels, MeasuredOutputLatencyMs, ManualLatencyOffsetMs,
                TimingSourceName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(AudioPlayerErrorCode.DeviceInitializationFailed, "Audio output could not be initialised", ex);
            throw;
        }
    }

    /// <inheritdoc/>
    public void SetSampleSource(IAudioSampleSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        SampleSource = source;
    }

    /// <inheritdoc/>
    public void Play()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (OutputFormat is null)
        {
            throw new InvalidOperationException("Play called before InitializeAsync.");
        }

        if (SampleSource is null)
        {
            throw new InvalidOperationException("Play called before SetSampleSource.");
        }

        if (State == AudioPlayerState.Playing)
        {
            return;
        }

        StartRendering();
        SetState(AudioPlayerState.Playing);
    }

    /// <inheritdoc/>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (State != AudioPlayerState.Playing)
        {
            return;
        }

        StopRendering(flush: false);

        // Drop the anchor even though the buffer is kept. While paused, wall time advances and the
        // device's frame counter does not, so an anchor established before the pause would, after
        // a resume, report a time earlier than reality by exactly the pause duration — which the
        // SDK reads as a large negative sync error. Re-anchoring on the first reading after resume
        // costs nothing, because the anchor's origin is meant to be "now" anyway.
        _clock?.Reset();
        SetState(AudioPlayerState.Paused);
    }

    /// <inheritdoc/>
    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (State is AudioPlayerState.Uninitialized or AudioPlayerState.Stopped)
        {
            return;
        }

        StopRendering(flush: true);

        // The device's frame counter restarts, so the clock anchor is stale. Keeping it would
        // read as a large jump backwards and provoke a spurious resync.
        _clock?.Reset();
        SetState(AudioPlayerState.Stopped);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reopens the device without touching the server connection, resuming if it was playing.
    /// </remarks>
    public async Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var format = OutputFormat;
        if (format is null)
        {
            // Not initialised yet; remember the choice so InitializeAsync opens the right one.
            CurrentDeviceId = deviceId;
            return;
        }

        var wasPlaying = State == AudioPlayerState.Playing;

        try
        {
            StopRendering(flush: true);

            lock (_clockGate)
            {
                CloseDevice();
            }

            CurrentDeviceId = deviceId;
            await OpenDeviceAsync(format, deviceId, cancellationToken).ConfigureAwait(false);

            _clock = new DeviceAnchoredClock(format.SampleRate);
            OnGainChanged();
            SetState(AudioPlayerState.Stopped);

            Logger.LogInformation("Switched audio output to {DeviceId}, measured latency {LatencyMs} ms",
                deviceId ?? "system default", MeasuredOutputLatencyMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(AudioPlayerErrorCode.DeviceNotFound, $"Could not switch to audio device {deviceId}", ex);
            throw;
        }

        if (wasPlaying)
        {
            StartRendering();
            SetState(AudioPlayerState.Playing);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns a monotonic microsecond time derived from the device's frame counter, so the
    /// sync loop advances at the rate the hardware really consumes samples rather than at the
    /// OS timer's. Null when the backend cannot read its clock, in which case the SDK falls
    /// back to a filtered wall clock and reports the fallback in
    /// <see cref="ITimedAudioBuffer.TimingSourceName"/>.
    /// </remarks>
    public long? GetAudioClockMicroseconds()
    {
        if (_isDisposed || State != AudioPlayerState.Playing)
        {
            return null;
        }

        var clock = _clock;
        if (clock is null)
        {
            return null;
        }

        AudioClockReading? reading;

        lock (_clockGate)
        {
            // Re-check inside the gate: teardown may have completed between the checks above and
            // acquiring it.
            if (_isDisposed || OutputFormat is null)
            {
                return null;
            }

            reading = TryReadDeviceClock();
        }

        if (reading is null)
        {
            return null;
        }

        // The origin must come from the SDK's own timeline, not from the reading's
        // platform-native timestamp. See DeviceAnchoredClock: the platforms all report
        // boot-relative time while the SDK compares against Unix-epoch microseconds, so mixing
        // the two would report a sync error of decades.
        return clock.Project(reading.Value, HighPrecisionTimer.Shared.GetCurrentTimeMicroseconds());
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A reconnect means the server's timeline restarts, so the anchor tying our frame counter
    /// to a host instant no longer refers to anything.
    /// </remarks>
    public virtual void NotifyReconnect()
    {
        _clock?.Reset();
        Logger.LogDebug("Audio clock anchor dropped after reconnect");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        try
        {
            StopRendering(flush: true);
        }
        catch (Exception ex)
        {
            // Teardown must not throw out of DisposeAsync, or shutdown becomes racy for
            // everything disposed after this. Recorded rather than swallowed.
            Logger.LogWarning(ex, "Audio render loop did not stop cleanly during disposal");
        }

        // DisposeCoreAsync is awaited and so cannot be inside the gate; by contract it releases only
        // what StopRendering has already stopped. CloseDevice, which frees what the clock reader
        // dereferences, is the part that has to be serialised against a read.
        await DisposeCoreAsync().ConfigureAwait(false);

        lock (_clockGate)
        {
            CloseDevice();
            _clock = null;
        }

        SampleSource = null;
        SetState(AudioPlayerState.Uninitialized);
    }

    /// <summary>
    /// Opens the device for the given format.
    /// </summary>
    /// <param name="format">The format to render.</param>
    /// <param name="deviceId">The device to open, or null for the system default.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    protected abstract Task OpenDeviceAsync(AudioFormat format, string? deviceId, CancellationToken cancellationToken);

    /// <summary>
    /// Releases the device. Must tolerate being called when nothing is open.
    /// </summary>
    protected abstract void CloseDevice();

    /// <summary>
    /// Starts moving frames to the device.
    /// </summary>
    protected abstract void StartRendering();

    /// <summary>
    /// Stops moving frames to the device, and returns only once rendering has actually
    /// stopped.
    /// </summary>
    /// <param name="flush">
    /// True to discard queued audio, false to leave it in place for a resume.
    /// </param>
    protected abstract void StopRendering(bool flush);

    /// <summary>
    /// Reads the device's frame position paired with the host time it was recorded at, or null
    /// when unavailable.
    /// </summary>
    /// <remarks>
    /// Called from ordinary managed code, never from a render callback: on Windows this is a
    /// blocking call that crosses into the kernel.
    /// </remarks>
    protected abstract AudioClockReading? TryReadDeviceClock();

    /// <summary>
    /// Called when volume or mute changes, for backends that set gain on the device rather
    /// than scaling samples themselves.
    /// </summary>
    protected virtual void OnGainChanged()
    {
    }

    /// <summary>
    /// Releases backend resources during disposal, after rendering has stopped and before the
    /// device is closed.
    /// </summary>
    protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Reports a failure: moves to <see cref="AudioPlayerState.Error"/> for faults that lose
    /// the device, and raises <see cref="ErrorOccurred"/>.
    /// </summary>
    protected void Fail(AudioPlayerErrorCode code, string message, Exception? exception = null)
    {
        Logger.LogError(exception, "Audio player error {Code}: {Message}", code, message);

        if (code is AudioPlayerErrorCode.DeviceInitializationFailed
            or AudioPlayerErrorCode.DeviceLost
            or AudioPlayerErrorCode.DeviceNotFound)
        {
            SetState(AudioPlayerState.Error);
        }

        ErrorOccurred?.Invoke(this, new AudioPlayerError($"[{code}] {message}", exception));
    }

    private void SetState(AudioPlayerState newState)
    {
        lock (_stateGate)
        {
            if (_state == newState)
            {
                return;
            }

            _state = newState;
        }

        StateChanged?.Invoke(this, newState);
    }
}
