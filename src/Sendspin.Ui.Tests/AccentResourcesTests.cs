using Avalonia.Controls;
using Avalonia.Media;
using Sendspin.Player.Theme;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Tests that the on-accent brush follows the accent it is handed.
/// </summary>
/// <remarks>
/// In the headless collection, and on its dispatcher, although nothing here renders: a test
/// class outside it runs in parallel with the collection, and an Avalonia object created on an
/// xunit worker thread in the gap between one isolated run's teardown and the next's set-up
/// binds the dispatcher to that worker thread, which then fails the next run's set-up.
/// </remarks>
[Collection(HeadlessCollection.Name)]
public sealed class AccentResourcesTests(HeadlessSession session)
{
    [Fact]
    public void Apply_PutsBlackGlyphsOnALightHighlight() => session.Run(() =>
    {
        var resources = new ResourceDictionary();

        AccentResources.Apply(resources, Color.Parse("#3DAEE9"));

        Assert.Equal(Colors.Black, OnAccent(resources).Color);
    });

    [Fact]
    public void Apply_PutsWhiteGlyphsOnASaturatedAccent() => session.Run(() =>
    {
        var resources = new ResourceDictionary();

        AccentResources.Apply(resources, Color.Parse("#0078D7"));

        Assert.Equal(Colors.White, OnAccent(resources).Color);
    });

    /// <remarks>
    /// The platform reports colour changes for as long as the app runs; each one has to replace
    /// the last, not stack behind it.
    /// </remarks>
    [Fact]
    public void Apply_ReplacesThePreviousAnswer() => session.Run(() =>
    {
        var resources = new ResourceDictionary();

        AccentResources.Apply(resources, Color.Parse("#3DAEE9"));
        AccentResources.Apply(resources, Color.Parse("#A94C31"));

        Assert.Equal(Colors.White, OnAccent(resources).Color);
        Assert.Single(resources.Keys);
    });

    private static ISolidColorBrush OnAccent(ResourceDictionary resources) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(resources[AccentResources.OnAccentKey]);
}
