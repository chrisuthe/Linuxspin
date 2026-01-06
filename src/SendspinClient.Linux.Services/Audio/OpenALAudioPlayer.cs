using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using SendspinClient.Linux.Services.Audio.Interfaces;
using Silk.NET.OpenAL;

namespace SendspinClient.Linux.Services.Audio;

/// <summary>
/// OpenAL-based audio player for Linux using Silk.NET.
/// Provides real-time audio playback with ~100ms buffer latency for sync.
/// Supports dynamic resampling for multi-room audio sync correction.
/// </summary>
public sealed class OpenALAudioPlayer : IAudioPlayer, IAudioDeviceEnumerator
{
    private const int BufferCount = 4;
    private const int BufferSizeMs = 25;

    private readonly ILogger<OpenALAudioPlayer>? _logger;
    private readonly object _lock = new();

    private AL? _al;
    private ALContext? _alc;
    private nint _device;
    private nint _context;
    private uint _source;
    private uint[] _buffers = Array.Empty<uint>();

    private AudioFormat? _format;
    private IAudioSampleSource? _sampleSource;
    private ITimedAudioBuffer? _timedBuffer; // For sync correction
    private IDynamicResampler? _resampler;
    private Thread? _playbackThread;
    private CancellationTokenSource? _playCts;

    // Buffers for float32->int16 conversion
    private float[] _floatBuffer = Array.Empty<float>();
    private byte[] _byteBuffer = Array.Empty<byte>();

    private AudioPlayerState _state = AudioPlayerState.Uninitialized;
    private float _volume = 1.0f;
    private bool _isMuted;
    private bool _isDisposed;

    public OpenALAudioPlayer() { }

    public OpenALAudioPlayer(ILogger<OpenALAudioPlayer> logger) => _logger = logger;

    public AudioPlayerState State
    {
        get { lock (_lock) return _state; }
        private set
        {
            lock (_lock) { if (_state == value) return; _state = value; }
            StateChanged?.Invoke(this, value);
        }
    }

