using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Sendspin.Ui.Tests;

/// <summary>
/// The player's resource dictionaries and styles, loaded from the app assembly and merged into
/// the application of the current headless run.
/// </summary>
/// <remarks>
/// The real <c>App</c> is not used (see <see cref="TestApp"/>), so what it merges is merged here
/// instead, against the same Fluent theme, which is what the DynamicResource colours inside them
/// resolve through. Every <see cref="HeadlessSession.Run"/> gets its own isolated
/// <see cref="Application"/>, so this is done per run and nothing is cached: brushes, geometries
/// and styles are Avalonia objects owned by that run's dispatcher.
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

    /// <summary>
    /// Loads both dictionaries and the player's styles and merges them into
    /// <see cref="Application.Current"/>.
    /// </summary>
    public static PlayerResources Merge()
    {
        Dispatcher.UIThread.VerifyAccess();

        var tokens = LoadDictionary("Tokens");
        var icons = LoadDictionary("Icons");

        var app = Application.Current!;
        app.Resources.MergedDictionaries.Add(tokens);
        app.Resources.MergedDictionaries.Add(icons);
        app.Styles.Add((Styles)Load("PlayerStyles"));

        return new PlayerResources(tokens, icons);
    }

    private static ResourceDictionary LoadDictionary(string name) => (ResourceDictionary)Load(name);

    private static object Load(string name) =>
        AvaloniaXamlLoader.Load(new Uri($"avares://Sendspin.Player/Styles/{name}.axaml"));
}
