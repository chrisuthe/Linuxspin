using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

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

    /// <summary>The least the wide composition's text column is given before the art shrinks.</summary>
    internal const double MinTextColumnWidth = 280;

    /// <summary>
    /// What the text and the transport are assumed to need before they have been measured.
    /// </summary>
    private const double DetailsHeightFallback = 240;

    public NowPlayingView()
    {
        InitializeComponent();

        // The shadow colour is read off a token brush, so it has to be re-read when the variant
        // flips; the brush itself follows, but a BoxShadow holds a Color, not a brush.
        ActualThemeVariantChanged += (_, _) => ApplyShadow();
    }

    /// <summary>Gets whether the wide composition is active.</summary>
    public bool IsWide => Composition.Classes.Contains("wide");

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

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Compose(e.NewSize);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyShadow();
    }

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

        // The text trims at the art's edge when stacked beneath it, and at the column's when beside it.
        TrackText.Width = isWide ? double.NaN : art;
    }

    private void ApplyShadow()
    {
        if (this.TryFindResource("ArtShadowBrush", ActualThemeVariant, out var resource)
            && resource is ISolidColorBrush brush)
        {
            ArtTile.BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 8, Blur = 24, Color = brush.Color });
        }
    }
}
