using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Sendspin.Core.Configuration;
using Sendspin.Core.MediaSession;
using Sendspin.Platform.Shared.Client;
using Sendspin.Player.Views;
using Sendspin.SDK.Models;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins Breathing Art: the wrapper scales and the tile glows through its box shadow only while
/// that style is in effect, and the art rests at scale 1 with the Phase 3 shadow otherwise.
/// </summary>
[Collection(HeadlessCollection.Name)]
public sealed class BreathingArtTests(HeadlessSession headless)
{
    /// <remarks>
    /// The palette goes in before the connection: the loop snaps its glow colour on its first
    /// frame and eases toward changes at 0.8 s after that, so a palette applied once frames are
    /// running would be caught mid-ease by the colour assertion.
    /// </remarks>
    [Fact]
    public void TheArt_BreathesAndGlowsWhileThatStyleIsUpAndPlaying() => headless.Run(() =>
    {
        using var shell = Shell.Show(s => s.Backdrop.Mode = BackdropMode.BreathingArt);
        shell.ViewModel.Backdrop.ApplyColorPalette(new ColorPalette { Accent = new RgbColor(6, 182, 212) });
        var (view, breath, tile) = Connect(shell);
        var animator = view.Breath;

        Assert.NotNull(animator);
        Assert.True(animator.IsRunning);
        Assert.Equal(BreathingArtAnimator.FramePeriod, animator.Clock.Period);
        Assert.Equal(TimeSpan.FromMilliseconds(16), animator.Clock.Period);
        Assert.True(animator.Clock.IsRunning);

        // The wrapper carries the scale about its centre; the tile keeps its own radius and clip.
        var scale = Assert.IsType<ScaleTransform>(breath.RenderTransform);
        Assert.Equal(Avalonia.RelativePoint.Center, breath.RenderTransformOrigin);

        shell.ViewModel.ApplyState(new MediaSessionState { Status = MediaPlaybackStatus.Playing, Title = "Song" });
        shell.ViewModel.Backdrop.ApplyVisualizerFrame(new VisualizerFrame { Loudness = 65535 });
        Dispatcher.UIThread.RunJobs();

        WaitFor(() => animator.Frames >= 12 && scale.ScaleX > 1.01);

        Assert.True(scale.ScaleX > 1.01, $"scale {scale.ScaleX}");
        Assert.Equal(scale.ScaleX, scale.ScaleY);
        Assert.True(scale.ScaleX <= 1.0 + 0.06 + 0.04 + 0.02, $"scale {scale.ScaleX}");

        Assert.Equal(1, tile.BoxShadow.Count);
        var glow = tile.BoxShadow[0];
        Assert.True(glow.Blur > 0, $"blur {glow.Blur}");
        Assert.True(glow.Blur <= BreathingArtAnimator.MaxGlowBlur);
        Assert.Equal(0, glow.OffsetY);
        Assert.True(glow.Color.A > 0);
        Assert.Equal((6, 182, 212), (glow.Color.R, glow.Color.G, glow.Color.B));

        // The layer stack is untouched: this style paints nothing behind the content.
        Assert.False(shell.ViewModel.HasAmbientBackdrop);
        Assert.False(shell.ViewModel.Backdrop.IsActive);
    });

