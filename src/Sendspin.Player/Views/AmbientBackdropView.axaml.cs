using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Sendspin.Core.Visualization;
using Sendspin.Player.Threading;
using Sendspin.Player.ViewModels;

namespace Sendspin.Player.Views;

/// <summary>
/// Ambient Glow: the base fill and three drifting blobs, eased each frame toward the targets on
/// the <see cref="AmbientBackdropViewModel"/> that is its data context.
/// </summary>
/// <remarks>
/// <para>
/// The loop is a <see cref="UiClock"/> at <see cref="FramePeriod"/>, never
/// <c>RequestAnimationFrame</c>, a dispatcher timer or an Avalonia <c>Animation</c>: the clock
/// table in <c>docs/ARCHITECTURE.md</c> has the first and last spinning a core on the Wayland
/// head and the second ticking at a 100 ms quantum. It is gated on being attached, the
/// window being visible and the view model being active, so it stops when the style is Off,
/// when a palette has not arrived, when the window is hidden to the tray, and on disconnect.
/// </para>
/// <para>
/// The beat handler is attached and detached with the loop. It only ever adds to the pulse
/// target, which the loop decays; left subscribed while the loop is stopped it would pile up
/// and fire one huge pulse the moment the window came back.
/// </para>
/// <para>
/// The colours ease at their own, slower time constant and carry across a stop and start, so a
/// re-show resumes from where the glow was rather than snapping in from black. The drift phase
/// accumulates scaled by intensity, so the slider changes the drift's speed without a jump.
/// </para>
/// </remarks>
public sealed partial class AmbientBackdropView : UserControl
{
    /// <summary>The frame period: 62 Hz on every head measured, once the clock is a thread timer.</summary>
    internal static readonly TimeSpan FramePeriod = TimeSpan.FromMilliseconds(16);

    private const double EnergyTimeConstant = 0.45;
    private const double ColorTimeConstant = 0.8;
    private const double BeatHalfLife = 0.30;
    private const double BeatAttack = 0.06;

    private readonly RenderLoopGate _gate;
    private readonly SolidColorBrush _baseBrush = new();
    private readonly Blob[] _blobs;
    private readonly EasedColor _base = new();

    private UiClock? _clock;
    private AmbientBackdropViewModel? _viewModel;
    private Window? _window;
    private FrameProfile? _profile;

    private TimeSpan _lastTick;
    private double _energy;
    private double _pulse;
    private double _pulseTarget;
    private double _driftPhase;
    private bool _colorsInitialized;
    private bool _probed;

    public AmbientBackdropView()
    {
        InitializeComponent();

        BaseFill.Fill = _baseBrush;
        _blobs = [Blob.Attach(Blob1), Blob.Attach(Blob2), Blob.Attach(Blob3)];
        _gate = new RenderLoopGate(StartLoop, StopLoop);

        ActualThemeVariantChanged += (_, _) => PushTheme();
    }

    /// <summary>Gets whether the frame loop is running.</summary>
    internal bool IsRunning => _gate.IsRunning;

    /// <summary>Gets the clock pacing the loop, while attached.</summary>
    internal UiClock? Clock => _clock;

    /// <summary>Gets how many frames the loop has drawn since it was attached.</summary>
    internal long Frames { get; private set; }

    /// <summary>Gets the blobs' current scale, as last drawn.</summary>
    internal double BlobScale => _blobs[0].Scale.ScaleX;

    /// <summary>Gets the base fill's colour, as last drawn.</summary>
    internal Color BaseColor => _baseBrush.Color;

    /// <summary>Gets the blobs' centre colours, as last drawn.</summary>
    internal IReadOnlyList<Color> BlobColors => [.. _blobs.Select(b => b.Centre.Color)];

    /// <summary>
    /// The theme facts for the view model, read off the resources at the variant.
    /// </summary>
    internal BackdropTheme ReadTheme() => new(
        ActualThemeVariant == ThemeVariant.Dark,
        ResolveColor("SystemControlBackgroundAltHighBrush"),
        ResolveColor("SystemAccentColor"),
        ResolveColor("GlowDefaultBrush"));

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // Stop first, so the beat handler comes off the outgoing view model; swapping before
        // stopping would unsubscribe from the wrong instance and leave the old one attached.
        _gate.Update(false);

