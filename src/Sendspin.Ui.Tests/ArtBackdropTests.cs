using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Sendspin.Core.MediaSession;
using Sendspin.Platform.Shared.Client;
using Sendspin.Player.ViewModels;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins the blurred-art backdrop: a 64×64 bitmap made once per artwork change, the flag that
/// shows the layer, and the veil over it.
/// </summary>
/// <remarks>
/// The headless platform decodes nothing — every loaded bitmap is a stub — so what is pinned
/// here is the pipeline: that a scaled bitmap of the right size is produced from the artwork
/// file, once, and released with it. The file still has to exist, so a real PNG is written.
/// </remarks>
[Collection(HeadlessCollection.Name)]
public sealed class ArtBackdropTests(HeadlessSession headless)
{
    /// <summary>A 1×1 PNG.</summary>
    private const string TinyPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    [Fact]
    public void TheBackdrop_Is64PxAndProducedOncePerArtworkChange() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        using var artwork = new ArtworkFiles();

        shell.ViewModel.ApplyConnection(new ConnectionChangedEventArgs(true, "Living Room"));
        shell.ViewModel.ApplyState(new MediaSessionState { Title = "Song", ArtworkFilePath = artwork.First });
        Dispatcher.UIThread.RunJobs();

        var backdrop = shell.ViewModel.ArtBackdrop;
        Assert.NotNull(backdrop);
        Assert.Equal(MainViewModel.BackdropSize, backdrop.PixelSize);
        Assert.NotNull(shell.ViewModel.Artwork);
        Assert.True(shell.ViewModel.HasArtBackdrop);

        // A state report with the same artwork is not a change.
        shell.ViewModel.ApplyState(new MediaSessionState { Title = "Song", Position = TimeSpan.FromSeconds(5), ArtworkFilePath = artwork.First });
        Dispatcher.UIThread.RunJobs();

        Assert.Same(backdrop, shell.ViewModel.ArtBackdrop);

        // A different file is.
        shell.ViewModel.ApplyState(new MediaSessionState { Title = "Next", ArtworkFilePath = artwork.Second });
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(backdrop, shell.ViewModel.ArtBackdrop);
        Assert.Equal(MainViewModel.BackdropSize, shell.ViewModel.ArtBackdrop!.PixelSize);

        // No artwork clears it.
        shell.ViewModel.ApplyState(new MediaSessionState { Title = "Talk" });
        Dispatcher.UIThread.RunJobs();

        Assert.Null(shell.ViewModel.ArtBackdrop);
        Assert.False(shell.ViewModel.HasArtBackdrop);
    });

    [Fact]
    public void TheBackdropLayer_ShowsWithTheVeilAndClearsOnDisconnect() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        using var artwork = new ArtworkFiles();

        var layer = shell.Find<Panel>("ArtBackdrop");
        var image = shell.Find<Image>("ArtBackdropImage");
        var veil = shell.Find<Border>("Veil");

        Assert.False(layer.IsVisible);
        Assert.IsType<BlurEffect>(image.Effect);

        shell.ViewModel.ApplyConnection(new ConnectionChangedEventArgs(true, "Living Room"));
        shell.ViewModel.ApplyState(new MediaSessionState { Title = "Song", ArtworkFilePath = artwork.First });
        Dispatcher.UIThread.RunJobs();

        Assert.True(layer.IsVisible);
        Assert.True(veil.IsVisible);
        Assert.Same(shell.ViewModel.ArtBackdrop, image.Source);

        shell.ViewModel.ApplyConnection(new ConnectionChangedEventArgs(false, null));
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.ViewModel.HasArtBackdrop);
        Assert.False(layer.IsVisible);
        Assert.False(veil.IsVisible);
        Assert.Null(shell.ViewModel.ArtBackdrop);
        Assert.Null(image.Source);
    });

    [Fact]
    public void TheBackdrop_StaysOffWhileNotConnected() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        using var artwork = new ArtworkFiles();

        // A late state report after a disconnect must not light the layer up.
        shell.ViewModel.ApplyState(new MediaSessionState { Title = "Song", ArtworkFilePath = artwork.First });
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(shell.ViewModel.ArtBackdrop);
        Assert.False(shell.ViewModel.HasArtBackdrop);
        Assert.False(shell.Find<Panel>("ArtBackdrop").IsVisible);
    });

    /// <summary>Two artwork files, deleted with the test.</summary>
    private sealed class ArtworkFiles : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "sendspin-ui-tests", Guid.NewGuid().ToString("n"));

        public ArtworkFiles()
        {
            Directory.CreateDirectory(_directory);

            var png = Convert.FromBase64String(TinyPng);
            First = Path.Combine(_directory, "first.png");
            Second = Path.Combine(_directory, "second.png");
            File.WriteAllBytes(First, png);
            File.WriteAllBytes(Second, png);
        }

        public string First { get; }

        public string Second { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