    [Fact]
    public void AnyOtherStyle_LeavesTheArtAtRestWithThePhase3Shadow() => headless.Run(() =>
    {
        using var shell = Shell.Show(s => s.Backdrop.Mode = BackdropMode.BreathingArt);
        var (view, breath, tile) = Connect(shell);
        var animator = view.Breath!;
        var scale = Assert.IsType<ScaleTransform>(breath.RenderTransform);

        shell.ViewModel.ApplyState(new MediaSessionState { Status = MediaPlaybackStatus.Playing, Title = "Song" });
        shell.ViewModel.Backdrop.ApplyVisualizerFrame(new VisualizerFrame { Loudness = 65535 });
        WaitFor(() => scale.ScaleX > 1.01);
        Assert.True(scale.ScaleX > 1.01);

        shell.Settings.Update(s => s.Backdrop.Mode = BackdropMode.AmbientGlow);
        Dispatcher.UIThread.RunJobs();

        Assert.False(animator.IsRunning);
        AssertAtRest(shell, breath, tile);

        var frames = animator.Frames;
        Thread.Sleep(120);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(frames, animator.Frames);

        shell.Settings.Update(s => s.Backdrop.Mode = BackdropMode.Off);
        Dispatcher.UIThread.RunJobs();
        Assert.False(animator.IsRunning);
        AssertAtRest(shell, breath, tile);

        shell.Settings.Update(s => s.Backdrop.Mode = BackdropMode.BreathingArt);
        Dispatcher.UIThread.RunJobs();
        Assert.True(animator.IsRunning);
    });

    [Fact]
    public void TheDefaultStyle_NeverStartsTheAnimator() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var (view, breath, tile) = Connect(shell);

        Assert.NotNull(view.Breath);
        Assert.False(view.Breath.IsRunning);
        AssertAtRest(shell, breath, tile);
    });

    [Fact]
    public void TheAnimator_StopsWhenTheArtLeavesTheScreen() => headless.Run(() =>
    {
        using var shell = Shell.Show(s => s.Backdrop.Mode = BackdropMode.BreathingArt);
        var (view, _, _) = Connect(shell);
        var animator = view.Breath!;
        Assert.True(animator.IsRunning);

        // The settings card steps Now Playing aside.
        shell.ViewModel.IsSettingsOpen = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(animator.IsRunning);

        shell.ViewModel.IsSettingsOpen = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(animator.IsRunning);

        // Hidden to the tray.
        shell.Window.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.False(animator.IsRunning);

        shell.Window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(animator.IsRunning);

        // Disconnected: Now Playing is gone.
        shell.ViewModel.ApplyConnection(new ConnectionChangedEventArgs(false, null));
        Dispatcher.UIThread.RunJobs();
        Assert.False(animator.IsRunning);
    });

    /// <remarks>The probe may already have run off the first headless frame; asked again so the timing does not matter.</remarks>
    [Fact]
    public void TheSoftwareGuard_KeepsTheArtAtRest() => headless.Run(() =>
    {
        using var shell = Shell.Show(s => s.Backdrop.Mode = BackdropMode.BreathingArt, hasGpu: false);
        var (view, breath, tile) = Connect(shell);

        shell.ViewModel.Backdrop.ProbeRenderer();
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.ViewModel.Backdrop.IsSoftwareRendering);
        Assert.Equal(BackdropMode.BreathingArt, shell.ViewModel.Backdrop.Mode);
        Assert.False(view.Breath!.IsRunning);
        AssertAtRest(shell, breath, tile);
    });

    private static void AssertAtRest(Shell shell, Border breath, Border tile)
    {
        var scale = Assert.IsType<ScaleTransform>(breath.RenderTransform);
        Assert.Equal(1.0, scale.ScaleX);
        Assert.Equal(1.0, scale.ScaleY);

        Assert.Equal(1, tile.BoxShadow.Count);
        var shadow = tile.BoxShadow[0];
        Assert.Equal(8, shadow.OffsetY);
        Assert.Equal(24, shadow.Blur);
        Assert.Equal(shell.Resolve<ISolidColorBrush>("ArtShadowBrush").Color, shadow.Color);
    }

    private static (NowPlayingView View, Border Breath, Border Tile) Connect(Shell shell)
    {
        shell.ViewModel.ApplyConnection(new ConnectionChangedEventArgs(true, "Living Room"));
        Dispatcher.UIThread.RunJobs();

        var view = shell.Find<NowPlayingView>("NowPlaying");
        Assert.True(view.IsVisible);

        return (view, shell.FindIn<Border>(view, "ArtBreath"), shell.FindIn<Border>(view, "ArtTile"));
    }

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(15);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
