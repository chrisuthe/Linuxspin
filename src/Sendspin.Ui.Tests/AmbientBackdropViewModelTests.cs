using Avalonia.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.Threading;
using Sendspin.Core.Configuration;
using Sendspin.Core.Visualization;
using Sendspin.Player.ViewModels;
using Sendspin.SDK.Models;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins the backdrop view model: the swatches follow the variant with the theme's own fallbacks,
/// activity follows the effective style and a palette, and the SDK's events land on the UI thread.
/// </summary>
[Collection(HeadlessCollection.Name)]
public sealed class AmbientBackdropViewModelTests(HeadlessSession headless)
{
    private static readonly Color ThemeBackground = Color.FromRgb(10, 11, 12);
    private static readonly Color ThemeAccent = Color.FromRgb(20, 21, 22);
    private static readonly Color ThemeGlow = Color.FromRgb(30, 31, 32);

    private static readonly ColorPalette Full = new()
    {
        BackgroundDark = new RgbColor(1, 1, 1),
        BackgroundLight = new RgbColor(2, 2, 2),
        Primary = new RgbColor(3, 3, 3),
        Accent = new RgbColor(4, 4, 4),
        OnDark = new RgbColor(5, 5, 5),
        OnLight = new RgbColor(6, 6, 6),
    };

    [Fact]
    public void TheSwatches_FollowTheVariant() => headless.Run(() =>
    {
        using var vm = Create();

        vm.ApplyTheme(Theme(isDark: true));
        vm.ApplyColorPalette(Full);

        Assert.Equal(Color.FromRgb(1, 1, 1), vm.BaseColor);
        Assert.Equal(Color.FromRgb(3, 3, 3), vm.BlobColor1);
        Assert.Equal(Color.FromRgb(4, 4, 4), vm.BlobColor2);
        Assert.Equal(Color.FromRgb(5, 5, 5), vm.BlobColor3);

        // The variant flips: the same palette, the other pair.
        vm.ApplyTheme(Theme(isDark: false));

        Assert.Equal(Color.FromRgb(2, 2, 2), vm.BaseColor);
        Assert.Equal(Color.FromRgb(3, 3, 3), vm.BlobColor1);
        Assert.Equal(Color.FromRgb(4, 4, 4), vm.BlobColor2);
        Assert.Equal(Color.FromRgb(6, 6, 6), vm.BlobColor3);
    });

