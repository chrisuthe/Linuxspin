using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Sendspin.Player.Behaviors;
using Sendspin.Player.Views;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins that a wheel notch over a closed ComboBox scrolls the panel instead of rewriting the
/// bound value.
/// </summary>
/// <remarks>
/// The regression these guard against wrote a settings file: a stray scroll over the settings
/// panel stepped <c>preferred_codec</c> two places down the codec list, and the player then
/// advertised — correctly — the wrong codec. Nothing about that was visible at the time.
/// </remarks>
[Collection(HeadlessCollection.Name)]
public sealed class ComboBoxWheelGuardTests(HeadlessSession headless)
{
    private static readonly string[] Codecs = ["flac", "opus", "pcm"];

    /// <summary>Backdrop style, connection mode, auto-connect, output device and preferred codec.</summary>
    private const int SettingsComboBoxCount = 5;

    [Fact]
    public void AnUnguardedComboBoxStepsItsSelectionOnAWheelNotch() => headless.Run(() =>
    {
        var (window, comboBox, scrollViewer) = BuildPanel(guarded: false);

        Focus(comboBox);
        Wheel(window, comboBox);

        // Not an assertion about our own code: it pins the Avalonia behaviour the guard exists to
        // suppress, so an Avalonia that stops doing this says so here rather than leaving every
        // test below passing vacuously.
        Assert.Equal(1, comboBox.SelectedIndex);
        Assert.Equal(0, scrollViewer.Offset.Y);
    });

    [Fact]
    public void AGuardedComboBoxKeepsItsSelectionOnAWheelNotch() => headless.Run(() =>
    {
        var (window, comboBox, _) = BuildPanel(guarded: true);

        Focus(comboBox);
        Wheel(window, comboBox);

        Assert.Equal(0, comboBox.SelectedIndex);
        Assert.Equal("flac", comboBox.SelectedItem);
    });

    [Fact]
    public void AGuardedComboBoxStillLetsTheWheelScrollThePanel() => headless.Run(() =>
    {
        var (window, comboBox, scrollViewer) = BuildPanel(guarded: true);

        Focus(comboBox);
        Wheel(window, comboBox);

        Assert.True(scrollViewer.Offset.Y > 0);
    });

    [Fact]
    public void AnUnfocusedGuardedComboBoxScrollsThePanelJustTheSame() => headless.Run(() =>
    {
        var (window, comboBox, scrollViewer) = BuildPanel(guarded: true);

        Assert.False(comboBox.IsFocused);
        Wheel(window, comboBox);

        Assert.Equal(0, comboBox.SelectedIndex);
        Assert.True(scrollViewer.Offset.Y > 0);
    });

    [Fact]
    public void AnOpenDropDownKeepsTheWheelForItsOwnList() => headless.Run(() =>
    {
        var (window, comboBox, scrollViewer) = BuildPanel(guarded: true);

        Focus(comboBox);
        comboBox.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();

        Wheel(window, comboBox);

        // The guard stands aside while the dropdown is open, so the event stays with the popup and
        // the panel behind it does not move — exactly what happens with no guard at all.
        Assert.Equal(0, scrollViewer.Offset.Y);
    });

    [Fact]
    public void EverySettingsComboBoxIsGuarded() => headless.Run(() =>
    {
        // The panel draws glyphs from Icons.axaml, which the real App merges and this run must.
        PlayerResources.Merge();

        var window = new Window { Width = 340, Height = 600, Content = new SettingsView() };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var comboBoxes = window.GetLogicalDescendants().OfType<ComboBox>().ToList();

        // The count is asserted so that "every" means something: a picker declared inside a
        // template this walk never realizes would otherwise go unexamined, and the test would
        // pass on the four it did find. Adding a fifth to SettingsView.axaml is meant to land
        // here, where the guard is the thing being counted.
        Assert.Equal(SettingsComboBoxCount, comboBoxes.Count);

        // The panel's own style is what installs the guard, so this is the assertion that a
        // ComboBox added to SettingsView.axaml later is covered without anyone remembering to.
        Assert.All(comboBoxes, comboBox => Assert.True(ComboBoxWheelGuard.GetIsEnabled(comboBox)));
    });

    private static (Window Window, ComboBox ComboBox, ScrollViewer ScrollViewer) BuildPanel(bool guarded)
    {
        var comboBox = new ComboBox
        {
            ItemsSource = Codecs,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        if (guarded)
        {
            ComboBoxWheelGuard.SetIsEnabled(comboBox, true);
        }

        // Taller than the window, so the scroll viewer has somewhere to scroll to.
        var panel = new StackPanel { Height = 3000 };
        panel.Children.Add(comboBox);

        var scrollViewer = new ScrollViewer { Content = panel };
        var window = new Window { Width = 300, Height = 200, Content = scrollViewer };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (window, comboBox, scrollViewer);
    }

    private static void Focus(ComboBox comboBox)
    {
        comboBox.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(comboBox.IsFocused);
    }

    private static void Wheel(Window window, Visual over)
    {
        var point = over.TranslatePoint(new Point(5, 5), window) ?? new Point(5, 5);
        window.MouseWheel(point, new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();
    }
}
