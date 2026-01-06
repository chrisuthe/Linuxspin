using System;
using System.Runtime.InteropServices;

namespace SendspinClient.Linux.Services.Audio;

/// <summary>
/// Interface for dynamic audio resamplers that support real-time rate adjustment.
/// Used for audio sync correction in multi-room audio systems.
/// </summary>
public interface IDynamicResampler : IDisposable
{
    /// <summary>
    /// Current resampling ratio (1.0 = no change, &lt;1.0 = slower, &gt;1.0 = faster).
    /// Valid range: 0.96 to 1.04 (±4% for drift correction).
    /// </summary>
    double Ratio { get; set; }

    /// <summary>
    /// Process input samples and write to output buffer.
    /// </summary>
    /// <param name="input">Input audio samples (mono float32).</param>
    /// <param name="output">Output buffer for resampled audio.</param>
    /// <returns>Number of samples written to output buffer.</returns>
    int Process(ReadOnlySpan<float> input, Span<float> output);

    /// <summary>
    /// Reset the resampler state, clearing any internal buffers.
    /// </summary>
    void Reset();
}

/// <summary>
/// Quality levels for the Speex resampler (1-10 scale).
/// Higher quality means better audio but more CPU usage.
/// </summary>
public enum ResamplerQuality
{
    Fastest = 0,
    Level1 = 1,
    Level2 = 2,
    Level3 = 3,
    Level4 = 4,
    Default = 5,
    Level6 = 6,
    Level7 = 7,
    Level8 = 8,
    Level9 = 9,
    Best = 10
}

/// <summary>
/// Factory for creating dynamic resamplers with automatic backend selection.
/// </summary>
public static class DynamicResamplerFactory
{
    public static IDynamicResampler Create(int sampleRate, int channels = 1, ResamplerQuality quality = ResamplerQuality.Default)
    {
        if (NativeSpeexResampler.IsAvailable)
        {
            try
            {
                return new NativeSpeexResampler(sampleRate, channels, quality);
            }
            catch { }
        }
        return new LinearInterpolationResampler(channels);
    }
}

/// <summary>
/// Native SpeexDSP resampler using P/Invoke to libspeexdsp.
/// </summary>
public sealed class NativeSpeexResampler : IDynamicResampler
{
    private IntPtr _state;
    private readonly int _channels;
    private readonly int _baseSampleRate;
    private double _ratio = 1.0;
    private bool _disposed;

    private const double MinRatio = 0.96;
    private const double MaxRatio = 1.04;

    public static bool IsAvailable
    {
        get
        {
            try
            {
                _ = SpeexNative.speex_resampler_get_version();
                return true;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    public NativeSpeexResampler(int sampleRate, int channels = 1, ResamplerQuality quality = ResamplerQuality.Default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 8);

        _baseSampleRate = sampleRate;
        _channels = channels;

        _state = SpeexNative.speex_resampler_init(
            (uint)channels, (uint)sampleRate, (uint)sampleRate, (int)quality, out int err);

        if (_state == IntPtr.Zero || err != 0)
            throw new InvalidOperationException($"Failed to initialize Speex resampler. Error: {err}");

        SpeexNative.speex_resampler_skip_zeros(_state);
    }

    public double Ratio
    {
        get => _ratio;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var clamped = Math.Clamp(value, MinRatio, MaxRatio);
            if (Math.Abs(clamped - _ratio) < 0.0001) return;

            _ratio = clamped;
            const uint denominator = 10000;
            uint inRate = (uint)(_baseSampleRate * denominator);
            uint outRate = (uint)(_baseSampleRate * denominator / _ratio);

            int err = SpeexNative.speex_resampler_set_rate_frac(
                _state, inRate, outRate, (uint)_baseSampleRate, (uint)_baseSampleRate);
            if (err != 0)
                throw new InvalidOperationException($"Failed to set rate. Error: {err}");
        }
    }

    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.IsEmpty) return 0;

        uint inLen = (uint)(input.Length / _channels);
        uint outLen = (uint)(output.Length / _channels);

        int err;
        unsafe
        {
            fixed (float* inPtr = input)
            fixed (float* outPtr = output)
            {
                err = _channels == 1
                    ? SpeexNative.speex_resampler_process_float(_state, 0, inPtr, ref inLen, outPtr, ref outLen)
                    : SpeexNative.speex_resampler_process_interleaved_float(_state, inPtr, ref inLen, outPtr, ref outLen);
            }
        }
        if (err != 0) throw new InvalidOperationException($"Resampling failed. Error: {err}");
        return (int)(outLen * _channels);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SpeexNative.speex_resampler_reset_mem(_state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_state != IntPtr.Zero)
        {
            SpeexNative.speex_resampler_destroy(_state);
            _state = IntPtr.Zero;
        }
    }
}

internal static partial class SpeexNative
{
    private const string LibName = "libspeexdsp.so.1";

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial IntPtr speex_resampler_init(uint nb_channels, uint in_rate, uint out_rate, int quality, out int err);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void speex_resampler_destroy(IntPtr st);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static unsafe partial int speex_resampler_process_float(IntPtr st, uint channel_index, float* input, ref uint in_len, float* output, ref uint out_len);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static unsafe partial int speex_resampler_process_interleaved_float(IntPtr st, float* input, ref uint in_len, float* output, ref uint out_len);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial int speex_resampler_set_rate_frac(IntPtr st, uint ratio_num, uint ratio_den, uint in_rate, uint out_rate);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void speex_resampler_reset_mem(IntPtr st);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial int speex_resampler_skip_zeros(IntPtr st);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial IntPtr speex_resampler_get_version();
}

/// <summary>
/// Linear interpolation resampler fallback when native SpeexDSP is unavailable.
/// </summary>
public sealed class LinearInterpolationResampler : IDynamicResampler
{
    private readonly int _channels;
    private double _ratio = 1.0;
    private double _fractionalPosition;
    private float[] _lastSample;
    private bool _disposed;

    public LinearInterpolationResampler(int channels = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        _channels = channels;
        _lastSample = new float[channels];
    }

    public double Ratio
    {
        get => _ratio;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ratio = Math.Clamp(value, 0.96, 1.04);
        }
    }

    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.IsEmpty) return 0;

        int inputFrames = input.Length / _channels;
        int maxOutputFrames = output.Length / _channels;
        int outputFrameCount = 0;
        double position = _fractionalPosition;

        while (position < inputFrames - 1 && outputFrameCount < maxOutputFrames)
        {
            int index0 = (int)position;
            int index1 = index0 + 1;
            double frac = position - index0;

            for (int ch = 0; ch < _channels; ch++)
            {
                float sample0 = index0 >= 0 ? input[index0 * _channels + ch] : _lastSample[ch];
                float sample1 = input[index1 * _channels + ch];
                output[outputFrameCount * _channels + ch] = (float)(sample0 + (sample1 - sample0) * frac);
            }
            outputFrameCount++;
            position += _ratio;
        }

        if (inputFrames > 0)
        {
            int lastFrameStart = (inputFrames - 1) * _channels;
            for (int ch = 0; ch < _channels; ch++)
                _lastSample[ch] = input[lastFrameStart + ch];
        }

        _fractionalPosition = Math.Max(0, position - (inputFrames - 1));
        return outputFrameCount * _channels;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _fractionalPosition = 0;
        Array.Clear(_lastSample);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lastSample = null!;
    }
}
