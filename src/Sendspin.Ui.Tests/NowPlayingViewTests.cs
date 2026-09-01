using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Sendspin.Core.MediaSession;
using Sendspin.Player.Views;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins Now Playing: the two compositions and what switches them, the art tile, the progress
/// row's two states, and the transport's sizes.
/// </summary>
[Collection(HeadlessCollection.Name)]
public sealed class NowPlayingViewTests(HeadlessSession headless)
{
    [Fact]
    public void TheView_ShowsOnlyWhileConnected() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        var nowPlaying = shell.Find<NowPlayingView>("NowPlaying");
        var welcome = shell.Find<WelcomeView>("Welcome");

        Assert.False(nowPlaying.IsVisible);
        Assert.True(welcome.IsVisible);

        shell.ViewModel.IsConnected = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(nowPlaying.IsVisible);
        Assert.False(welcome.IsEffectivelyVisible);
    });

    [Fact]
    public void TheComposition_IsStackedAtTheDefaultWidthAndSplitAt800() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var (view, art, details, transport, title) = Connect(shell);

        // 440: the stacked column, centred.
        Assert.False(view.IsWide);
        Assert.True(details.Bounds.Top >= art.Bounds.Bottom, $"details at {details.Bounds.Top}, art ends at {art.Bounds.Bottom}");
        Assert.Equal(art.Bounds.Center.X, details.Bounds.Center.X, 1.0);
        Assert.Equal(HorizontalAlignment.Center, transport.HorizontalAlignment);
        Assert.Equal(TextAlignment.Center, title.TextAlignment);

        // 800: art on the left, the column on the right, left-aligned.
        shell.Window.Width = 800;
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.IsWide);
        Assert.True(details.Bounds.Left >= art.Bounds.Right, $"details at {details.Bounds.Left}, art ends at {art.Bounds.Right}");
        Assert.Equal(HorizontalAlignment.Left, transport.HorizontalAlignment);
        Assert.Equal(TextAlignment.Left, title.TextAlignment);

        // And back: one tree, switched by a class, not two.
        shell.Window.Width = 440;
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.IsWide);
        Assert.True(details.Bounds.Top >= art.Bounds.Bottom);
    });

    [Theory]
    [InlineData(639, false)]
    [InlineData(640, true)]
    [InlineData(800, true)]
    public void TheWideComposition_StartsAtTheThreshold(double width, bool expected) =>
        Assert.Equal(expected, NowPlayingView.IsWideFor(width));

    /// <remarks>
    /// Narrow: the width minus the margins, capped, unless the text beneath needs the height.
    /// Wide: the height minus the margins, capped, unless the text column needs the width.
    /// </remarks>
    [Theory]
    [InlineData(false, 440, 700, 240, 320)]
    [InlineData(false, 360, 700, 240, 312)]
    [InlineData(false, 340, 700, 240, 292)]
    [InlineData(false, 440, 600, 240, 288)]
    [InlineData(false, 440, 460, 240, 148)]
    [InlineData(false, 440, 200, 240, NowPlayingView.ArtMinSize)]
    [InlineData(true, 800, 600, 240, 320)]
    [InlineData(true, 800, 300, 240, 252)]
    [InlineData(true, 640, 600, 240, 288)]
    public void TheArt_TakesTheRoomItHas(bool isWide, double width, double height, double detailsHeight, double expected) =>
        Assert.Equal(expected, NowPlayingView.ArtSizeFor(isWide, new Size(width, height), detailsHeight));

    [Fact]
    public void TheArtTile_IsClippedShadowedAndWrapped() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var (view, breath, _, _, _) = Connect(shell);

        var tile = shell.FindIn<Border>(view, "ArtTile");
        var clip = shell.FindIn<Border>(view, "ArtClip");
        var placeholder = shell.FindIn<PathIcon>(view, "ArtPlaceholder");

        // Square, within the caps, and no wider than the body allows.
        Assert.Equal(tile.Bounds.Width, tile.Bounds.Height, 0.5);
        Assert.InRange(tile.Bounds.Width, NowPlayingView.ArtMinSize, NowPlayingView.ArtMaxSize);
        Assert.True(tile.Bounds.Width <= view.Bounds.Width - 2 * NowPlayingView.EdgeMargin);

        // A BoxShadow in the token's colour, on the border that does not clip.
        Assert.Equal(1, tile.BoxShadow.Count);
        var shadow = tile.BoxShadow[0];
        Assert.True(shadow.Blur > 0);
        Assert.False(tile.ClipToBounds);
        Assert.Equal(shell.Resolve<ISolidColorBrush>("ArtShadowBrush").Color, shadow.Color);

        // Clipped at 12 px, placeholder brush and glyph while there is no art.
        Assert.Equal(new CornerRadius(12), clip.CornerRadius);
        Assert.True(clip.ClipToBounds);
        Assert.Same(shell.Resolve<ISolidColorBrush>("ArtPlaceholderBrush"), clip.Background);
        Assert.Null(shell.ViewModel.Artwork);
        Assert.True(placeholder.IsVisible);

        // The wrapper Phase 5 animates carries nothing today.
        Assert.Null(breath.RenderTransform);
        Assert.Same(tile, breath.Child);
    });

    [Fact]
    public void TheProgressRow_HidesTheBarForALiveStreamAndShowsBothTimesForATrack() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var (view, _, _, _, _) = Connect(shell);

        var bar = shell.FindIn<ProgressBar>(view, "TrackProgress");
        var elapsed = shell.FindIn<TextBlock>(view, "ElapsedText");
        var duration = shell.FindIn<TextBlock>(view, "DurationText");

        shell.ViewModel.ApplyState(new MediaSessionState
        {
            Status = MediaPlaybackStatus.Paused,
            Title = "Morning Edition",
            Position = TimeSpan.FromSeconds(221),
        });
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.ViewModel.HasKnownDuration);
        Assert.False(bar.IsVisible);
        Assert.Equal("3:41", elapsed.Text);
        Assert.Equal("LIVE", duration.Text);

        shell.ViewModel.ApplyState(new MediaSessionState
        {
            Status = MediaPlaybackStatus.Paused,
            Title = "Song",
            Duration = TimeSpan.FromSeconds(210),
            Position = TimeSpan.FromSeconds(65),
        });
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.ViewModel.HasKnownDuration);
        Assert.True(bar.IsVisible);
        Assert.Equal("1:05", elapsed.Text);
        Assert.Equal("3:30", duration.Text);
        Assert.Equal(65.0 / 210.0, bar.Value, 0.001);
        Assert.Equal(4, bar.Bounds.Height, 0.5);
    });

    [Fact]
    public void TheTrackText_IsSingleLineTrimmedAndTheAlbumHidesWhenNull() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var (view, _, _, _, title) = Connect(shell);

        var artist = shell.FindIn<TextBlock>(view, "ArtistText");
        var album = shell.FindIn<TextBlock>(view, "AlbumText");

        Assert.Equal("Nothing playing", title.Text);
        Assert.Contains("title", title.Classes);
        Assert.Contains("subtitle", artist.Classes);
        Assert.Contains("album", album.Classes);
        Assert.False(album.IsVisible);

        foreach (var line in new[] { title, artist, album })
        {
            Assert.Equal(TextTrimming.CharacterEllipsis, line.TextTrimming);
            Assert.Equal(TextWrapping.NoWrap, line.TextWrapping);
        }

        shell.ViewModel.ApplyState(new MediaSessionState { Title = "Song", Artist = "Band", Album = "Record" });
        Dispatcher.UIThread.RunJobs();

        Assert.True(album.IsVisible);
        Assert.Equal("Record", album.Text);
    });

    [Fact]
    public void TheTransport_HasTheFiveButtonsAtTheirSizes() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var (view, _, _, transport, _) = Connect(shell);

        var shuffle = shell.FindIn<ToggleButton>(view, "ShuffleButton");
        var previous = shell.FindIn<Button>(view, "PreviousButton");
        var play = shell.FindIn<Button>(view, "PlayButton");
        var next = shell.FindIn<Button>(view, "NextButton");
        var repeat = shell.FindIn<ToggleButton>(view, "RepeatButton");

        Assert.Equal([shuffle, previous, play, next, repeat], transport.Children);
        Assert.Equal(40, shuffle.Bounds.Width);
        Assert.Equal(48, previous.Bounds.Width);
        Assert.Equal(72, play.Bounds.Width);
        Assert.Equal(48, next.Bounds.Width);
        Assert.Equal(40, repeat.Bounds.Width);
        Assert.Contains("playButton", play.Classes);
        Assert.Equal("Shuffle", ToolTip.GetTip(shuffle));
        Assert.Equal("Repeat off", ToolTip.GetTip(repeat));

        shell.ViewModel.ApplyState(new MediaSessionState { Shuffle = true, Repeat = MediaRepeatMode.One });
        Dispatcher.UIThread.RunJobs();

        Assert.True(shuffle.IsChecked);
        Assert.True(repeat.IsChecked);
        Assert.Equal("Repeat one", ToolTip.GetTip(repeat));
    });

    private static (NowPlayingView View, Border Art, StackPanel Details, StackPanel Transport, TextBlock Title) Connect(Shell shell)
    {
        shell.ViewModel.IsConnected = true;
        Dispatcher.UIThread.RunJobs();

        var view = shell.Find<NowPlayingView>("NowPlaying");

        return (
            view,
            shell.FindIn<Border>(view, "ArtBreath"),
            shell.FindIn<StackPanel>(view, "Details"),
            shell.FindIn<StackPanel>(view, "Transport"),
            shell.FindIn<TextBlock>(view, "TitleText"));
    }
}
