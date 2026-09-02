using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sendspin.Core.Configuration;
using Sendspin.Core.Visualization;
using Sendspin.Player.Threading;
using Sendspin.Player.ViewModels;

namespace Sendspin.Player.Views;

/// <summary>
/// Breathing Art: scales the art's wrapper and glows the tile with the loudness and beat signal
/// on the <see cref="AmbientBackdropViewModel"/>, while that is the effective style.
/// </summary>
/// <remarks>
/// <para>
/// The wrapper is <c>ArtBreath</c>, which carries only this <see cref="ScaleTransform"/> about
/// its centre; the tile's own shadow and clip are untouched by the scale. The glow is the tile's
/// <see cref="Border.BoxShadow"/>, not a <c>DropShadowEffect</c>: the effect table has the two
/// equal on a GPU and the box shadow five times cheaper on software raster. While the style is
/// anything else the wrapper rests at scale 1 and the tile keeps Now Playing's resting shadow,
/// which <paramref name="restShadow"/> puts back.
/// </para>
/// <para>
/// Same clock and gate rules as Ambient Glow: a <see cref="UiClock"/> at 16 ms, run only while
/// the art is on screen in a visible window and the style is Breathing Art, with the beat handler
/// attached and detached alongside. On top of the signal an idle breath of ±2 % at about five
/// seconds keeps the art alive between frames while the group plays; paused, everything eases
/// to rest.
/// </para>
/// </remarks>
internal sealed class BreathingArtAnimator : IDisposable
{
    /// <summary>The frame period, the same as Ambient Glow's.</summary>
    internal static readonly TimeSpan FramePeriod = TimeSpan.FromMilliseconds(16);

    /// <summary>The glow's blur at full strength, in pixels.</summary>
    internal const double MaxGlowBlur = 40.0;

    /// <summary>The glow's alpha at full strength.</summary>
    internal const double MaxGlowOpacity = 0.85;

    private const double EnergyTimeConstant = 0.45;
    private const double BeatHalfLife = 0.30;
    private const double BeatAttack = 0.06;
    private const double GlowColorTimeConstant = 0.8;
    private const double IdleBreathSpan = 0.02;
    private const double IdleBreathSpeed = 1.25;
    private const double PlayLevelTimeConstant = 0.45;

    private readonly ScaleTransform _scale = new(1.0, 1.0);
    private readonly Border _tile;
    private readonly AmbientBackdropViewModel _viewModel;
    private readonly Action _restShadow;
    private readonly RenderLoopGate _gate;
    private readonly UiClock _clock = new(FramePeriod);
    private readonly FrameProfile? _profile = FrameProfile.Create("Breathing Art");

    private bool _isVisible;
    private bool _isDisposed;

    private TimeSpan _lastTick;
    private double _energy;
    private double _pulse;
    private double _pulseTarget;
    private double _idlePhase;
    private double _playLevel;
    private double _glowR;
    private double _glowG;
    private double _glowB;
    private bool _glowColorInitialized;

    /// <param name="breath">The wrapper to scale.</param>
    /// <param name="tile">The tile to glow.</param>
    /// <param name="viewModel">The signal.</param>
    /// <param name="restShadow">Puts the tile's resting shadow back when the glow stops.</param>
    public BreathingArtAnimator(Border breath, Border tile, AmbientBackdropViewModel viewModel, Action restShadow)
    {
        ArgumentNullException.ThrowIfNull(breath);
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(restShadow);

        _tile = tile;
        _viewModel = viewModel;
        _restShadow = restShadow;
        _gate = new RenderLoopGate(Hook, Unhook);

        breath.RenderTransformOrigin = RelativePoint.Center;
        breath.RenderTransform = _scale;

        _clock.Tick += OnTick;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateGate();
    }

    /// <summary>Gets whether the loop is running.</summary>
    public bool IsRunning => _gate.IsRunning;

