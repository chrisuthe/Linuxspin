namespace Sendspin.Core.Visualization;

/// <summary>
/// Pure, dependency-free maths for the living backdrop: the loudness-to-energy mapping, the
/// easing and decay every animated value goes through, and the blob and art mappings.
/// </summary>
/// <remarks>
/// Ported verbatim from the WPF player's <c>AmbientMath</c>, constants included: they were tuned by
/// eye against real music, and the two apps are meant to breathe the same way. No Avalonia or SDK
/// types, so the view models adapt SDK frames into these primitives and the whole thing is
/// testable without a renderer.
/// </remarks>
public static class AmbientMath
{
    /// <summary>The largest raw loudness a visualizer loudness frame carries.</summary>
    public const int LoudnessMax = 65535;

    /// <summary>Blob scale at zero energy.</summary>
    public const double ScaleMin = 0.82;

    /// <summary>Blob scale added by energy (0 to 1).</summary>
    public const double ScaleEnergySpan = 0.50;

    /// <summary>Blob scale added by a full (1.0) beat pulse.</summary>
    public const double ScalePulseSpan = 0.35;

    /// <summary>Blob opacity at zero energy. The blobs stay clearly present when the music is quiet.</summary>
    public const double OpacityMin = 0.55;

    /// <summary>Blob opacity added by energy (0 to 1).</summary>
    public const double OpacityEnergySpan = 0.42;

    /// <summary>
    /// The least effective intensity. The slider's 0 % floors to this so the backdrop stays faintly
    /// alive rather than going dark; turning it off is what the Off style is for. Applied at the
    /// view-model boundary, not here.
    /// </summary>
    public const double IntensityFloor = 0.15;

    /// <summary>Art scale added by energy (0 to 1) at intensity 1.</summary>
    public const double BreathScaleEnergySpan = 0.06;

    /// <summary>Art scale added by a full beat pulse at intensity 1.</summary>
    public const double BreathScalePulseSpan = 0.04;

    /// <summary>The art's glow at zero energy, so it keeps a faint aura when quiet.</summary>
    public const double BreathGlowBase = 0.15;

    /// <summary>The art's glow added by energy (0 to 1).</summary>
    public const double BreathGlowEnergySpan = 0.85;

    /// <summary>
    /// Maps a raw loudness (0 to 65535, already dB-normalised by the server) to an energy of 0
    /// to 1. Null, meaning no loudness frame yet, maps to 0; out-of-range values clamp.
    /// </summary>
    public static double NormalizeLoudness(int? rawLoudness)
    {
        if (rawLoudness is not { } raw)
        {
            return 0.0;
        }

        return Math.Clamp(raw / (double)LoudnessMax, 0.0, 1.0);
    }

    /// <summary>
    /// Eases <paramref name="current"/> exponentially toward <paramref name="target"/> over a frame
    /// of <paramref name="dtSeconds"/>. The time constant is the e-folding time, so smaller is
    /// snappier; a non-positive one snaps to the target, and a non-positive frame is a no-op.
    /// Frame-rate independent.
    /// </summary>
    public static double Ease(double current, double target, double dtSeconds, double timeConstantSeconds)
    {
        if (dtSeconds <= 0.0)
        {
            return current;
        }

        if (timeConstantSeconds <= 0.0)
        {
            return target;
        }

        var alpha = 1.0 - Math.Exp(-dtSeconds / timeConstantSeconds);
        return current + ((target - current) * alpha);
    }

    /// <summary>
    /// Decays <paramref name="current"/> toward zero with the given half-life over a frame of
    /// <paramref name="dtSeconds"/>. This is the beat pulse's envelope: a beat adds an impulse,
    /// and each frame decays it. A non-positive half-life returns 0.
    /// </summary>
    public static double Decay(double current, double dtSeconds, double halfLifeSeconds)
    {
        if (dtSeconds <= 0.0)
        {
            return current;
        }

        if (halfLifeSeconds <= 0.0)
        {
            return 0.0;
        }

        return current * Math.Pow(0.5, dtSeconds / halfLifeSeconds);
    }

    /// <summary>
    /// The blobs' render scale from eased energy and the current beat pulse, scaled by
    /// <paramref name="intensity"/> (1 is the default). Energy and pulse clamp to 0 to 1 and the
    /// intensity to non-negative; <see cref="ScaleMin"/> is never scaled, so the blobs keep their
    /// resting size at intensity 0.
    /// </summary>
    public static double BlobScale(double energy, double pulse, double intensity = 1.0)
    {
        var e = Math.Clamp(energy, 0.0, 1.0);
        var p = Math.Clamp(pulse, 0.0, 1.0);
        var i = Math.Max(0.0, intensity);
        return ScaleMin + (i * ((e * ScaleEnergySpan) + (p * ScalePulseSpan)));
    }

    /// <summary>
    /// The blobs' opacity from eased energy, scaled by <paramref name="intensity"/>. The whole
    /// opacity scales, so intensity 0 is invisible; the result clamps to 0 to 1.
    /// </summary>
    public static double BlobOpacity(double energy, double intensity = 1.0)
    {
        var e = Math.Clamp(energy, 0.0, 1.0);
        var i = Math.Max(0.0, intensity);
        return Math.Clamp(i * (OpacityMin + (e * OpacityEnergySpan)), 0.0, 1.0);
    }

    /// <summary>
    /// The art's breathing scale from eased energy and the beat pulse, scaled by
    /// <paramref name="intensity"/>. Rests at 1: the art is never shrunk.
    /// </summary>
    public static double BreathScale(double energy, double pulse, double intensity = 1.0)
    {
        var e = Math.Clamp(energy, 0.0, 1.0);
        var p = Math.Clamp(pulse, 0.0, 1.0);
        var i = Math.Max(0.0, intensity);
        return 1.0 + (i * ((e * BreathScaleEnergySpan) + (p * BreathScalePulseSpan)));
    }

    /// <summary>
    /// The art's glow strength, 0 to 1, from eased energy scaled by <paramref name="intensity"/>.
    /// The animator maps it to a blur radius and an alpha.
    /// </summary>
    public static double BreathGlow(double energy, double intensity = 1.0)
    {
        var e = Math.Clamp(energy, 0.0, 1.0);
        var i = Math.Max(0.0, intensity);
        return Math.Clamp(i * (BreathGlowBase + (e * BreathGlowEnergySpan)), 0.0, 1.0);
    }
}
