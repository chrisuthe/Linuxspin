using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Sendspin.Player.Views;
using Sendspin.SDK.Client;
using Sendspin.SDK.Discovery;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins Welcome: which cards each connection mode shows, the broadcasting indicator, the
/// searching and connecting states, and the manual section's default.
/// </summary>
[Collection(HeadlessCollection.Name)]
public sealed class WelcomeViewTests(HeadlessSession headless)
{
    [Fact]
    public void AdvertiseMode_ShowsTheWaitingCardAndTheBroadcastingIndicator() => headless.Run(() =>
    {
        using var shell = Shell.Show(s => s.ConnectionMode = ConnectionMode.AdvertiseOnly);
        var welcome = shell.Find<WelcomeView>("Welcome");

        Assert.True(shell.ViewModel.IsAdvertising);
        Assert.False(shell.ViewModel.IsDiscovering);

        Assert.True(shell.FindIn<Border>(welcome, "ThisPlayerCard").IsVisible);
        Assert.True(shell.FindIn<Border>(welcome, "WaitingCard").IsVisible);
        Assert.False(shell.FindIn<Border>(welcome, "ServersCard").IsVisible);
        Assert.True(shell.FindIn<StackPanel>(welcome, "BroadcastingRow").IsVisible);
        Assert.False(shell.FindIn<TextBlock>(welcome, "SearchingLine").IsEffectivelyVisible);

        var dot = shell.FindIn<Ellipse>(welcome, "BroadcastDot");
        Assert.Equal(shell.Resolve<Color>("SystemAccentColor"), Assert.IsAssignableFrom<ISolidColorBrush>(dot.Fill).Color);
        Assert.True(welcome.IsPulsing);

        // The pulse stops with the card: connecting hides Welcome.
        shell.ViewModel.IsConnected = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(welcome.IsPulsing);
        Assert.Equal(1.0, dot.Opacity);
    });

    [Fact]
    public void DiscoverMode_ShowsThePickerAndNoIndicator() => headless.Run(() =>
    {
        using var shell = Shell.Show(s => s.ConnectionMode = ConnectionMode.DiscoverOnly);
        var welcome = shell.Find<WelcomeView>("Welcome");

        Assert.True(shell.ViewModel.IsDiscovering);
        Assert.False(shell.ViewModel.IsAdvertising);

        Assert.True(shell.FindIn<Border>(welcome, "ServersCard").IsVisible);
        Assert.False(shell.FindIn<Border>(welcome, "WaitingCard").IsVisible);
        Assert.False(shell.FindIn<StackPanel>(welcome, "BroadcastingRow").IsVisible);
        Assert.False(welcome.IsPulsing);

        // Searching until the first server appears.
        var searching = shell.FindIn<TextBlock>(welcome, "SearchingLine");
        var list = shell.FindIn<ListBox>(welcome, "ServerList");
        Assert.True(shell.ViewModel.IsSearching);
        Assert.True(searching.IsVisible);
        Assert.Empty(list.Items);

        shell.ViewModel.DiscoveredServers.Add(new DiscoveredServer
        {
            ServerId = "srv-1",
            Name = "Living Room",
            Host = "10.0.0.5",
            Port = 8927,
            IpAddresses = ["10.0.0.5"],
        });
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.ViewModel.IsSearching);
        Assert.False(searching.IsVisible);
        Assert.Single(list.Items);
    });

    [Fact]
    public void Connecting_ReplacesTheActionWithALine() => headless.Run(() =>
    {
        using var shell = Shell.Show(s => s.ConnectionMode = ConnectionMode.DiscoverOnly);
        var welcome = shell.Find<WelcomeView>("Welcome");

        var connect = shell.FindIn<Button>(welcome, "ConnectButton");
        var connecting = shell.FindIn<TextBlock>(welcome, "ConnectingLine");
        var connectManually = shell.FindIn<Button>(welcome, "ConnectManuallyButton");
        var connectingManually = shell.FindIn<TextBlock>(welcome, "ConnectingManuallyLine");

        Assert.True(connect.IsVisible);
        Assert.False(connecting.IsVisible);
        Assert.Equal("Connecting…", connecting.Text);

        shell.ViewModel.IsConnecting = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(connect.IsVisible);
        Assert.True(connecting.IsVisible);
        Assert.False(connectManually.IsVisible);
        Assert.True(connectingManually.IsVisible);
    });

    [Fact]
    public void TheManualSection_IsCollapsedByDefaultInBothModes() => headless.Run(() =>
    {
        foreach (var mode in new[] { ConnectionMode.AdvertiseOnly, ConnectionMode.DiscoverOnly })
        {
            using var shell = Shell.Show(s => s.ConnectionMode = mode);
            var welcome = shell.Find<WelcomeView>("Welcome");

            var section = shell.FindIn<Expander>(welcome, "ManualSection");
            Assert.True(section.IsVisible);
            Assert.False(section.IsExpanded);
            Assert.Equal("Connect by address", section.Header);
            Assert.NotNull(shell.FindIn<TextBox>(welcome, "ManualUrlBox"));
        }
    });

    [Fact]
    public void ThePlayerName_WritesThrough() => headless.Run(() =>
    {
        using var shell = Shell.Show(s => s.PlayerName = "Study");
        var welcome = shell.Find<WelcomeView>("Welcome");

        var box = shell.FindIn<TextBox>(welcome, "PlayerNameBox");
        Assert.Equal("Study", box.Text);

        box.Text = "Kitchen";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Kitchen", shell.Settings.Current.PlayerName);
    });

    [Fact]
    public void TheCards_SitOnTheTranslucentSurface() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var welcome = shell.Find<WelcomeView>("Welcome");

        var card = shell.FindIn<Border>(welcome, "ThisPlayerCard");
        Assert.Same(shell.Resolve<ISolidColorBrush>("TranslucentSurfaceBrush"), card.Background);
        Assert.Equal(new CornerRadius(8), card.CornerRadius);
        Assert.Equal(new Thickness(16), card.Padding);
    });
}
