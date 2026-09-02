using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sendspin.Player.ViewModels;

namespace Sendspin.Player.Views;

/// <summary>
/// Now Playing: the art tile, the track text, the progress row and the transport.
/// </summary>
/// <remarks>
/// <para>
/// One tree, two compositions. The view's width picks the composition — the stacked column
/// below <see cref="WideThreshold"/>, art beside a text column at or above it — by setting the
/// <c>wide</c> class on the composition grid, and the styles in the axaml do the rest. That is a
/// class toggle rather than a container query because the test that pins which composition is
/// active, and Phase 5's breathing-art work, both read the switch off this one control.
/// </para>
/// <para>
/// The art's size is the one value the styles cannot express: the body width minus the margins
/// in the narrow composition, the body height in the wide one, whichever is smaller once the
/// text and transport have taken what they need, and never more than <see cref="ArtMaxSize"/>.
/// The tile's shadow is a <see cref="BoxShadow"/> — the cheap kind, by the spike's measurements
/// — and it is set here because a shadow takes a <see cref="Color"/>, and the colour is a token.
/// </para>
/// <para>
/// Breathing Art lives in a <see cref="BreathingArtAnimator"/> this view owns while attached. It
/// scales the <c>ArtBreath</c> wrapper and replaces the tile's shadow with a glow, only while that
/// style is in effect; the animator hands the shadow back when it stops, and this view leaves the
/// shadow alone while it runs.
/// </para>
/// </remarks>
public sealed partial class NowPlayingView : UserControl
{
    /// <summary>The view width at which the wide composition takes over.</summary>
    internal const double WideThreshold = 640;

    /// <summary>The art tile's largest size, in either composition.</summary>
    internal const double ArtMaxSize = 320;

    /// <summary>The art tile's smallest size, below which the text is worth more than the picture.</summary>
    internal const double ArtMinSize = 96;

    /// <summary>The composition's margin on every side.</summary>
    internal const double EdgeMargin = 24;

    /// <summary>The gap between the art and the text: below it when narrow, beside it when wide.</summary>
    internal const double ArtGap = 24;

    /// <summary>The least the text column is given before the art shrinks, beside it or beneath it.</summary>
    internal const double MinTextColumnWidth = 280;

    /// <summary>
    /// What the text and the transport are assumed to need before they have been measured.
    /// </summary>
    private const double DetailsHeightFallback = 240;

    private MainViewModel? _viewModel;
    private BreathingArtAnimator? _breath;
    private Window? _window;

    public NowPlayingView()
    {
        InitializeComponent();

        // The shadow colour is read off a token brush, so it has to be re-read when the variant
        // flips; the brush itself follows, but a BoxShadow holds a Color, not a brush.
        ActualThemeVariantChanged += (_, _) => ApplyShadow();
    }

    /// <summary>Gets whether the wide composition is active.</summary>
    public bool IsWide => Composition.Classes.Contains("wide");

    /// <summary>Gets the Breathing Art animator, while the view is attached over a view model.</summary>
    internal BreathingArtAnimator? Breath => _breath;

    /// <summary>Which composition a view of this width gets.</summary>
    internal static bool IsWideFor(double width) => width >= WideThreshold;

    /// <summary>
    /// The art tile's edge for a composition, the space the view has, and the height the text
    /// and transport need beneath the art in the narrow composition.
    /// </summary>
    internal static double ArtSizeFor(bool isWide, Size available, double detailsHeight)
    {
        var width = available.Width - 2 * EdgeMargin;
        var height = available.Height - 2 * EdgeMargin;

        var fit = isWide
            ? Math.Min(height, width - ArtGap - MinTextColumnWidth)
            : Math.Min(width, height - ArtGap - detailsHeight);

        return Math.Clamp(fit, ArtMinSize, ArtMaxSize);
    }

    /// <summary>
    /// The narrow composition's text column: the art's width, but never less than
    /// <see cref="MinTextColumnWidth"/> while the body has that to give.
    /// </summary>
    /// <remarks>
    /// A short body shrinks the art towards <see cref="ArtMinSize"/>, and a column that narrow
    /// trims the title to a word and leaves the progress row's two times no room beside the bar.
    /// The column is always a fixed width here, since a fixed width is what centres and trims it.
    /// </remarks>
    internal static double TextColumnWidthFor(double art, double availableWidth)
    {
        var body = Math.Max(availableWidth - 2 * EdgeMargin, 0);
        return Math.Min(Math.Max(art, MinTextColumnWidth), body);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Compose(e.NewSize);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        _viewModel = DataContext as MainViewModel;
        RebuildAnimator();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is { } window)
        {
            window.PropertyChanged += OnWindowPropertyChanged;
        }

        ApplyShadow();
        RebuildAnimator();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_window is { } window)
        {
            window.PropertyChanged -= OnWindowPropertyChanged;
            _window = null;
        }

        RebuildAnimator();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty)
        {
            UpdateBreathVisibility();
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
        {
            UpdateBreathVisibility();
        }
    }

    /// <summary>
    /// An animator exists exactly while the view is attached over a main view model; it is torn
    /// down (which rests the art) and rebuilt on either side changing, rather than re-pointed.
    /// </summary>
    private void RebuildAnimator()
    {
        _breath?.Dispose();
        _breath = null;

        if (_window is not null && _viewModel is { } viewModel)
        {
            _breath = new BreathingArtAnimator(ArtBreath, ArtTile, viewModel.Backdrop, ApplyShadow);
            UpdateBreathVisibility();
        }
    }

    /// <summary>On screen: this view visible, in a visible window. The style is the animator's half.</summary>
    private void UpdateBreathVisibility() =>
        _breath?.SetVisible(IsVisible && _window is { IsVisible: true });

    private void Compose(Size size)
    {
        var isWide = IsWideFor(size.Width);
        Composition.Classes.Set("wide", isWide);

        // Measured in the pass that raised SizeChanged, so these are current; neither depends
        // on the art's size, which is what keeps this from feeding back into another pass.
        var detailsHeight = TrackText.DesiredSize.Height + Transport.DesiredSize.Height;
        if (detailsHeight <= 0)
        {
            detailsHeight = DetailsHeightFallback;
        }

        var art = ArtSizeFor(isWide, size, detailsHeight);

        ArtTile.Width = art;
        ArtTile.Height = art;

        // The text trims at the column's edge when beside the art, and at the art's edge when
        // stacked beneath it, down to the column's floor.
        TrackText.Width = isWide ? double.NaN : TextColumnWidthFor(art, size.Width);
    }

    /// <summary>
    /// The resting shadow. Left alone while the art is breathing: the glow owns the shadow then,
    /// and puts this back through the same call when it stops.
    /// </summary>
    private void ApplyShadow()
    {
        if (_breath is { IsRunning: true })
        {
            return;
        }

        if (this.TryFindResource("ArtShadowBrush", ActualThemeVariant, out var resource)
            && resource is ISolidColorBrush brush)
        {
            ArtTile.BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 8, Blur = 24, Color = brush.Color });
        }
    }
}