        if (_viewModel is { } previous)
        {
            previous.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as AmbientBackdropViewModel;

        if (_viewModel is { } current)
        {
            current.PropertyChanged += OnViewModelPropertyChanged;
            PushTheme();
        }

        UpdateGate();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _clock = new UiClock(FramePeriod);
        _clock.Tick += OnTick;
        _profile = FrameProfile.Create("Ambient Glow");

        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is { } window)
        {
            window.PropertyChanged += OnWindowPropertyChanged;
        }

        PushTheme();
        UpdateGate();
        ProbeAfterFirstFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _gate.Update(false);

        if (_window is { } window)
        {
            window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }

        if (_clock is { } clock)
        {
            clock.Tick -= OnTick;
            clock.Dispose();
            _clock = null;
        }
    }

    /// <summary>
    /// Runs the renderer probe once, after the first frame: the loader has mapped whatever it is
    /// going to map by then. One animation-frame callback, not re-armed, is the "after the first
    /// frame" signal; a background-priority post from inside it lands after that frame's work.
    /// </summary>
    private void ProbeAfterFirstFrame()
    {
        if (_probed || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        _probed = true;
        top.RequestAnimationFrame(_ => Dispatcher.UIThread.Post(
            () => _viewModel?.ProbeRenderer(),
            DispatcherPriority.Background));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AmbientBackdropViewModel.IsActive))
        {
            UpdateGate();
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
        {
            UpdateGate();
        }
    }

    /// <summary>
    /// Attached, in a visible window, and active: the one rule, re-evaluated from every input.
    /// The window's own visibility is the "effectively visible" that matters, since hiding to
    /// the tray leaves this control's own <c>IsVisible</c> true; the mode and the connection
    /// both come in through <see cref="AmbientBackdropViewModel.IsActive"/>.
    /// </summary>
    private void UpdateGate() =>
        _gate.Update(_clock is not null && _viewModel is { IsActive: true } && _window is { IsVisible: true });

    private void StartLoop()
    {
        _lastTick = _clock!.Elapsed;
        _viewModel!.BeatTriggered += OnBeat;
        _clock.Start();
    }

    private void StopLoop()
    {
        _clock?.Stop();
        _viewModel!.BeatTriggered -= OnBeat;
    }

    private void OnBeat(object? sender, double strength) => _pulseTarget += strength;

    private void OnTick(object? sender, EventArgs e)
    {
        if (_viewModel is not { } vm || _clock is not { } clock)
        {
            return;
        }

        var now = clock.Elapsed;
        var dt = (now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (dt <= 0.0)
        {
            return;
        }

        // Energy eases toward the target; the pulse eases fast toward a decaying target, so a
        // beat swells in rather than popping.
        _energy = AmbientMath.Ease(_energy, vm.TargetEnergy, dt, EnergyTimeConstant);
        _pulseTarget = AmbientMath.Decay(_pulseTarget, dt, BeatHalfLife);
        _pulse = AmbientMath.Ease(_pulse, _pulseTarget, dt, BeatAttack);

        var intensity = vm.Intensity;
        var scale = AmbientMath.BlobScale(_energy, _pulse, intensity);
        var opacity = AmbientMath.BlobOpacity(_energy, intensity);

        _blobs[0].Apply(scale, opacity);
        _blobs[1].Apply(scale * 0.95, opacity * 0.9);
        _blobs[2].Apply(scale * 1.05, opacity * 0.8);

        // The blobs sit in the interior, so the window's edges only ever see the faded part of a
        // gradient and never a hard-clipped bright core; the drift is gentle on top of that.
        _driftPhase += dt * intensity;
        var t = _driftPhase;
        _blobs[0].Move(-110.0 + (Math.Sin(t * 0.13) * 55.0), -150.0 + (Math.Cos(t * 0.11) * 45.0));
        _blobs[1].Move(120.0 + (Math.Sin((t * 0.09) + 2.0) * 60.0), 150.0 + (Math.Cos((t * 0.15) + 1.0) * 50.0));
        _blobs[2].Move(Math.Sin((t * 0.17) + 4.0) * 45.0, Math.Cos((t * 0.08) + 3.0) * 55.0);

        EaseColors(vm, dt);

        Frames++;
        _profile?.Tick();
    }

    private void EaseColors(AmbientBackdropViewModel vm, double dt)
    {
        if (!_colorsInitialized)
        {
            _blobs[0].Eased.Snap(vm.BlobColor1);
            _blobs[1].Eased.Snap(vm.BlobColor2);
            _blobs[2].Eased.Snap(vm.BlobColor3);
            _base.Snap(vm.BaseColor);
            _colorsInitialized = true;
        }

        _blobs[0].Centre.Color = _blobs[0].Eased.Toward(vm.BlobColor1, dt, ColorTimeConstant);
        _blobs[1].Centre.Color = _blobs[1].Eased.Toward(vm.BlobColor2, dt, ColorTimeConstant);
        _blobs[2].Centre.Color = _blobs[2].Eased.Toward(vm.BlobColor3, dt, ColorTimeConstant);

        var baseColor = _base.Toward(vm.BaseColor, dt, ColorTimeConstant);
        if (_baseBrush.Color != baseColor)
        {
            _baseBrush.Color = baseColor;
        }
    }

    private void PushTheme() => _viewModel?.ApplyTheme(ReadTheme());

    private Color ResolveColor(string key)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var resource))
        {
            switch (resource)
            {
                case Color color:
                    return color;
                case ISolidColorBrush brush:
                    return brush.Color;
            }
        }

        return default;
    }

    /// <summary>
    /// One blob: its ellipse, the transforms the loop drives, and the gradient stop it recolours.
    /// </summary>
    private sealed class Blob
    {
        private readonly Ellipse _ellipse;
        private readonly TranslateTransform _translate;

        private Blob(Ellipse ellipse, ScaleTransform scale, TranslateTransform translate, GradientStop centre)
        {
            _ellipse = ellipse;
            _translate = translate;
            Scale = scale;
            Centre = centre;
        }

        public ScaleTransform Scale { get; }

        public GradientStop Centre { get; }

        public EasedColor Eased { get; } = new();

        /// <summary>Gives the ellipse its gradient and transforms. The edge stop's default colour is transparent.</summary>
        public static Blob Attach(Ellipse ellipse)
        {
            var centre = new GradientStop { Offset = 0.0 };
            var edge = new GradientStop { Offset = 1.0 };
            ellipse.Fill = new RadialGradientBrush { GradientStops = { centre, edge } };

            var scale = new ScaleTransform(1.0, 1.0);
            var translate = new TranslateTransform();
            ellipse.RenderTransformOrigin = RelativePoint.Center;
            ellipse.RenderTransform = new TransformGroup { Children = { scale, translate } };

            return new Blob(ellipse, scale, translate, centre);
        }

        public void Apply(double scale, double opacity)
        {
            Scale.ScaleX = scale;
            Scale.ScaleY = scale;
            _ellipse.Opacity = Math.Clamp(opacity, 0.0, 1.0);
        }

        public void Move(double x, double y)
        {
            _translate.X = x;
            _translate.Y = y;
        }
    }

    /// <summary>A colour eased channel by channel, in doubles so the steps are smooth.</summary>
    private sealed class EasedColor
    {
        private double _r;
        private double _g;
        private double _b;

        public void Snap(Color color) => (_r, _g, _b) = (color.R, color.G, color.B);

        public Color Toward(Color target, double dt, double timeConstant)
        {
            _r = AmbientMath.Ease(_r, target.R, dt, timeConstant);
            _g = AmbientMath.Ease(_g, target.G, dt, timeConstant);
            _b = AmbientMath.Ease(_b, target.B, dt, timeConstant);
            return Color.FromRgb(Channel(_r), Channel(_g), Channel(_b));
        }

        private static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
    }
}
