using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sendspin.Core.Configuration;
using Sendspin.Player.ViewModels;

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
/// <para>
/// This window also keeps the one <see cref="StatsWindow"/>. It is shown while
/// <see cref="DiagnosticsViewModel.IsVisible"/> is set and this window is itself visible, and
/// hidden otherwise — so hiding to the tray takes it along and showing again brings it back, and
/// a start hidden in the tray does not leave a stray stats window on the desktop. It is not an
/// owned window: Avalonia hides owned windows with their owner but never re-shows them.
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

    private MainViewModel? _viewModel;
    private StatsWindow? _stats;

    public MainWindow()
    {
        InitializeComponent();
        ApplyPlatformHints();
        UpdateRootOpacity();
        UpdateToolbarInset();
    }

    /// <summary>Gets the Stats window, once it has been asked for.</summary>
    internal StatsWindow? Stats => _stats;

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
    /// configure. And keeps the Stats window with this one as it is shown and hidden.
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
        else if (change.Property == IsVisibleProperty)
        {
            UpdateStatsWindow();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is { } previous)
        {
            previous.Diagnostics.PropertyChanged -= OnDiagnosticsPropertyChanged;
            previous.StatsRequested -= OnStatsRequested;
        }

        _viewModel = DataContext as MainViewModel;

        if (_viewModel is { } current)
        {
            current.Diagnostics.PropertyChanged += OnDiagnosticsPropertyChanged;
            current.StatsRequested += OnStatsRequested;
        }

        UpdateStatsWindow();
    }

    /// <summary>
    /// Closes the Stats window with this one. Only a real close gets here: hiding to the tray
    /// goes through <see cref="OnClosing"/> and leaves both windows alive.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (_stats is { } stats)
        {
            stats.Closing -= OnStatsClosing;
            _stats = null;
            stats.Close();
        }
    }

    private void OnDiagnosticsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiagnosticsViewModel.IsVisible))
        {
            UpdateStatsWindow();
        }
    }

    private void OnStatsRequested(object? sender, EventArgs e) => _stats?.Activate();

    /// <summary>
    /// Records that the user closed the Stats window. The window itself cancels the close and
    /// hides (see <see cref="StatsWindow"/>); what has to happen here is the view model learning
    /// of it, so the refresh clock stops and the next start does not reopen it.
    /// </summary>
    private void OnStatsClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!e.IsProgrammatic)
        {
            _viewModel?.SetStatsVisible(false);
        }
    }

    private void UpdateStatsWindow()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (IsVisible && _viewModel.Diagnostics.IsVisible)
        {
            if (_stats is null)
            {
                _stats = new StatsWindow { DataContext = _viewModel.Diagnostics };
                _stats.Closing += OnStatsClosing;
            }

            if (!_stats.IsVisible)
            {
                _stats.Show();
            }
        }
        else if (_stats is { IsVisible: true })
        {
            _stats.Hide();
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
