using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sendspin.Player.ViewModels;
using Sendspin.Player.Views;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins the window shell: its geometry, the layer stack, the opaque-root rule, the toolbar, the
/// footer's two states, and the settings overlay.
/// </summary>
/// <remarks>
/// Everything here is found by name. The names are the contract Phases 3 to 5 fill layers in
/// against, so a rename that silently reshuffled the stack is meant to land here.
/// </remarks>
[Collection(HeadlessCollection.Name)]
public sealed class MainWindowShellTests(HeadlessSession headless)
{
    /// <summary>Bottom to top.</summary>
    private static readonly string[] Layers = ["OpaqueRoot", "ArtBackdrop", "AmbientBackdrop", "Veil", "ContentLayer"];

    [Fact]
    public void TheWindow_OpensAtTheShellGeometry() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        Assert.Equal("Sendspin Player", shell.Window.Title);
        Assert.Equal(440, shell.Window.Width);
        Assert.Equal(700, shell.Window.Height);
        Assert.Equal(400, shell.Window.MinWidth);
        Assert.Equal(560, shell.Window.MinHeight);
        Assert.True(shell.Window.CanResize);
        Assert.Equal(WindowStartupLocation.CenterScreen, shell.Window.WindowStartupLocation);
        Assert.Equal(new Size(440, 700), shell.Window.ClientSize);
    });

    [Fact]
    public void TheLayerStack_HasEveryLayerInOrder() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        var stack = Assert.IsType<Panel>(shell.Window.FindControl<Panel>("LayerStack"));
        Assert.Equal(Layers, stack.Children.Select(c => c.Name));

        Assert.IsType<Border>(stack.Children[0]);
        Assert.IsType<Image>(Assert.Single(Assert.IsType<Panel>(stack.Children[1]).Children));
        Assert.IsType<AmbientBackdropView>(Assert.Single(Assert.IsType<Panel>(stack.Children[2]).Children));
        Assert.IsType<Border>(stack.Children[3]);
        Assert.IsType<Grid>(stack.Children[4]);
    });

    [Fact]
    public void TheBackdropLayers_AreBoundAndOff() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        Assert.False(shell.ViewModel.HasArtBackdrop);
        Assert.False(shell.ViewModel.HasAmbientBackdrop);
        Assert.False(shell.ViewModel.HasBackdrop);

        Assert.False(shell.Find<Panel>("ArtBackdrop").IsVisible);
        Assert.False(shell.Find<Panel>("AmbientBackdrop").IsVisible);
        Assert.False(shell.Find<Border>("Veil").IsVisible);
        Assert.True(shell.Find<Grid>("ContentLayer").IsVisible);

        // Bound, not merely false: flipping the property is all Phase 3 has to do.
        shell.ViewModel.HasArtBackdrop = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.Find<Panel>("ArtBackdrop").IsVisible);
        Assert.True(shell.Find<Border>("Veil").IsVisible);
        Assert.False(shell.Find<Panel>("AmbientBackdrop").IsVisible);

        shell.ViewModel.HasArtBackdrop = false;
        shell.ViewModel.HasAmbientBackdrop = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.Find<Panel>("ArtBackdrop").IsVisible);
        Assert.True(shell.Find<Panel>("AmbientBackdrop").IsVisible);
        Assert.True(shell.Find<Border>("Veil").IsVisible);
    });

    [Fact]
    public void TheVeil_UsesTheVeilToken() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        Assert.True(Application.Current!.TryGetResource("VeilBrush", shell.Window.ActualThemeVariant, out var token));
        Assert.Same(token, shell.Find<Border>("Veil").Background);
    });

    /// <remarks>
    /// The token's 0.75 over the blurred art; 0.5 over the glow, so the glow's colour reads as
    /// colour. The border's opacity is what thins it, so the product is what is pinned.
    /// </remarks>
    [Fact]
    public void TheVeil_ThinsToHalfOverTheGlow() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var veil = shell.Find<Border>("Veil");
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(veil.Background);

        shell.ViewModel.HasArtBackdrop = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1.0, veil.Opacity);
        Assert.Equal(0.75, brush.Opacity * veil.Opacity, 0.001);

        shell.ViewModel.HasArtBackdrop = false;
        shell.ViewModel.HasAmbientBackdrop = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(MainViewModel.AmbientVeilFactor, veil.Opacity);
        Assert.Equal(0.5, brush.Opacity * veil.Opacity, 0.001);
    });

    /// <remarks>
    /// Headless grants no transparency, which is the Linux and Windows 10 case: the root is
    /// opaque. The Mica case cannot be granted here, so it is pinned on the rule itself.
    /// </remarks>
    [Fact]
    public void TheOpaqueRoot_IsOpaqueWhenMicaIsNotGranted() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        Assert.NotEqual(WindowTransparencyLevel.Mica, shell.Window.ActualTransparencyLevel);

        var root = shell.Find<Border>("OpaqueRoot");
        Assert.Equal(1.0, root.Opacity);

        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(root.Background);
        Assert.Equal(byte.MaxValue, brush.Color.A);
        Assert.Equal(1.0, brush.Opacity);
    });

    [Theory]
    [InlineData("None", 1.0)]
    [InlineData("Transparent", 1.0)]
    [InlineData("Blur", 1.0)]
    [InlineData("AcrylicBlur", 1.0)]
    [InlineData("Mica", MainWindow.MicaRootOpacity)]
    public void TheOpaqueRoot_ThinsOnlyForMica(string level, double expected)
    {
        var granted = typeof(WindowTransparencyLevel).GetProperty(level)!.GetValue(null)!;

        Assert.Equal(expected, MainWindow.RootOpacityFor((WindowTransparencyLevel)granted));
    }

    [Fact]
    public void TheMicaRootOpacity_LetsTheMaterialThrough() =>
        Assert.InRange(MainWindow.MicaRootOpacity, 0.25, 0.45);

    [Fact]
    public void TheToolbar_ShowsTheConnectionLineAndTheGearOnly() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        var line = shell.Find<TextBlock>("ConnectionLine");
        Assert.Equal("Not connected", line.Text);
        Assert.Contains("caption", line.Classes);

        var gear = shell.Find<ToggleButton>("SettingsButton");
        Assert.Contains("iconButton", gear.Classes);
        Assert.IsType<PathIcon>(gear.Content);
        Assert.True(gear.IsVisible);
        Assert.True(gear.Bounds.Width > 0);
        Assert.True(line.Bounds.Right <= gear.Bounds.Left);

        // The Phase 2 stats toggle is gone: diagnostics open from Settings now.
        var toolbar = shell.Find<Border>("Toolbar");
        Assert.Same(toolbar, gear.FindAncestorOfType<Border>());
        Assert.Null(shell.Window.FindControl<ToggleButton>("StatsButton"));
        Assert.Single(toolbar.GetLogicalDescendants().OfType<ToggleButton>());
    });

    [Fact]
    public void TheGear_TurnsAccentWhileSettingsAreOpen() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        var gear = shell.Find<ToggleButton>("SettingsButton");
        var glyph = Assert.IsType<PathIcon>(gear.Content);
        var accent = shell.Resolve<Color>("SystemAccentColor");

        Assert.False(gear.IsChecked);
        Assert.NotEqual(accent, Assert.IsAssignableFrom<ISolidColorBrush>(glyph.Foreground).Color);

        shell.ViewModel.IsSettingsOpen = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(gear.IsChecked);
        Assert.Equal(accent, Assert.IsAssignableFrom<ISolidColorBrush>(glyph.Foreground).Color);

        shell.ViewModel.IsSettingsOpen = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(gear.IsChecked);
        Assert.NotEqual(accent, Assert.IsAssignableFrom<ISolidColorBrush>(glyph.Foreground).Color);
    });

    [Fact]
    public void TheFooter_ShowsTheVolumeRowWhenConnected() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        shell.ViewModel.IsConnected = true;
        Dispatcher.UIThread.RunJobs();

        var footer = shell.Find<Border>("Footer");
        var row = shell.Find<Grid>("VolumeRow");
        Assert.True(row.IsVisible);
        Assert.True(row.IsEnabled);
        Assert.False(shell.Find<TextBlock>("FooterStatus").IsVisible);

        Assert.True(Application.Current!.TryGetResource("TranslucentSurfaceBrush", shell.Window.ActualThemeVariant, out var surface));
        Assert.Same(surface, footer.Background);

        var mute = shell.Find<ToggleButton>("MuteButton");
        var slider = shell.Find<Slider>("VolumeSlider");
        var percent = shell.Find<TextBlock>("VolumeText");
        var switchGroup = shell.Find<Button>("SwitchGroupButton");

        Assert.Contains("iconButton", mute.Classes);
        Assert.Contains("iconButton", switchGroup.Classes);
        Assert.Equal(HorizontalAlignment.Stretch, slider.HorizontalAlignment);
        Assert.Equal("100%", percent.Text);
        Assert.Contains("caption", percent.Classes);

        // Disconnect moved to Settings' Connection section; the footer has no button for it.
        Assert.Null(shell.Window.FindControl<Button>("DisconnectButton"));
        Assert.DoesNotContain(footer.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "Disconnect"));

        // Slim: the footer's icon buttons are the 36 px size, not the transport's 48.
        Assert.Equal(36, mute.Bounds.Width);
        Assert.Equal(36, switchGroup.Bounds.Width);

        // Left to right, in one row.
        Assert.True(mute.Bounds.Right <= slider.Bounds.Left);
        Assert.True(slider.Bounds.Right <= percent.Bounds.Left);
        Assert.True(percent.Bounds.Right <= switchGroup.Bounds.Left);

        // Stretched: the slider takes whatever the buttons and the percentage leave, so widening
        // the window widens the slider by the same amount — not the old fixed 140.
        var narrow = slider.Bounds.Width;
        Assert.True(narrow > 2 * mute.Bounds.Width, $"slider is {narrow} wide in a {row.Bounds.Width} row");

        shell.Window.Width = 640;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(narrow + 200, slider.Bounds.Width, 1.0);
    });

    [Fact]
    public void TheFooter_ShowsTheStatusMessageWhenDisconnectedWithOne() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        shell.ViewModel.IsConnected = false;
        shell.ViewModel.StatusMessage = "Could not connect to Living Room: refused.";
        Dispatcher.UIThread.RunJobs();

        var status = shell.Find<TextBlock>("FooterStatus");
        Assert.True(shell.ViewModel.HasFooterStatus);
        Assert.False(shell.Find<Grid>("VolumeRow").IsVisible);
        Assert.True(status.IsVisible);
        Assert.Equal("Could not connect to Living Room: refused.", status.Text);
        Assert.Contains("warning", status.Classes);

        // Connecting again puts the volume row back even while the message is still set.
        shell.ViewModel.IsConnected = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.ViewModel.HasFooterStatus);
        Assert.True(shell.Find<Grid>("VolumeRow").IsVisible);
        Assert.False(status.IsVisible);

        // And a disconnect with nothing to say collapses the footer: no disabled volume row.
        shell.ViewModel.IsConnected = false;
        shell.ViewModel.StatusMessage = null;
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.ViewModel.HasFooter);
        Assert.False(shell.Find<Border>("Footer").IsVisible);
        Assert.False(shell.Find<Grid>("VolumeRow").IsEffectivelyVisible);
        Assert.False(status.IsEffectivelyVisible);
    });

    [Fact]
    public void TheFooter_CollapsesWhileDisconnectedWithNothingToSay() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        var footer = shell.Find<Border>("Footer");
        var body = shell.Find<Panel>("Body");

        Assert.False(shell.ViewModel.IsConnected);
        Assert.Null(shell.ViewModel.StatusMessage);
        Assert.False(footer.IsVisible);
        Assert.Equal(0, footer.Bounds.Height);

        // The body takes the room the footer gave up.
        Assert.Equal(shell.Window.ClientSize.Height, body.Bounds.Bottom, 1.0);

        shell.ViewModel.IsConnected = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(footer.IsVisible);
        Assert.True(footer.Bounds.Height >= 52);
        Assert.True(shell.Find<Grid>("VolumeRow").IsVisible);
        Assert.True(shell.Find<Grid>("VolumeRow").IsEnabled);
    });

    [Fact]
    public void TheSwitchGroupButton_FollowsTheSetting() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        shell.ViewModel.IsConnected = true;
        Dispatcher.UIThread.RunJobs();

        var switchGroup = shell.Find<Button>("SwitchGroupButton");
        Assert.True(shell.Settings.Current.ShowSwitchGroupButton);
        Assert.True(switchGroup.IsVisible);

        shell.ViewModel.Settings.ShowSwitchGroupButton = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.Settings.Current.ShowSwitchGroupButton);
        Assert.False(switchGroup.IsVisible);

        // The rest of the row stays; the slider takes the space.
        Assert.True(shell.Find<Grid>("VolumeRow").IsVisible);
        Assert.True(shell.Find<Slider>("VolumeSlider").Bounds.Width > 0);

        shell.ViewModel.Settings.ShowSwitchGroupButton = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(switchGroup.IsVisible);
    });

    [Fact]
    public void TheTransport_SitsInNowPlayingNotTheFooter() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        shell.ViewModel.IsConnected = true;
        Dispatcher.UIThread.RunJobs();

        var nowPlaying = shell.Find<NowPlayingView>("NowPlaying");
        var transport = shell.FindIn<StackPanel>(nowPlaying, "Transport");
        var body = shell.Find<Panel>("Body");

        Assert.Contains(transport, body.GetVisualDescendants());
        Assert.DoesNotContain(transport, shell.Find<Border>("Footer").GetVisualDescendants());
        Assert.Contains(transport.Children, c => c.Classes.Contains("playButton"));

        // Below the art and the text, above the footer.
        var art = shell.FindIn<Border>(nowPlaying, "ArtBreath");
        var footer = shell.Find<Border>("Footer");
        Assert.True(Shell.TopIn(transport, shell.Window) > Shell.TopIn(art, shell.Window) + art.Bounds.Height);
        Assert.True(Shell.TopIn(transport, shell.Window) < Shell.TopIn(footer, shell.Window));
    });

    [Fact]
    public void TheSettingsOverlay_CoversTheBodyAtTheDefaultWidth() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        var overlay = shell.Find<Border>("SettingsOverlay");
        var body = shell.Find<Panel>("Body");

        Assert.False(overlay.IsVisible);

        shell.ViewModel.IsSettingsOpen = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(overlay.IsVisible);
        Assert.Contains(overlay, body.GetVisualDescendants());
        Assert.Single(overlay.GetLogicalDescendants().OfType<SettingsView>());

        // A card over the whole body, inset by its margin, rather than a column beside it.
        Assert.True(overlay.Bounds.Width >= body.Bounds.Width - 2 * overlay.Margin.Left - 1,
            $"overlay {overlay.Bounds.Width} wide in a {body.Bounds.Width} body");
        Assert.True(overlay.Bounds.Height >= body.Bounds.Height - 2 * overlay.Margin.Top - 1,
            $"overlay {overlay.Bounds.Height} tall in a {body.Bounds.Height} body");

        Assert.True(Application.Current!.TryGetResource("TranslucentSurfaceBrush", shell.Window.ActualThemeVariant, out var surface));
        Assert.Same(surface, overlay.Background);
    });

    [Fact]
    public void TheToolbarInset_FollowsTheDecorationMargin()
    {
        var macOS = new Thickness(0, 28, 0, 0);

        Assert.Equal(default, MainWindow.ToolbarInsetFor(false, macOS));
        Assert.Equal(new Thickness(MainWindow.TrafficLightsWidth, 28, 0, 0), MainWindow.ToolbarInsetFor(true, macOS));
        Assert.Equal(default, MainWindow.ToolbarInsetFor(false, default));
    }

    [Fact]
    public void TheToolbarInset_IsZeroWhereDecorationsAreNative() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        Assert.False(shell.Window.IsExtendedIntoWindowDecorations);
        Assert.Equal(default, shell.Find<Grid>("ToolbarContent").Margin);
    });
}
