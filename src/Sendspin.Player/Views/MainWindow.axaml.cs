using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sendspin.Core.Configuration;

namespace Sendspin.Player.Views;

/// <summary>
/// The main window.
/// </summary>
/// <remarks>
/// <para>
/// The shell belongs to the OS, so the per-platform choices here are hints, and everything that
/// depends on what the platform actually granted is read back from the window's own properties
/// as they change rather than assumed. Every choice is a measured fact from the "UI shell"
/// section of <c>docs/ARCHITECTURE.md</c>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Linux: nothing. The client-area hint is inert under KWin, and the window has an alpha channel
/// by default, so the layer stack starts with an opaque root.
/// </description></item>
/// <item><description>
/// Windows 11: Mica, then None as the Windows 10 fallback. When Mica is granted the opaque root
/// thins so the material shows through. The client area is not extended: whether the system
/// caption buttons survive that is still unverified.
/// </description></item>
/// <item><description>
/// macOS: the client area extends into the title strip (measured 28 px), and the toolbar insets
/// its content by that strip and leaves room for the traffic lights.
/// </description></item>
/// </list>
/// <para>
/// Nothing here reads the theme variant or the render scaling at <c>Opened</c>: on the Wayland
/// head both can still be the fallback then.
/// </para>
/// </remarks>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// The opaque root's opacity when Mica is granted: enough of the material shows through to
    /// read as Mica, and enough of the root stays for theme-foreground text to keep its contrast.
    /// </summary>
    internal const double MicaRootOpacity = 0.35;

    /// <summary>
    /// The width of the traffic-light cluster the toolbar leaves clear when it runs into the
    /// title strip. <see cref="Window.WindowDecorationMargin"/> reports only the strip's height
    /// (<c>0,28,0,0</c> measured), not the buttons' width, so this is the one figure not read
    /// from a property.
    /// </summary>
    internal const double TrafficLightsWidth = 78;

    public MainWindow()
    {
        InitializeComponent();
        ApplyPlatformHints();
        UpdateRootOpacity();
        UpdateToolbarInset();
    }

    /// <summary>The opaque root's opacity for what the platform granted.</summary>
    internal static double RootOpacityFor(WindowTransparencyLevel level) =>
        level == WindowTransparencyLevel.Mica ? MicaRootOpacity : 1.0;

    /// <summary>
    /// The toolbar's content inset for a decoration margin: the strip's height on top and room
    /// for the traffic lights on the left while extended into the decorations, nothing otherwise.
    /// </summary>
    internal static Thickness ToolbarInsetFor(bool isExtendedIntoDecorations, Thickness decorationMargin) =>
        isExtendedIntoDecorations
            ? new Thickness(decorationMargin.Left + TrafficLightsWidth, decorationMargin.Top, decorationMargin.Right, 0)
            : default;

    /// <summary>
    /// Hides to the tray, or asks the application to shut down, depending on the user's choice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Synchronous, and it disposes nothing. Teardown has exactly one owner,
    /// <c>App.OnShutdownRequested</c>; a window handler that also disposed the view model would
    /// race it for the audio pipeline.
    /// </para>
    /// <para>
    /// The application's shutdown mode is explicit rather than last-window-closed, because an
    /// endpoint whose window is shut should keep playing. That also means closing the window has
    /// to ask for shutdown itself when the user has not opted into close-to-tray, otherwise the
    /// process would linger with no window.
    /// </para>
    /// </remarks>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        var app = Application.Current as App;

        var closeToTray = app?.Services?.GetService<SettingsService>()?.Current.CloseToTray ?? false;

        if (closeToTray && !e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);

        if (!e.Cancel && !e.IsProgrammatic)
        {
            app?.RequestShutdown();
        }
    }

    /// <summary>
    /// Keeps the layer stack in step with what the platform granted, whenever it settles: the
    /// transparency level after the compositor answers, the decoration margin after the first
    /// configure.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ActualTransparencyLevelProperty)
        {
            UpdateRootOpacity();
        }
        else if (change.Property == WindowDecorationMarginProperty
                 || change.Property == IsExtendedIntoWindowDecorationsProperty)
        {
            UpdateToolbarInset();
        }
    }

    private void ApplyPlatformHints()
    {
        if (OperatingSystem.IsWindows())
        {
            TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None];
        }
        else if (OperatingSystem.IsMacOS())
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = -1;
        }
    }

    // Both null-guarded: the platform raises these properties from the base Window constructor
    // (the Wayland head sets IsExtendedIntoWindowDecorations there), before the XAML has loaded
    // and the named controls exist. The constructor applies both once the tree is up.

    private void UpdateRootOpacity()
    {
        if (OpaqueRoot is { } root)
        {
            root.Opacity = RootOpacityFor(ActualTransparencyLevel);
        }
    }

    private void UpdateToolbarInset()
    {
        if (ToolbarContent is { } toolbar)
        {
            toolbar.Margin = ToolbarInsetFor(IsExtendedIntoWindowDecorations, WindowDecorationMargin);
        }
    }
}
