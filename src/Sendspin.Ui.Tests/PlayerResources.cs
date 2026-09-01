using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Sendspin.Ui.Tests;

/// <summary>
/// The player's resource dictionaries, loaded from the app assembly and merged into the
/// application of the current headless run.
/// </summary>
/// <remarks>
/// The real <c>App</c> is not used (see <see cref="TestApp"/>), so the dictionaries it merges
/// are merged here instead, against the same Fluent theme, which is what the DynamicResource
/// colours inside them resolve through. Every <see cref="HeadlessSession.Run"/> gets its own
/// isolated <see cref="Application"/>, so this is done per run and nothing is cached: brushes
/// and geometries are Avalonia objects owned by that run's dispatcher.
/// </remarks>
internal sealed class PlayerResources
{
    private PlayerResources(ResourceDictionary tokens, ResourceDictionary icons)
    {
        Tokens = tokens;
        Icons = icons;
    }

    public ResourceDictionary Tokens { get; }

    public ResourceDictionary Icons { get; }

    /// <summary>Loads both dictionaries and merges them into <see cref="Application.Current"/>.</summary>
    public static PlayerResources Merge()
    {
        Dispatcher.UIThread.VerifyAccess();

        var tokens = Load("Tokens");
        var icons = Load("Icons");

        var resources = Application.Current!.Resources;
        resources.MergedDictionaries.Add(tokens);
        resources.MergedDictionaries.Add(icons);

        return new PlayerResources(tokens, icons);
    }

    private static ResourceDictionary Load(string name) =>
        (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri($"avares://Sendspin.Player/Styles/{name}.axaml"));
}
