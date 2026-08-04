namespace Sendspin.Platform.Shared.Audio;

/// <summary>
/// A linear-interpolation resampler for the very small, continuously varying rate changes
/// that drift correction needs.
/// </summary>
/// <remarks>
/// <para>
/// Linear interpolation is the right tool at these ratios and would be the wrong tool at
/// others. Drift correction runs inside ±500 ppm, so every output frame lands within
/// 0.0005 frames of an input frame and the interpolation error is proportional to that
/// distance. A libspeexdsp P/Invoke would give a better answer to a question nobody is
/// asking here, at the cost of a native dependency on every platform — so it was removed
/// rather than kept.
/// </para>
/// <para>
/// Pull-shaped: the caller says how much output it needs and learns how much input it consumed,
/// which is what a fixed-size audio buffer actually requires. The fractional read position carries
/// across calls, and the caller retains the input this did not consume, so block boundaries do not
/// click.
/// </para>
/// <para>
/// Not thread-safe. It belongs to one render path.
/// </para>
/// </remarks>
public sealed class DriftResampler
{
    private readonly int _channels;

    private double _position;

    /// <param name="channels">Interleaved channel count.</param>
    public DriftResampler(int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        _channels = channels;
    }

    /// <summary>
    /// Resamples <paramref name="input"/> into <paramref name="output"/> at
    /// <paramref name="ratio"/> input frames per output frame.
    /// </summary>
    /// <param name="input">Interleaved input samples.</param>
    /// <param name="output">Interleaved output buffer.</param>
    /// <param name="ratio">
    /// Input frames consumed per output frame. Above 1.0 plays faster and consumes more input
    /// than it emits; below 1.0 plays slower.
    /// </param>
    /// <param name="inputSamplesConsumed">Input samples consumed, always a whole frame count.</param>
    /// <returns>
    /// Output samples written. Less than <paramref name="output"/>.Length when the input ran
    /// out, which the caller must treat as an underrun and pad.
    /// </returns>
    public int Resample(ReadOnlySpan<float> input, Span<float> output, double ratio, out int inputSamplesConsumed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ratio, 0.0);

        var inputFrames = input.Length / _channels;
        var outputFrames = output.Length / _channels;
        var written = 0;

        while (written < outputFrames)
        {
            var baseIndex = (int)Math.Floor(_position);
            var fraction = _position - baseIndex;

            // Interpolating into baseIndex + 1 means that frame has to exist. When it does
            // not, stop and let the caller supply more input on the next call: the retained
            // _position picks up exactly where this left off.
            if (baseIndex + 1 >= inputFrames)
            {
                break;
            }

            for (var channel = 0; channel < _channels; channel++)
            {
                var first = input[(baseIndex * _channels) + channel];
                var second = input[((baseIndex + 1) * _channels) + channel];

                output[(written * _channels) + channel] = (float)(first + ((second - first) * fraction));
            }

            written++;
            _position += ratio;
        }

        var consumedFrames = Math.Clamp((int)Math.Floor(_position), 0, inputFrames);

        if (consumedFrames > 0)
        {
            _position -= consumedFrames;
        }

        inputSamplesConsumed = consumedFrames * _channels;
        return written * _channels;
    }

    /// <summary>
    /// Returns the input frames needed to produce <paramref name="outputFrames"/>, with a
    /// two-frame margin for the interpolation lookahead and the retained fractional position.
    /// </summary>
    public int RequiredInputFrames(int outputFrames, double ratio) =>
        (int)Math.Ceiling((outputFrames * ratio) + _position) + 2;

    /// <summary>
    /// Discards the fractional read position. Call whenever the stream is not continuous with what
    /// came before — a buffer clear, a re-anchor, a device switch.
    /// </summary>
    /// <remarks>
    /// There is no sample history to clear: <see cref="Resample"/> subtracts the whole frames it
    /// consumed, so <c>_position</c> stays in <c>[0, 1)</c> and every interpolation reads two frames
    /// that are both inside the current input. Continuity across calls comes from the caller
    /// retaining the input it did not consume, not from this type keeping a copy.
    /// </remarks>
    public void Reset() => _position = 0.0;
}
