using Avalonia;
using Avalonia.Media;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Tests that every glyph in <c>Styles/Icons.axaml</c> is a geometry drawn on the 24-unit box
/// the PathIcon theme expects.
/// </summary>
[Collection(HeadlessCollection.Name)]
public sealed class IconsTests(HeadlessSession session)
{
    private static readonly string[] Required =
    [
        "PlayIcon", "PauseIcon", "PreviousIcon", "NextIcon", "ShuffleIcon", "RepeatIcon", "RepeatOneIcon",
        "SpeakerIcon", "MutedIcon", "SwitchGroupIcon", "GearIcon", "StatsIcon", "PlusIcon", "MinusIcon",
    ];

    [Fact]
    public void TheSetTheReskinNeeds_IsAllThere() => session.Run(() =>
    {
        var resources = PlayerResources.Merge();

        foreach (var key in Required)
        {
            Assert.True(resources.Icons.ContainsKey(key), key);
        }
    });

    /// <remarks>
    /// The PathIcon theme draws the geometry at its own coordinates inside a 24×24 box and lets
    /// a Viewbox scale the result, so a glyph that strays outside the box is clipped, and one
    /// drawn to a different box comes out the wrong size next to the others.
    /// </remarks>
    [Fact]
    public void EveryIcon_IsAGeometryInsideTheBox() => session.Run(() =>
    {
        var resources = PlayerResources.Merge();
        var box = new Rect(0, 0, 24, 24);

        Assert.NotEmpty(resources.Icons.Keys);

        foreach (var key in resources.Icons.Keys)
        {
            Assert.True(Application.Current!.TryGetResource(key, null, out var value), key.ToString());
            var geometry = Assert.IsAssignableFrom<Geometry>(value);
            var bounds = geometry.Bounds;

            // A straight line, like minus, has one zero dimension and is still a glyph.
            Assert.True(bounds.Width > 0 || bounds.Height > 0, $"{key} is empty");
            Assert.True(box.Contains(bounds.TopLeft) && box.Contains(bounds.BottomRight), $"{key} spans {bounds}");
        }
    });
}
