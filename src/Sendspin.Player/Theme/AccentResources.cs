using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Sendspin.Core.Theme;

namespace Sendspin.Player.Theme;

/// <summary>
/// Keeps the resources that depend on the accent's actual colour in step with it.
/// </summary>
/// <remarks>
/// Fluent updates <c>SystemAccentColor</c> itself. What it does not do is pick a readable glyph
/// colour for it — its text-on-accent brush is white whatever the accent — so
/// <c>OnAccentBrush</c>, which Tokens.axaml seeds as white, is recomputed here from the accent's
/// luminance every time the platform reports a colour change. Written into the application's own
/// dictionary rather than the token dictionary: an entry there wins the lookup over a merged one,
/// and DynamicResource consumers repaint on the write.
/// </remarks>
internal static class AccentResources
{
    /// <summary>The key Tokens.axaml declares and this class rewrites.</summary>
    public const string OnAccentKey = "OnAccentBrush";

    /// <summary>
    /// Sets the on-accent brush to black or white for the given accent.
    /// </summary>
    public static void Apply(IResourceDictionary resources, Color accent)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var black = AccentContrast.PrefersBlackText(accent.R, accent.G, accent.B);
        resources[OnAccentKey] = new ImmutableSolidColorBrush(black ? Colors.Black : Colors.White);
    }
}