    /// <summary>Gets the clock pacing the loop.</summary>
    internal UiClock Clock => _clock;

    /// <summary>Gets the wrapper's scale, as last drawn.</summary>
    internal double Scale => _scale.ScaleX;

    /// <summary>Gets how many frames the loop has drawn.</summary>
    internal long Frames { get; private set; }

    /// <summary>
    /// Tells the animator whether the art is on screen: the view attached and visible, in a
    /// visible window. The style is read from the view model; this is the other half of the gate.
    /// </summary>
    public void SetVisible(bool visible)
    {
        _isVisible = visible;
        UpdateGate();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _gate.Update(false);
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _clock.Tick -= OnTick;
        _clock.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AmbientBackdropViewModel.EffectiveMode))
        {
            UpdateGate();
        }
    }

    private void UpdateGate() =>
        _gate.Update(!_isDisposed && _isVisible && _viewModel.EffectiveMode == BackdropMode.BreathingArt);

    private void Hook()
    {
        _lastTick = _clock.Elapsed;
        _viewModel.BeatTriggered += OnBeat;
        _clock.Start();
    }

    private void Unhook()
    {
        _clock.Stop();
        _viewModel.BeatTriggered -= OnBeat;
        ResetToRest();
    }

    private void OnBeat(object? sender, double strength) => _pulseTarget += strength;

    private void ResetToRest()
    {
        _energy = 0.0;
        _pulse = 0.0;
        _pulseTarget = 0.0;
        _playLevel = 0.0;
        _scale.ScaleX = 1.0;
        _scale.ScaleY = 1.0;
        _restShadow();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var dt = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (dt <= 0.0)
        {
            return;
        }

        // Alive only while playing: the play level and the energy target both ease to zero on a
        // pause, so the art settles rather than freezing mid-breath.
        var playing = _viewModel.IsPlaying;
        _playLevel = AmbientMath.Ease(_playLevel, playing ? 1.0 : 0.0, dt, PlayLevelTimeConstant);
        _energy = AmbientMath.Ease(_energy, playing ? _viewModel.TargetEnergy : 0.0, dt, EnergyTimeConstant);
        _pulseTarget = AmbientMath.Decay(_pulseTarget, dt, BeatHalfLife);
        _pulse = AmbientMath.Ease(_pulse, _pulseTarget, dt, BeatAttack);

        var intensity = _viewModel.Intensity;

        _idlePhase += dt;
        var idle = intensity * IdleBreathSpan * Math.Sin(_idlePhase * IdleBreathSpeed) * _playLevel;
        var scale = AmbientMath.BreathScale(_energy, _pulse, intensity) + idle;
        _scale.ScaleX = scale;
        _scale.ScaleY = scale;

        var glow = AmbientMath.BreathGlow(_energy, intensity) * _playLevel;

        var target = _viewModel.BlobColor2;
        if (!_glowColorInitialized)
        {
            (_glowR, _glowG, _glowB) = (target.R, target.G, target.B);
            _glowColorInitialized = true;
        }

        _glowR = AmbientMath.Ease(_glowR, target.R, dt, GlowColorTimeConstant);
        _glowG = AmbientMath.Ease(_glowG, target.G, dt, GlowColorTimeConstant);
        _glowB = AmbientMath.Ease(_glowB, target.B, dt, GlowColorTimeConstant);

        _tile.BoxShadow = new BoxShadows(new BoxShadow
        {
            Blur = glow * MaxGlowBlur,
            Color = Color.FromArgb(
                (byte)Math.Clamp(Math.Round(glow * MaxGlowOpacity * 255.0), 0, 255),
                (byte)Math.Clamp(Math.Round(_glowR), 0, 255),
                (byte)Math.Clamp(Math.Round(_glowG), 0, 255),
                (byte)Math.Clamp(Math.Round(_glowB), 0, 255)),
        });

        Frames++;
        _profile?.Tick();
    }
}
