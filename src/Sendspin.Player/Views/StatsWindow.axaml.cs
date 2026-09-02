using Avalonia.Controls;

namespace Sendspin.Player.Views;

/// <summary>
/// The Stats for Nerds window.
/// </summary>
/// <remarks>
/// One instance for the life of the main window, which keeps it (see <see cref="MainWindow"/>):
/// the user's close hides it rather than destroying it, so the Diagnostics row shows the same
/// window again. Every other close — shutdown from the tray, and the OS quitting the app, which
/// Avalonia delivers as a non-programmatic close with a shutdown reason — is let through.
/// </remarks>
public sealed partial class StatsWindow : Window
{
    public StatsWindow() => InitializeComponent();

    /// <summary>
    /// Whether a close is the user closing this window, as opposed to the app or the OS closing
    /// everything. <c>IsProgrammatic</c> alone is not the test: an OS-initiated quit closes each
    /// window non-programmatically too, and cancelling that one would keep the app alive.
    /// </summary>
    internal static bool IsUserClose(WindowClosingEventArgs e) =>
        !e.IsProgrammatic && e.CloseReason == WindowCloseReason.WindowClosing;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel || !IsUserClose(e))
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
