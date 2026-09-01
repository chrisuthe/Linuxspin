namespace Sendspin.Core.Platform;

/// <summary>
/// Turns a desktop's interface font setting into a family name a font manager can be handed.
/// </summary>
/// <remarks>
/// <para>
/// The Settings portal serves <c>org.gnome.desktop.interface/font-name</c> as a Pango font
/// description — <c>"Cantarell 11"</c>, <c>"Ubuntu Bold 11"</c> — and the KDE backend writes it
/// with a double space (<c>"Noto Sans  10"</c>). Skia asks fontconfig for the family by name, and
/// fontconfig treats a family it does not know as a request for the default face, so a style word
/// or a size left on the end turns "the user's font" back into "DejaVu Sans". This strips the
/// size and the style words Pango recognises, and keeps the first family of a comma list.
/// </para>
/// </remarks>
public static class DesktopFontName
{
    /// <summary>
    /// Pango's style keywords, which can trail the family in a font description.
    /// </summary>
    private static readonly HashSet<string> StyleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Thin", "Ultra-Light", "Extra-Light", "Light", "Semi-Light", "Demi-Light", "Book", "Regular",
        "Medium", "Semi-Bold", "Demi-Bold", "Bold", "Ultra-Bold", "Extra-Bold", "Heavy", "Black",
        "Ultra-Black", "Extra-Black", "Italic", "Oblique", "Roman",
        "Ultra-Condensed", "Extra-Condensed", "Condensed", "Semi-Condensed",
        "Semi-Expanded", "Expanded", "Extra-Expanded", "Ultra-Expanded",
        "Normal", "Small-Caps",
        // Pango accepts the same words without the hyphen.
        "Ultralight", "Extralight", "Semilight", "Demilight", "Semibold", "Demibold", "Ultrabold",
        "Extrabold", "Ultrablack", "Extrablack", "Ultracondensed", "Extracondensed", "Semicondensed",
        "Semiexpanded", "Extraexpanded", "Ultraexpanded",
    };

    /// <summary>
    /// Extracts the family name, or null when nothing usable is left.
    /// </summary>
    public static string? ParseFamily(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var firstFamily = description.Split(',', 2)[0];
        var words = new List<string>(firstFamily.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        while (words.Count > 0 && IsSizeOrStyle(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }

        return words.Count == 0 ? null : string.Join(' ', words);
    }

    private static bool IsSizeOrStyle(string word) =>
        StyleWords.Contains(word) || IsSize(word);

    /// <summary>
    /// A Pango size is a number in points, or a number followed by <c>px</c>.
    /// </summary>
    private static bool IsSize(string word)
    {
        var digits = word.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? word[..^2] : word;
        return digits.Length > 0
            && double.TryParse(digits, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _);
    }
}
