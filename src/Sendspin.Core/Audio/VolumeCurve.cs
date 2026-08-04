namespace Sendspin.Core.Audio;

/// <summary>
/// The Sendspin perceived-loudness mapping from a 0-100 protocol volume to a linear
/// amplitude multiplier.
/// </summary>
/// <remarks>
/// <para>
/// The spec fixes this as <c>amplitude = (volume / 100) ^ 1.5</c>.
/// </para>
/// <para>
/// <strong>The SDK already applies it.</strong> <c>AudioPipeline.SetVolume(int)</c> performs
/// this conversion before the value reaches <c>IAudioPlayer.Volume</c>, so a platform player
/// receives an amplitude that is already curved and must apply it <em>linearly</em>. A
/// platform that raises it to 1.5 again is not being cautious, it is halving the user's
/// volume: 50 becomes 0.354 correctly, or 0.125 if squared through the curve twice. That bug
/// was live on Windows.
/// </para>
/// <para>
/// So this type is not the thing that applies the curve — nothing in this repo should. It is
/// the written-down statement of what the curve is, used to assert against the SDK's actual
/// behaviour in a test, and to convert in the one direction the SDK does not
/// (<see cref="ToVolume(float)"/>). Volume is server-authoritative throughout: the server
/// sets it, the SDK converts it, the player multiplies by it once.
/// </para>
/// </remarks>
public static class VolumeCurve
{
    /// <summary>
    /// The spec-mandated exponent. Not configurable.
    /// </summary>
    public const double Exponent = 1.5;

    /// <summary>
    /// Converts a protocol volume (0-100, clamped) to a linear amplitude in 0.0-1.0.
    /// </summary>
    public static float ToAmplitude(int volume)
    {
        var clamped = Math.Clamp(volume, 0, 100);
        return (float)Math.Pow(clamped / 100.0, Exponent);
    }

    /// <summary>
    /// Converts a linear amplitude in 0.0-1.0 back to the nearest protocol volume,
    /// for reporting a locally-changed volume back to the server.
    /// </summary>
    public static int ToVolume(float amplitude)
    {
        var clamped = Math.Clamp(amplitude, 0f, 1f);
        return (int)Math.Round(Math.Pow(clamped, 1.0 / Exponent) * 100.0);
    }
}