    public float Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0f, 1f); ApplyVolume(); }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set { _isMuted = value; ApplyVolume(); }
    }

    public int OutputLatencyMs => BufferCount * BufferSizeMs;

    public event EventHandler<AudioPlayerState>? StateChanged;
    public event EventHandler<AudioPlayerError>? ErrorOccurred;

    public unsafe Task InitializeAsync(AudioFormat format, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ct.ThrowIfCancellationRequested();

        try
        {
            _format = format;
            _al = AL.GetApi();
            _alc = ALContext.GetApi();

            _device = (nint)_alc.OpenDevice((string?)null);
            if (_device == 0)
                throw new InvalidOperationException("Failed to open default audio device");

            _context = (nint)_alc.CreateContext((Device*)_device, null);
            if (_context == 0)
            {
                _alc.CloseDevice((Device*)_device);
                throw new InvalidOperationException("Failed to create OpenAL context");
            }

            _alc.MakeContextCurrent((Context*)_context);

            _source = _al.GenSource();
            _buffers = new uint[BufferCount];
            for (int i = 0; i < BufferCount; i++)
                _buffers[i] = _al.GenBuffer();

            ApplyVolume();
            State = AudioPlayerState.Stopped;
            _logger?.LogInformation("OpenAL initialized: {Rate}Hz, {Ch}ch", format.SampleRate, format.Channels);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "OpenAL init failed");
            RaiseError(AudioPlayerErrorCode.DeviceInitializationFailed, "Init failed", ex);
            throw;
        }

        return Task.CompletedTask;
    }

    public void SetSampleSource(IAudioSampleSource source)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        lock (_lock)
        {
            _sampleSource = source;

            // If the source is backed by a timed buffer, create resampler for sync correction
            if (_format != null && _resampler == null)
            {
                _resampler = DynamicResamplerFactory.Create(_format.SampleRate, _format.Channels);
                _logger?.LogInformation("Resampler ready for sync correction (backend: {Type})",
                    _resampler.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Sets the timed buffer for sync correction. The SDK's audio pipeline will call this.
    /// </summary>
    public void SetTimedBuffer(ITimedAudioBuffer timedBuffer)
    {
        lock (_lock)
        {
            _timedBuffer = timedBuffer;
            _timedBuffer.OutputLatencyMicroseconds = OutputLatencyMs * 1000;
        }
    }

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_format == null || _al == null) return;

        lock (_lock)
        {
            if (_state == AudioPlayerState.Playing) return;

            _playCts?.Cancel();
            _playCts = new CancellationTokenSource();
            _playbackThread = new Thread(() => PlaybackLoop(_playCts.Token))
            {
                Name = "OpenAL-Playback",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _playbackThread.Start();
            State = AudioPlayerState.Playing;
        }
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_al == null || _state != AudioPlayerState.Playing) return;

        lock (_lock)
        {
            _playCts?.Cancel();
            _playbackThread?.Join(1000);
            _al.SourcePause(_source);
            State = AudioPlayerState.Paused;
        }
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_al == null) return;

        lock (_lock)
        {
            _playCts?.Cancel();
            _playbackThread?.Join(1000);
            _al.SourceStop(_source);
            UnqueueAllBuffers();
            State = AudioPlayerState.Stopped;
        }
    }

    public unsafe Task SwitchDeviceAsync(string? deviceId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_alc == null || _format == null) return Task.CompletedTask;

        var wasPlaying = State == AudioPlayerState.Playing;
        Stop();

        try
        {
            CleanupOpenAL();

            _device = (nint)_alc.OpenDevice(deviceId);
            if (_device == 0)
                throw new InvalidOperationException($"Failed to open device: {deviceId ?? "default"}");

            _context = (nint)_alc.CreateContext((Device*)_device, null);
            _alc.MakeContextCurrent((Context*)_context);

            _source = _al!.GenSource();
            _buffers = new uint[BufferCount];
            for (int i = 0; i < BufferCount; i++)
                _buffers[i] = _al.GenBuffer();
            ApplyVolume();

            _logger?.LogInformation("Switched to device: {Dev}", deviceId ?? "default");
        }
        catch (Exception ex)
        {
            RaiseError(AudioPlayerErrorCode.DeviceNotFound, $"Switch failed: {deviceId}", ex);
            throw;
        }

        if (wasPlaying) Play();
        return Task.CompletedTask;
    }

    public unsafe IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        if (_alc == null) return devices;

        try
        {
            var defaultName = _alc.GetContextProperty((Device*)_device, GetContextString.DeviceSpecifier);
            if (!string.IsNullOrEmpty(defaultName))
                devices.Add(new AudioDeviceInfo { Id = defaultName, Name = defaultName, IsDefault = true });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enumerate devices");
        }

        return devices;
    }

    public AudioDeviceInfo? GetDefaultDevice() =>
        GetDevices().FirstOrDefault(d => d.IsDefault) ?? GetDevices().FirstOrDefault();

    private void PlaybackLoop(CancellationToken ct)
    {
        if (_al == null || _format == null) return;

        // Calculate buffer sizes: samples per buffer (25ms at sample rate)
        int samplesPerBuffer = _format.SampleRate * BufferSizeMs / 1000 * _format.Channels;
        int bytesPerBuffer = samplesPerBuffer * 2; // int16 = 2 bytes per sample

        // Allocate conversion buffers
        _floatBuffer = new float[samplesPerBuffer];
        _byteBuffer = new byte[bytesPerBuffer];

        // Allocate resampling buffers if needed
        float[] resampledBuffer = new float[(int)(samplesPerBuffer * 1.1)];

        try
        {
            foreach (var buf in _buffers)
            {
                if (ct.IsCancellationRequested) return;
                FillAndQueueBuffer(buf, _floatBuffer, _byteBuffer, resampledBuffer);
            }

            _al.SourcePlay(_source);

            var unqueueBuf = new uint[1];
            while (!ct.IsCancellationRequested)
            {
                _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);

                while (processed-- > 0 && !ct.IsCancellationRequested)
                {
                    _al.SourceUnqueueBuffers(_source, unqueueBuf);
                    FillAndQueueBuffer(unqueueBuf[0], _floatBuffer, _byteBuffer, resampledBuffer);
                }

                _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
                if ((SourceState)state == SourceState.Stopped && !ct.IsCancellationRequested)
                {
                    _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
                    if (queued > 0) _al.SourcePlay(_source);
                }

                Thread.Sleep(5);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger?.LogError(ex, "Playback error");
            RaiseError(AudioPlayerErrorCode.Unknown, "Playback error", ex);
        }
    }

    private void FillAndQueueBuffer(uint buffer, float[] floatData, byte[] byteData, float[] resampledData)
    {
        if (_al == null || _format == null) return;

        IAudioSampleSource? source;
        ITimedAudioBuffer? timedBuffer;
        IDynamicResampler? resampler;
        lock (_lock)
        {
            source = _sampleSource;
            timedBuffer = _timedBuffer;
            resampler = _resampler;
        }

        // Read float samples from SDK source
        int samplesRead = source?.Read(floatData, 0, floatData.Length) ?? 0;
        if (samplesRead == 0)
        {
            Array.Clear(byteData);
        }
        else
        {
            // Apply sync correction via resampling if needed
            float[] outputSamples = floatData;
            int outputCount = samplesRead;

            if (timedBuffer != null && resampler != null)
            {
                double targetRate = timedBuffer.TargetPlaybackRate;
                if (Math.Abs(targetRate - 1.0) > 0.0001)
                {
                    resampler.Ratio = targetRate;
                    outputCount = resampler.Process(
                        floatData.AsSpan(0, samplesRead),
                        resampledData.AsSpan());
                    outputSamples = resampledData;
                }
            }

            // Convert float32 to int16
            ConvertFloatToInt16(outputSamples, byteData, Math.Min(outputCount, byteData.Length / 2));
        }

        var alFormat = _format.Channels == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16;
        _al.BufferData(buffer, alFormat, byteData, _format.SampleRate);
        _al.SourceQueueBuffers(_source, new uint[] { buffer });
    }

    private static void ConvertFloatToInt16(float[] source, byte[] dest, int sampleCount)
    {
        for (int i = 0; i < sampleCount; i++)
        {
            float clamped = Math.Clamp(source[i], -1.0f, 1.0f);
            short sample = (short)(clamped * 32767.0f);
            dest[i * 2] = (byte)(sample & 0xFF);
            dest[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        // Clear remaining bytes if any
        if (sampleCount * 2 < dest.Length)
            Array.Clear(dest, sampleCount * 2, dest.Length - sampleCount * 2);
    }

    private void UnqueueAllBuffers()
    {
        if (_al == null) return;
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
        var buf = new uint[1];
        while (queued-- > 0)
        {
            try { _al.SourceUnqueueBuffers(_source, buf); } catch { break; }
        }
    }

    private void ApplyVolume()
    {
        if (_al == null) return;
        _al.SetSourceProperty(_source, SourceFloat.Gain, _isMuted ? 0f : _volume);
    }

    private unsafe void CleanupOpenAL()
    {
        // Dispose resampling resources
        _resampler?.Dispose();
        _resampler = null;
        _timedBuffer = null;

        if (_al != null)
        {
            foreach (var buf in _buffers) try { _al.DeleteBuffer(buf); } catch { }
            _buffers = Array.Empty<uint>();
            if (_source != 0) try { _al.DeleteSource(_source); } catch { }
            _source = 0;
        }

        if (_alc != null)
        {
            if (_context != 0)
            {
                try { _alc.MakeContextCurrent(null); _alc.DestroyContext((Context*)_context); } catch { }
                _context = 0;
            }
            if (_device != 0)
            {
                try { _alc.CloseDevice((Device*)_device); } catch { }
                _device = 0;
            }
        }

        _al?.Dispose(); _alc?.Dispose();
        _al = null; _alc = null;
    }

    private void RaiseError(AudioPlayerErrorCode code, string msg, Exception? ex)
    {
        if (code is AudioPlayerErrorCode.DeviceInitializationFailed or AudioPlayerErrorCode.DeviceLost or AudioPlayerErrorCode.DeviceNotFound)
            lock (_lock) _state = AudioPlayerState.Error;
        ErrorOccurred?.Invoke(this, new AudioPlayerError($"[{code}] {msg}", ex));
    }

    public unsafe ValueTask DisposeAsync()
    {
        if (_isDisposed) return ValueTask.CompletedTask;
        _isDisposed = true;
        _playCts?.Cancel();
        try { _playbackThread?.Join(1000); } catch { }
        lock (_lock) CleanupOpenAL();
        return ValueTask.CompletedTask;
    }
}
