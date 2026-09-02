using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Sendspin.Core.Configuration;
using Sendspin.Core.Visualization;
using Sendspin.SDK.Models;

namespace Sendspin.Player.ViewModels;

/// <summary>
/// The theme facts the backdrop's swatch picks depend on: which variant is up, and the three
/// colours that stand in for swatches the palette does not carry.
/// </summary>
/// <param name="IsDark">Whether the dark variant is active.</param>
/// <param name="Background">The theme's background, behind everything: the base fill's fallback.</param>
/// <param name="Accent">The system accent: the primary blob's fallback, and the third's.</param>
/// <param name="GlowDefault">The <c>GlowDefaultBrush</c> token: the accent blob's fallback, and so the art glow's.</param>
public readonly record struct BackdropTheme(bool IsDark, Color Background, Color Accent, Color GlowDefault);

/// <summary>
/// Target state for the living backdrop. The views read these from their frame loops and ease
/// what they draw toward them.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is UI-thread state. The SDK raises the palette and the visualizer frames on
/// its own threads, and the settings service raises its change on whichever thread wrote; all of
/// them come in through <see cref="OnUiThread"/>, the one place that marshals, so the views need
/// no locks and the tests can call the <c>Apply</c> methods directly.
/// </para>
/// <para>
/// Ported from the WPF player's view model with two changes. The swatches follow the theme
/// variant: the palette carries a background and an "on" colour for each variant, and the dark
/// pair over a light window (or the other way round) is what the reference's dark-only chrome
/// never had to deal with. And the fallbacks are the theme's own colours, pushed in by the view
/// as <see cref="BackdropTheme"/>, rather than the reference's hard-coded purples.
/// </para>
/// </remarks>
public sealed partial class AmbientBackdropViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly IGraphicsContextProbe _probe;
    private readonly ILogger<AmbientBackdropViewModel> _logger;

    private double _intensity = 1.0;
    private bool _loggedPalette;
    private bool _loggedLoudness;
    private bool _loggedBeat;
    private ColorPalette? _palette;
    private PaletteSnapshot? _lastPalette;
    private bool _isDisposed;

    /// <summary>The style the user chose. What actually runs is <see cref="EffectiveMode"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveMode))]
    private BackdropMode _mode;

    /// <summary>
    /// Whether the renderer turned out to be a software rasteriser, which forces the backdrop off
    /// whatever the setting says. Set once by <see cref="ProbeRenderer"/>, after the first frame.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveMode))]
    private bool _isSoftwareRendering;

    /// <summary>Whether the effective style is Ambient Glow and a real palette has arrived.</summary>
    [ObservableProperty]
    private bool _isActive;

    public AmbientBackdropViewModel(SettingsService settings, IGraphicsContextProbe probe, ILogger<AmbientBackdropViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _probe = probe;
        _logger = logger;

        ApplySettings(settings.Current.Backdrop);
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Raised on the UI thread for each beat frame: 0.85 for a beat, 1.0 for a downbeat.</summary>
    public event EventHandler<double>? BeatTriggered;

    /// <summary>
    /// Gets the style that runs: the chosen one, or <see cref="BackdropMode.Off"/> while the
    /// window is drawing in software.
    /// </summary>
    public BackdropMode EffectiveMode => IsSoftwareRendering ? BackdropMode.Off : Mode;

    /// <summary>
    /// Gets the intensity the renderers use: the setting, floored at
    /// <see cref="AmbientMath.IntensityFloor"/> so the slider's 0 % is faint rather than dark.
    /// </summary>
    public double Intensity => Math.Max(AmbientMath.IntensityFloor, _intensity);

    /// <summary>Gets the energy target, 0 to 1, from the latest loudness frame; the views ease toward it.</summary>
    public double TargetEnergy { get; private set; }

    /// <summary>
    /// Gets whether the group is playing. Breathing Art breathes only while it is: the visualizer
    /// stream does not reliably resume on a same-track pause and resume, so liveness follows
    /// playback rather than energy.
    /// </summary>
    public bool IsPlaying { get; private set; }

    /// <summary>Gets the theme facts the fallbacks come from, as the view last pushed them.</summary>
    public BackdropTheme Theme { get; private set; }

    /// <summary>Gets the base fill's colour target.</summary>
    public Color BaseColor { get; private set; }

    /// <summary>Gets the first blob's colour target: the palette's primary.</summary>
    public Color BlobColor1 { get; private set; }

    /// <summary>Gets the second blob's colour target: the palette's accent, which the art glow uses too.</summary>
    public Color BlobColor2 { get; private set; }

    /// <summary>Gets the third blob's colour target: the palette's "on" colour for the variant.</summary>
    public Color BlobColor3 { get; private set; }

    /// <summary>Gets whether a palette with at least one swatch has arrived since the last reset.</summary>
    public bool HasPalette => _palette is not null;

    /// <summary>Gets how many palettes got past the duplicate check, for the test that pins it.</summary>
    internal int PalettesApplied { get; private set; }

    /// <summary>
    /// Takes a palette from the SDK, on whatever thread it arrived on.
    /// </summary>
    public void ReceivePalette(ColorPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        OnUiThread(() => ApplyColorPalette(palette));
    }

    /// <summary>
    /// Takes a visualizer frame from the SDK, on whatever thread it arrived on.
    /// </summary>
    public void ReceiveFrame(VisualizerFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        OnUiThread(() => ApplyVisualizerFrame(frame));
    }

    /// <summary>
    /// Applies a palette on the UI thread. A palette equal to the last one applied is dropped,
    /// the way the platform accent's duplicate reports are; an all-null palette clears.
    /// </summary>
    public void ApplyColorPalette(ColorPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        var snapshot = PaletteSnapshot.Of(palette);
        if (snapshot == _lastPalette)
        {
            return;
        }

        _lastPalette = snapshot;
        _palette = snapshot.IsEmpty ? null : palette;
        PalettesApplied++;

        // Once per session: the proof, in the log, that the color role is live on this path.
        if (!_loggedPalette && _palette is not null)
        {
            _loggedPalette = true;
            _logger.LogInformation(
                "Backdrop: first palette arrived (primary {Primary}, accent {Accent}, background dark {Dark}, light {Light})",
                palette.Primary, palette.Accent, palette.BackgroundDark, palette.BackgroundLight);
        }

        // A clear deactivates and leaves the colours where they are, so the view eases out from
        // wherever it was rather than snapping to the fallbacks.
        if (_palette is not null)
        {
            PickSwatches();
        }

        UpdateActive();
    }

    /// <summary>Applies one visualizer frame on the UI thread: loudness moves the target, a beat pulses.</summary>
    public void ApplyVisualizerFrame(VisualizerFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Loudness is { } loudness)
        {
            TargetEnergy = AmbientMath.NormalizeLoudness(loudness);

            if (!_loggedLoudness)
            {
                _loggedLoudness = true;
                _logger.LogInformation("Backdrop: first loudness frame arrived ({Loudness})", loudness);
            }
        }

        if (frame.IsDownbeat is { } downbeat)
        {
            BeatTriggered?.Invoke(this, downbeat ? 1.0 : 0.85);

            if (!_loggedBeat)
            {
                _loggedBeat = true;
                _logger.LogInformation("Backdrop: first beat frame arrived (downbeat {Downbeat})", downbeat);
            }
        }
    }

    /// <summary>
    /// Takes the theme facts from the view, which is where resources resolve, and re-picks the
    /// swatches for the variant.
    /// </summary>
    public void ApplyTheme(BackdropTheme theme)
    {
        Theme = theme;
        PickSwatches();
    }

    /// <summary>Records whether the group is playing, from the main view model's state.</summary>
    public void SetPlaying(bool playing) => IsPlaying = playing;

    /// <summary>
    /// Asks the probe whether a GPU is drawing this window, and forces the backdrop off if not.
    /// Called by the view after the first frame, when the answer means something.
    /// </summary>
    public void ProbeRenderer()
    {
        var hasGpu = _probe.HasGpuContext();

        // The evidence the decision rests on, the way the shell spike printed it, so a wrong
        // answer on some box can be read straight out of its log.
        _logger.LogInformation(
            "Renderer probe: {Verdict}; mapped graphics libraries [{Libraries}]; open DRM nodes [{Nodes}]",
            hasGpu ? "GPU context" : "no GPU context, backdrop forced off",
            string.Join(", ", MappedGraphicsProbe.MappedLibraries()),
            string.Join(", ", MappedGraphicsProbe.OpenDrmNodes()));

        IsSoftwareRendering = !hasGpu;
    }

    /// <summary>Returns to the idle state: no palette, no energy. The disconnect path.</summary>
    public void Reset()
    {
        _palette = null;
        _lastPalette = null;
        _loggedPalette = false;
        _loggedLoudness = false;
        _loggedBeat = false;
        TargetEnergy = 0.0;
        IsPlaying = false;
        UpdateActive();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _settings.Changed -= OnSettingsChanged;
    }

    partial void OnModeChanged(BackdropMode value) => UpdateActive();

    partial void OnIsSoftwareRenderingChanged(bool value) => UpdateActive();

    private static Color ToColor(RgbColor? rgb, Color fallback) =>
        rgb is { } c ? Color.FromRgb(c.R, c.G, c.B) : fallback;

    private void OnSettingsChanged(object? sender, PlayerSettings settings) =>
        OnUiThread(() => ApplySettings(settings.Backdrop));

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread: now if this is it, posted otherwise.
    /// Nothing runs after disposal, since a post can land after the owner has gone.
    /// </summary>
    private void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            if (!_isDisposed)
            {
                action();
            }

            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_isDisposed)
            {
                action();
            }
        });
    }

    private void ApplySettings(BackdropSettings backdrop)
    {
        Mode = backdrop.Mode;

        var intensity = Math.Clamp(backdrop.Intensity, 0.0, BackdropSettings.MaxIntensity);
        if (intensity != _intensity)
        {
            _intensity = intensity;
            OnPropertyChanged(nameof(Intensity));
        }
    }

    /// <summary>
    /// The variant decides which half of the palette is used: the dark background and the light
    /// "on" colour over a dark window, their light counterparts over a light one.
    /// </summary>
    private void PickSwatches()
    {
        var theme = Theme;
        var palette = _palette;

        BaseColor = ToColor(theme.IsDark ? palette?.BackgroundDark : palette?.BackgroundLight, theme.Background);
        BlobColor1 = ToColor(palette?.Primary, theme.Accent);
        BlobColor2 = ToColor(palette?.Accent, theme.GlowDefault);
        BlobColor3 = ToColor(theme.IsDark ? palette?.OnDark : palette?.OnLight, theme.Accent);
    }

    private void UpdateActive() => IsActive = EffectiveMode == BackdropMode.AmbientGlow && _palette is not null;

    /// <summary>The six swatches by value, for the duplicate check.</summary>
    private readonly record struct PaletteSnapshot(
        RgbColor? BackgroundDark,
        RgbColor? BackgroundLight,
        RgbColor? Primary,
        RgbColor? Accent,
        RgbColor? OnDark,
        RgbColor? OnLight)
    {
        public bool IsEmpty =>
            BackgroundDark is null && BackgroundLight is null && Primary is null
            && Accent is null && OnDark is null && OnLight is null;

        public static PaletteSnapshot Of(ColorPalette palette) => new(
            palette.BackgroundDark,
            palette.BackgroundLight,
            palette.Primary,
            palette.Accent,
            palette.OnDark,
            palette.OnLight);
    }
}
