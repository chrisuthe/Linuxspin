using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.Player;
using Sendspin.Player.ViewModels;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins the tray menu: the reference's items in the reference's order under the status line,
/// with the state carried in labels rather than checkmarks.
/// </summary>
/// <remarks>
/// The headless windowing platform has no tray, so <see cref="TrayIcon"/> is created over a null
/// implementation; the menu is built and bound regardless, which is what these tests read.
/// </remarks>
[Collection(HeadlessCollection.Name)]
public sealed class TrayMenuTests(HeadlessSession headless)
{
    [Fact]
    public void TheMenu_HasTheReferenceItemsInOrder() => headless.Run(() =>
    {
        using var tray = Tray.Attach();

        var entries = tray.Controller.Menu.Items
            .Select(item => item is NativeMenuItemSeparator ? "---" : ((NativeMenuItem)item).Header)
            .ToList();

        Assert.Equal(
            [
                "Not connected", "---",
                "Play", "Next", "Previous", "Switch Group", "---",
                "Mute", "Volume: 100 %", "---",
                "Show Sendspin Player", "Quit",
            ],
            entries);
    });

    [Fact]
    public void TheTransportItems_RunTheViewModelsCommandsAndFollowTheConnection() => headless.Run(() =>
    {
        using var tray = Tray.Attach();
        var viewModel = tray.ViewModel;

        Assert.Same(viewModel.PlayPauseCommand, tray.Item("Play").Command);
        Assert.Same(viewModel.NextCommand, tray.Item("Next").Command);
        Assert.Same(viewModel.PreviousCommand, tray.Item("Previous").Command);
        Assert.Same(viewModel.SwitchGroupCommand, tray.Item("Switch Group").Command);
        Assert.Same(viewModel.ToggleMuteCommand, tray.Item("Mute").Command);

        // Disconnected: nothing to drive, and the readout is never a button.
        Assert.All(new[] { "Play", "Next", "Previous", "Switch Group", "Mute" }, name => Assert.False(tray.Item(name).IsEnabled, name));
        Assert.False(tray.Item("Volume: 100 %").IsEnabled);
        Assert.True(tray.Item("Show Sendspin Player").IsEnabled);
        Assert.True(tray.Item("Quit").IsEnabled);

        viewModel.IsConnected = true;
        Dispatcher.UIThread.RunJobs();

        Assert.All(new[] { "Play", "Next", "Previous", "Switch Group", "Mute" }, name => Assert.True(tray.Item(name).IsEnabled, name));
        Assert.False(tray.Item("Volume: 100 %").IsEnabled);
    });

    [Fact]
    public void MuteAndTheVolumeReadout_FollowTheViewModel() => headless.Run(() =>
    {
        using var tray = Tray.Attach();
        var viewModel = tray.ViewModel;
        viewModel.IsConnected = true;

        var mute = tray.Item("Mute");
        var volume = tray.Item("Volume: 100 %");

        viewModel.Volume = 37;
        Assert.Equal("Volume: 37 %", volume.Header);

        mute.Command!.Execute(null);
        Assert.True(viewModel.IsMuted);
        Assert.Equal("Unmute", mute.Header);

        mute.Command.Execute(null);
        Assert.False(viewModel.IsMuted);
        Assert.Equal("Mute", mute.Header);
    });

    [Fact]
    public void Detach_UnbindsTheMenuFromTheViewModel() => headless.Run(() =>
    {
        using var tray = Tray.Attach();
        var mute = tray.Item("Mute");

        tray.Controller.Detach();
        tray.ViewModel.IsMuted = true;

        Assert.Null(mute.Command);
        Assert.Equal("Mute", mute.Header);
    });

    /// <summary>A controller attached to a real view model, detached and disposed with the test.</summary>
    private sealed class Tray : IDisposable
    {
        private Tray(TrayIconController controller, ShellGraph graph)
        {
            Controller = controller;
            ViewModel = graph.ViewModel;
        }

        public TrayIconController Controller { get; }

        public MainViewModel ViewModel { get; }

        public static Tray Attach()
        {
            PlayerResources.Merge();

            var graph = ShellViewModels.CreateMain();
            var controller = new TrayIconController(NullLogger<TrayIconController>.Instance);
            controller.Attach(graph.ViewModel);

            return new Tray(controller, graph);
        }

        /// <summary>The item whose header is <paramref name="header"/> right now.</summary>
        public NativeMenuItem Item(string header)
        {
            var item = Controller.Menu.Items.OfType<NativeMenuItem>().SingleOrDefault(i => i.Header == header);
            Assert.True(item is not null, $"no menu item headed {header}");
            return item!;
        }

        public void Dispose()
        {
            Controller.Detach();
            ViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
