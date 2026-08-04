using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sendspin.Core.Configuration;

namespace Sendspin.Player.Views;

/// <summary>
/// The main window.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

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
}
