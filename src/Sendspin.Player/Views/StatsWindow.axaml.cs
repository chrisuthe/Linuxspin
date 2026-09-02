using Avalonia.Controls;

namespace Sendspin.Player.Views;

/// <summary>
/// The Stats for Nerds window.
/// </summary>
/// <remarks>
/// One instance for the life of the main window, which owns it (see <see cref="MainWindow"/>):
/// the user's close hides it rather than destroying it, so the Diagnostics row shows the same
/// window again. Shutdown closes it programmatically, and that is let through.
/// </remarks>
public sealed partial class StatsWindow : Window
{
    public StatsWindow() => InitializeComponent();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (e.IsProgrammatic || e.Cancel)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }
}
