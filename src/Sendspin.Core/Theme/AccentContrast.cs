namespace Sendspin.Core.Theme;

/// <summary>
/// Picks black or white for text drawn over the system accent.
/// </summary>
/// <remarks>
/// <para>
/// The accent is not one kind of colour. GNOME and Windows serve the saturated pick the user
/// made; Plasma serves the colour scheme's <em>highlight</em>, which in a light scheme is a light
/// derivative of the pick (see the "Theme and accent on Linux" section of
/// <c>docs/ARCHITECTURE.md</c>). Fluent's text-on-accent brush is white regardless, and white on
/// Plasma's light highlight is unreadable. So the glyph colour is computed from the accent's
/// relative luminance (WCAG 2.x, sRGB) rather than assumed.
/// </para>
/// <para>
/// The threshold is deliberately not the pure WCAG crossover. Black has the higher contrast ratio
/// from a luminance of 0.179 upward, which would put black glyphs on Windows blue (#0078D7,
/// 0.18) and on GNOME's blue (#3584E4, 0.23) — accents every user has only ever seen white on.
/// 0.3 sits between those saturated picks and the lightest measured highlights on Plasma
/// (#3DAEE9 at 0.37, #EF9277 at 0.40, Nordic's #8FBCBB at 0.45), so both kinds of accent get
/// the glyph their desktop would draw.
/// </para>
/// </remarks>
public static class AccentContrast
{
    /// <summary>
    /// The relative luminance above which black glyphs are used over the accent.
    /// </summary>
    public const double BlackTextLuminanceThreshold = 0.3;

    /// <summary>
    /// Returns true when black glyphs belong over the given accent, false when white does.
    /// </summary>
    public static bool PrefersBlackText(byte red, byte green, byte blue) =>
        RelativeLuminance(red, green, blue) > BlackTextLuminanceThreshold;

    /// <summary>
    /// The WCAG 2.x relative luminance of an sRGB colour, 0 for black through 1 for white.
    /// </summary>
    public static double RelativeLuminance(byte red, byte green, byte blue) =>
        (0.2126 * Linearize(red)) + (0.7152 * Linearize(green)) + (0.0722 * Linearize(blue));

    private static double Linearize(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