    /// <remarks>
    /// A partial palette in each variant: what is missing comes from the theme, never from a
    /// hard-coded colour — the base from the theme's background, the primary and "on" blobs from
    /// the accent, the accent blob from the glow token.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingSwatches_FallBackToTheTheme(bool isDark) => headless.Run(() =>
    {
        using var vm = Create();
        vm.ApplyTheme(Theme(isDark));

        vm.ApplyColorPalette(new ColorPalette { Primary = new RgbColor(7, 7, 7) });

        Assert.Equal(ThemeBackground, vm.BaseColor);
        Assert.Equal(Color.FromRgb(7, 7, 7), vm.BlobColor1);
        Assert.Equal(ThemeGlow, vm.BlobColor2);
        Assert.Equal(ThemeAccent, vm.BlobColor3);
        Assert.True(vm.IsActive);
    });

    /// <remarks>Only the variant's own pair counts: the other variant's swatches are not a fallback.</remarks>
    [Fact]
    public void TheOtherVariantsSwatches_DoNotStandIn() => headless.Run(() =>
    {
        using var vm = Create();
        vm.ApplyTheme(Theme(isDark: true));

        vm.ApplyColorPalette(new ColorPalette { BackgroundLight = new RgbColor(2, 2, 2), OnLight = new RgbColor(6, 6, 6) });

        Assert.Equal(ThemeBackground, vm.BaseColor);
        Assert.Equal(ThemeAccent, vm.BlobColor3);
    });

    [Fact]
    public void IsActive_NeedsAmbientGlowAndAPalette() => headless.Run(() =>
    {
        using var vm = Create();
        Assert.False(vm.IsActive);
        Assert.False(vm.HasPalette);

        vm.ApplyColorPalette(Full);
        Assert.True(vm.IsActive);
        Assert.True(vm.HasPalette);

        vm.Mode = BackdropMode.BreathingArt;
        Assert.False(vm.IsActive);

        vm.Mode = BackdropMode.Off;
        Assert.False(vm.IsActive);

        vm.Mode = BackdropMode.AmbientGlow;
        Assert.True(vm.IsActive);

        // An all-null palette is a clear.
        vm.ApplyColorPalette(new ColorPalette());
        Assert.False(vm.IsActive);
        Assert.False(vm.HasPalette);
    });

    [Fact]
    public void SoftwareRendering_ForcesTheEffectiveStyleOff() => headless.Run(() =>
    {
        using var vm = Create(hasGpu: false);
        vm.ApplyColorPalette(Full);
        Assert.True(vm.IsActive);

        vm.ProbeRenderer();

        Assert.True(vm.IsSoftwareRendering);
        Assert.Equal(BackdropMode.AmbientGlow, vm.Mode);
        Assert.Equal(BackdropMode.Off, vm.EffectiveMode);
        Assert.False(vm.IsActive);

        vm.Mode = BackdropMode.BreathingArt;
        Assert.Equal(BackdropMode.Off, vm.EffectiveMode);
    });

    [Fact]
    public void AGpu_LeavesTheStyleAlone() => headless.Run(() =>
    {
        using var vm = Create(hasGpu: true);

        vm.ProbeRenderer();

        Assert.False(vm.IsSoftwareRendering);
        Assert.Equal(BackdropMode.AmbientGlow, vm.EffectiveMode);
    });

    [Fact]
    public void Reset_DropsThePaletteAndTheEnergy() => headless.Run(() =>
    {
        using var vm = Create();
        vm.ApplyColorPalette(Full);
        vm.ApplyVisualizerFrame(new VisualizerFrame { Loudness = 65535 });
        vm.SetPlaying(true);

        vm.Reset();

        Assert.False(vm.IsActive);
        Assert.False(vm.HasPalette);
        Assert.Equal(0.0, vm.TargetEnergy);
        Assert.False(vm.IsPlaying);

        // The same palette again after a reset is new again.
        vm.ApplyColorPalette(Full);
        Assert.True(vm.IsActive);
    });

    [Fact]
    public void APaletteEqualToTheLast_IsDropped() => headless.Run(() =>
    {
        using var vm = Create();

        vm.ApplyColorPalette(Full);
        vm.ApplyColorPalette(new ColorPalette
        {
            BackgroundDark = Full.BackgroundDark,
            BackgroundLight = Full.BackgroundLight,
            Primary = Full.Primary,
            Accent = Full.Accent,
            OnDark = Full.OnDark,
            OnLight = Full.OnLight,
            Timestamp = 99,
        });
        Assert.Equal(1, vm.PalettesApplied);

        vm.ApplyColorPalette(new ColorPalette { Primary = new RgbColor(9, 9, 9) });
        Assert.Equal(2, vm.PalettesApplied);
    });

    [Fact]
    public void Frames_MoveTheEnergyAndRaiseBeats() => headless.Run(() =>
    {
        using var vm = Create();
        var beats = new List<double>();
        vm.BeatTriggered += (_, strength) => beats.Add(strength);

        vm.ApplyVisualizerFrame(new VisualizerFrame { Loudness = 65535 });
        Assert.Equal(1.0, vm.TargetEnergy);

        vm.ApplyVisualizerFrame(new VisualizerFrame { Loudness = 0 });
        Assert.Equal(0.0, vm.TargetEnergy);

        vm.ApplyVisualizerFrame(new VisualizerFrame { IsDownbeat = false });
        vm.ApplyVisualizerFrame(new VisualizerFrame { IsDownbeat = true });
        Assert.Equal([0.85, 1.0], beats);
    });

    [Fact]
    public void TheIntensity_FollowsTheSettingWithTheFloor() => headless.Run(() =>
    {
        var settings = ShellViewModels.CreateSettings();
        using var vm = new AmbientBackdropViewModel(settings, new ShellViewModels.FixedProbe(true), NullLogger<AmbientBackdropViewModel>.Instance);
        Assert.Equal(1.0, vm.Intensity);

        settings.Update(s => s.Backdrop.Intensity = 0.0);
        Assert.Equal(AmbientMath.IntensityFloor, vm.Intensity);

        settings.Update(s => s.Backdrop.Intensity = 1.5);
        Assert.Equal(1.5, vm.Intensity);

        settings.Update(s => s.Backdrop.Intensity = 9.0);
        Assert.Equal(BackdropSettings.MaxIntensity, vm.Intensity);

        settings.Update(s => s.Backdrop.Mode = BackdropMode.BreathingArt);
        Assert.Equal(BackdropMode.BreathingArt, vm.Mode);
    });

    /// <remarks>The SDK's threads are not the UI thread; a palette from one lands on the next dispatcher pass.</remarks>
    [Fact]
    public void APaletteFromAnotherThread_IsAppliedOnTheUiThread() => headless.Run(() =>
    {
        using var vm = Create();

        var worker = new Thread(() => vm.ReceivePalette(Full));
        worker.Start();
        worker.Join();

        Assert.False(vm.IsActive);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsActive);

        var frames = new Thread(() => vm.ReceiveFrame(new VisualizerFrame { Loudness = 65535 }));
        frames.Start();
        frames.Join();

        Assert.Equal(0.0, vm.TargetEnergy);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1.0, vm.TargetEnergy);
    });

    private static AmbientBackdropViewModel Create(bool hasGpu = true)
    {
        var vm = new AmbientBackdropViewModel(ShellViewModels.CreateSettings(), new ShellViewModels.FixedProbe(hasGpu), NullLogger<AmbientBackdropViewModel>.Instance);
        vm.ApplyTheme(Theme(isDark: true));
        return vm;
    }

    private static BackdropTheme Theme(bool isDark) => new(isDark, ThemeBackground, ThemeAccent, ThemeGlow);
}
