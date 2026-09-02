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
/// Pins Ambient Glow through the window: it renders on layer 0b only while connected, active and
/// visible, its loop is a <c>UiClock</c>, and the loop stops within a tick of the style going Off
/// or the window hiding.
/// </summary>
[Collection(HeadlessCollection.Name)]
public sealed class AmbientBackdropViewTests(HeadlessSession headless)
{
    private static readonly ColorPalette Palette = new()
    {
        BackgroundDark = new RgbColor(30, 30, 46),
        BackgroundLight = new RgbColor(240, 240, 250),
        Primary = new RgbColor(109, 40, 217),
        Accent = new RgbColor(6, 182, 212),
        OnDark = new RgbColor(219, 39, 119),
        OnLight = new RgbColor(60, 20, 90),
    };

    [Fact]
    public void TheGlow_RunsOnLayer0bOnlyWhileConnectedAndActive() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var layer = shell.Find<Panel>("AmbientBackdrop");
        var glow = shell.Find<AmbientBackdropView>("AmbientGlow");
        var veil = shell.Find<Border>("Veil");

        Assert.Same(shell.ViewModel.Backdrop, glow.DataContext);
        Assert.False(layer.IsVisible);
        Assert.False(glow.IsRunning);

        // A palette before the connection lights nothing: Reset cleared it, and the layer needs both.
        shell.ViewModel.ApplyConnection(new ConnectionChangedEventArgs(true, "Living Room"));
        shell.ViewModel.Backdrop.ApplyColorPalette(Palette);
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.ViewModel.HasAmbientBackdrop);
        Assert.True(layer.IsVisible);
        Assert.True(veil.IsVisible);
        Assert.True(glow.IsRunning);
        Assert.NotNull(glow.Clock);
        Assert.True(glow.Clock.IsRunning);
        Assert.Equal(AmbientBackdropView.FramePeriod, glow.Clock.Period);
        Assert.Equal(TimeSpan.FromMilliseconds(16), glow.Clock.Period);

        shell.ViewModel.ApplyConnection(new ConnectionChangedEventArgs(false, null));
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.ViewModel.HasAmbientBackdrop);
        Assert.False(shell.ViewModel.Backdrop.IsActive);
        Assert.False(layer.IsVisible);
        Assert.False(glow.IsRunning);
        Assert.False(glow.Clock.IsRunning);
    });

    [Fact]
    public void TheGlow_HidesTheBlurredArtWhileItRuns() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        using var artwork = new ArtworkFiles();

        shell.ViewModel.ApplyConnection(new ConnectionChangedEventArgs(true, "Living Room"));
        shell.ViewModel.ApplyState(new MediaSessionState { Title = "Song", ArtworkFilePath = artwork.First });
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.ViewModel.HasArtBackdrop);
        Assert.False(shell.ViewModel.HasAmbientBackdrop);

        shell.ViewModel.Backdrop.ApplyColorPalette(Palette);
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.ViewModel.HasAmbientBackdrop);
        Assert.False(shell.ViewModel.HasArtBackdrop);
        Assert.False(shell.Find<Panel>("ArtBackdrop").IsVisible);
        Assert.NotNull(shell.ViewModel.ArtBackdrop);

        // The glow going off hands the layer back to the art.
        shell.Settings.Update(s => s.Backdrop.Mode = BackdropMode.Off);
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.ViewModel.HasAmbientBackdrop);
        Assert.True(shell.ViewModel.HasArtBackdrop);
        Assert.True(shell.Find<Panel>("ArtBackdrop").IsVisible);
    });

    /// <remarks>
    /// The loop is a <c>UiClock</c>, so a stop takes effect on the tick already posted: nothing
    /// after Off draws a frame.
    /// </remarks>
    [Fact]
    public void TheLoop_StopsWithinATickOfTheStyleGoingOff() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var glow = Start(shell);

        WaitFor(() => glow.Frames >= 3);
        Assert.True(glow.Frames >= 3, $"{glow.Frames} frames");
        Assert.True(glow.BlobScale > 0.0);

        shell.Settings.Update(s => s.Backdrop.Mode = BackdropMode.Off);
        Dispatcher.UIThread.RunJobs();

        Assert.False(glow.IsRunning);
        var frames = glow.Frames;

        Thread.Sleep(120);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(frames, glow.Frames);

        shell.Settings.Update(s => s.Backdrop.Mode = BackdropMode.AmbientGlow);
        Dispatcher.UIThread.RunJobs();

        Assert.True(glow.IsRunning);
    });

    [Fact]
    public void TheLoop_StopsWhenTheWindowHidesAndResumesWhenItShows() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var glow = Start(shell);

        shell.Window.Hide();
        Dispatcher.UIThread.RunJobs();

        Assert.False(glow.IsRunning);
        var frames = glow.Frames;

        Thread.Sleep(120);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(frames, glow.Frames);

        // Hidden, the layer's own flag is untouched: it is the window that stopped the loop.
        Assert.True(shell.ViewModel.HasAmbientBackdrop);

        shell.Window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(glow.IsRunning);
    });

    [Fact]
    public void TheLoop_EasesTheColoursTowardThePalette() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var glow = Start(shell);

        WaitFor(() => glow.Frames >= 2);

        // The first frame snaps to the targets, so from then on the colours are the palette's
        // picks for the variant, not a hard-coded fallback.
        var vm = shell.ViewModel.Backdrop;
        Assert.Equal(vm.BaseColor, glow.BaseColor);
        Assert.Equal([vm.BlobColor1, vm.BlobColor2, vm.BlobColor3], glow.BlobColors);
        Assert.Equal(Color.FromRgb(109, 40, 217), vm.BlobColor1);
        Assert.Equal(Color.FromRgb(6, 182, 212), vm.BlobColor2);
    });

    /// <remarks>The view hands the view model the theme it resolved, so the picks are the variant's.</remarks>
    [Fact]
    public void TheView_PushesTheThemeItResolves() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var glow = shell.Find<AmbientBackdropView>("AmbientGlow");
        var theme = glow.ReadTheme();
        var vm = shell.ViewModel.Backdrop;

        Assert.Equal(theme, vm.Theme);
        Assert.Equal(shell.Window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark, theme.IsDark);
        Assert.Equal(shell.Resolve<ISolidColorBrush>("SystemControlBackgroundAltHighBrush").Color, theme.Background);
        Assert.Equal(shell.Resolve<Color>("SystemAccentColor"), theme.Accent);
        Assert.Equal(shell.Resolve<ISolidColorBrush>("GlowDefaultBrush").Color, theme.GlowDefault);

        // No palette yet: the fallbacks are the theme's, and the blobs are the accent and the glow.
        vm.ApplyColorPalette(new ColorPalette { BackgroundDark = new RgbColor(1, 2, 3), BackgroundLight = new RgbColor(1, 2, 3) });
        Assert.Equal(theme.Accent, vm.BlobColor1);
        Assert.Equal(theme.GlowDefault, vm.BlobColor2);
        Assert.Equal(theme.Accent, vm.BlobColor3);
    });

    /// <remarks>
    /// The view runs the probe itself after the first frame, which the headless renderer does
    /// draw, so by the time a test looks the guard may already have spoken; it is asked again
    /// here so the assertion does not depend on that timing.
    /// </remarks>
    [Fact]
    public void TheSoftwareGuard_KeepsTheGlowOff() => headless.Run(() =>
    {
        using var shell = Shell.Show(hasGpu: false);
        shell.ViewModel.ApplyConnection(new ConnectionChangedEventArgs(true, "Living Room"));
        shell.ViewModel.Backdrop.ApplyColorPalette(Palette);
        var glow = shell.Find<AmbientBackdropView>("AmbientGlow");

        shell.ViewModel.Backdrop.ProbeRenderer();
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.ViewModel.Backdrop.HasPalette);

        Assert.True(shell.ViewModel.Backdrop.IsSoftwareRendering);
        Assert.False(glow.IsRunning);
        Assert.False(shell.ViewModel.HasAmbientBackdrop);
        Assert.False(shell.Find<Panel>("AmbientBackdrop").IsVisible);
    });

    [Fact]
    public void TheView_IsNotHitTestable() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var glow = shell.Find<AmbientBackdropView>("AmbientGlow");

        Assert.False(glow.IsHitTestVisible);
        Assert.True(glow.ClipToBounds);
    });

    private static AmbientBackdropView Start(Shell shell)
    {
        shell.ViewModel.ApplyConnection(new ConnectionChangedEventArgs(true, "Living Room"));
        shell.ViewModel.Backdrop.ApplyColorPalette(Palette);
        shell.ViewModel.Backdrop.ApplyVisualizerFrame(new VisualizerFrame { Loudness = 40000 });
        Dispatcher.UIThread.RunJobs();

        var glow = shell.Find<AmbientBackdropView>("AmbientGlow");
        Assert.True(glow.IsRunning);
        return glow;
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

    /// <summary>One artwork file, deleted with the test.</summary>
    private sealed class ArtworkFiles : IDisposable
    {
        private const string TinyPng =
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "sendspin-ui-tests", Guid.NewGuid().ToString("n"));

        public ArtworkFiles()
        {
            Directory.CreateDirectory(_directory);
            First = Path.Combine(_directory, "first.png");
            File.WriteAllBytes(First, Convert.FromBase64String(TinyPng));
        }

        public string First { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
