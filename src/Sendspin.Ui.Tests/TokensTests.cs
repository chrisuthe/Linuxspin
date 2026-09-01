using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Sendspin.Core.Theme;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Tests that every token in <c>Styles/Tokens.axaml</c> resolves, under both theme variants, to
/// a real colour.
/// </summary>
/// <remarks>
/// The tokens are brushes whose colours are DynamicResources into Fluent. A key that resolves
/// to a brush with no colour means that chain is broken — a misspelt Fluent key, or a dictionary
/// that never got an owner — and would render as nothing without any error.
/// </remarks>
[Collection(HeadlessCollection.Name)]
public sealed class TokensTests(HeadlessSession session)
{
    private static readonly ThemeVariant[] Variants = [ThemeVariant.Light, ThemeVariant.Dark];

    [Fact]
    public void BothVariants_DeclareTheSameTokens() => session.Run(() =>
    {
        var resources = PlayerResources.Merge();

        var light = VariantDictionary(resources, ThemeVariant.Light);
        var dark = VariantDictionary(resources, ThemeVariant.Dark);

        Assert.NotEmpty(light.Keys);
        Assert.Equal(light.Keys.OrderBy(k => k.ToString()), dark.Keys.OrderBy(k => k.ToString()));
    });

    [Fact]
    public void EveryToken_ResolvesToAColourUnderBothVariants() => session.Run(() =>
    {
        var resources = PlayerResources.Merge();
        var app = Application.Current!;

        foreach (var key in TokenKeys(resources))
        {
            foreach (var variant in Variants)
            {
                Assert.True(app.TryGetResource(key, variant, out var value), $"{key} under {variant}");
                var brush = Assert.IsAssignableFrom<ISolidColorBrush>(value);
                Assert.True(brush.Color != default, $"{key} under {variant} has no colour");
            }
        }
    });

    /// <remarks>
    /// The veil goes over artwork so that theme-foreground text reads; a veil that is the wrong
    /// shade for its variant is exactly the failure this pins.
    /// </remarks>
    [Fact]
    public void Veil_IsLightOnLightAndDarkOnDark() => session.Run(() =>
    {
        PlayerResources.Merge();

        var light = Resolve("VeilBrush", ThemeVariant.Light);
        var dark = Resolve("VeilBrush", ThemeVariant.Dark);

        Assert.True(Luminance(light.Color) > 0.5, $"light veil is {light.Color}");
        Assert.True(Luminance(dark.Color) < 0.5, $"dark veil is {dark.Color}");
        Assert.InRange(light.Opacity, 0.6, 0.9);
        Assert.Equal(light.Opacity, dark.Opacity);
    });

    private static IEnumerable<object> TokenKeys(PlayerResources resources) =>
        VariantDictionary(resources, ThemeVariant.Light).Keys.Concat(resources.Tokens.Keys);

    private static ResourceDictionary VariantDictionary(PlayerResources resources, ThemeVariant variant) =>
        (ResourceDictionary)resources.Tokens.ThemeDictionaries[variant];

    private static ISolidColorBrush Resolve(string key, ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value), key);
        return Assert.IsAssignableFrom<ISolidColorBrush>(value);
    }

    private static double Luminance(Color color) => AccentContrast.RelativeLuminance(color.R, color.G, color.B);
}
