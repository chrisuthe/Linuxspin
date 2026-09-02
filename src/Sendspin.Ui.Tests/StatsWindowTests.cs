using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Threading;
using Sendspin.Core.Diagnostics;
using Sendspin.Player.Views;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins the Stats window: opened from the Diagnostics row, one instance, hidden by its close,
/// every field of the old inline panel with the timing source first, the refresh clock only
/// while it is open, and reopened at start when it was open at exit.
/// </summary>
[Collection(HeadlessCollection.Name)]
public sealed partial class StatsWindowTests(HeadlessSession headless)
{
    /// <summary>Every value the inline panel showed, by the name of the control that shows it.</summary>
    private static readonly string[] Values =
    [
        "TimingSourceValue", "SyncErrorValue", "CorrectionModeValue", "PlaybackRateValue",
        "BufferedValue", "StaticDelayValue",
        "ClockOffsetValue", "ClockDriftValue", "ClockUncertaintyValue", "ClockConvergedValue", "PostAnchorValue", "RoundTripValue",
        "OutputLatencyValue", "StreamValue", "OutputDeviceValue", "PlatformValue",
    ];

    private static readonly string[] Cards = ["TimingCard", "SyncCard", "BufferCard", "ClockCard", "OutputCard"];

    [Fact]
    public void TheDiagnosticsRow_OpensTheWindow() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        Assert.Null(shell.Window.Stats);
        Assert.False(shell.ViewModel.Diagnostics.IsRefreshing);

        var stats = Open(shell);

        Assert.True(stats.IsVisible);
        Assert.Equal("Stats for Nerds", stats.Title);
        Assert.Equal(480, stats.Width);
        Assert.Equal(640, stats.Height);
        Assert.True(stats.CanResize);
        Assert.Same(shell.ViewModel.Diagnostics, stats.DataContext);

        Assert.True(shell.ViewModel.Diagnostics.IsVisible);
        Assert.True(shell.ViewModel.Diagnostics.IsRefreshing);
        Assert.True(shell.Settings.Current.ShowDiagnostics);
    });

    [Fact]
    public void ASecondClick_ShowsTheSameWindow() => headless.Run(() =>
    {
        using var shell = Shell.Show();

        var first = Open(shell);
        var second = Open(shell);

        Assert.Same(first, second);
        Assert.True(second.IsVisible);
    });

    [Fact]
    public void ClosingIt_HidesItAndStopsTheClock() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var stats = Open(shell);

        CloseFromTheDesktop(stats);
        Dispatcher.UIThread.RunJobs();

        Assert.False(stats.IsVisible);
        Assert.False(shell.ViewModel.Diagnostics.IsVisible);
        Assert.False(shell.ViewModel.Diagnostics.IsRefreshing);
        Assert.False(shell.Settings.Current.ShowDiagnostics);

        // Hidden, not destroyed: the next click shows the same window.
        Assert.Same(stats, shell.Window.Stats);
        Assert.Same(stats, Open(shell));
        Assert.True(stats.IsVisible);
        Assert.True(shell.ViewModel.Diagnostics.IsRefreshing);
    });

    /// <remarks>
    /// Cmd+Q on macOS and a Windows session end reach every unowned window as a close that is
    /// not programmatic but carries a shutdown reason. That close must go through, and it must
    /// not be mistaken for the user closing the window: the flag says "open at exit".
    /// </remarks>
    [Theory]
    [InlineData(WindowCloseReason.ApplicationShutdown)]
    [InlineData(WindowCloseReason.OSShutdown)]
    public void AShutdownsClose_GoesThroughAndLeavesTheFlagAlone(WindowCloseReason reason) => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var stats = Open(shell);

        var cancelled = CloseFromTheDesktop(stats, reason);
        Dispatcher.UIThread.RunJobs();

        Assert.False(cancelled);
        Assert.False(stats.IsVisible);
        Assert.True(shell.Settings.Current.ShowDiagnostics);
        Assert.True(shell.ViewModel.Diagnostics.IsVisible);
    });

    [Fact]
    public void TheWindow_ShowsEveryFieldWithTheTimingSourceFirst() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var stats = Open(shell);

        shell.ViewModel.Diagnostics.Snapshot = new PlayerDiagnosticsSnapshot
        {
            TimingSource = "wall-clock",
            SmoothedSyncErrorMicroseconds = 1234,
            CorrectionMode = "RateAdjust",
            PlaybackRate = 1.0001,
            BufferedMilliseconds = 512.5,
            StaticDelayMs = 20,
            ClockOffsetMilliseconds = -3.25,
            ClockDriftMicrosecondsPerSecond = 1.5,
            ClockOffsetUncertaintyMicroseconds = 250,
            ClockConverged = true,
            ClockDriftMs = 0.125,
            RoundTripMicroseconds = 800,
            OutputLatencyMs = 12,
            ManualLatencyOffsetMs = 5,
            Codec = "flac",
            SampleRate = 48000,
            Channels = 2,
            BitDepth = 24,
            AudioDeviceName = "Speakers",
            PlatformName = "Linux",
        };
        Dispatcher.UIThread.RunJobs();

        foreach (var name in Values)
        {
            var value = Find<TextBlock>(stats, name);
            Assert.False(string.IsNullOrWhiteSpace(value.Text), $"{name} is empty");
            Assert.NotEqual("—", value.Text);
        }

        Assert.Equal("wall-clock", Find<TextBlock>(stats, "TimingSourceValue").Text);
        Assert.Equal("+1.234 ms, rate adjust", Find<TextBlock>(stats, "SyncErrorValue").Text);
        Assert.Equal("1.000100 (+100 ppm, ceiling ±500)", Find<TextBlock>(stats, "PlaybackRateValue").Text);
        Assert.Equal("12 ms measured + 5.0 ms manual = 17.0 ms", Find<TextBlock>(stats, "OutputLatencyValue").Text);
        Assert.Equal("flac 48000 Hz/24-bit, 2 ch", Find<TextBlock>(stats, "StreamValue").Text);
        Assert.Equal("±250 µs", Find<TextBlock>(stats, "ClockUncertaintyValue").Text);
        Assert.Equal("True", Find<TextBlock>(stats, "ClockConvergedValue").Text);

        // Not a hardware clock: the warning stands beside the source, and the source is on top.
        var warning = Find<TextBlock>(stats, "TimingSourceWarning");
        Assert.True(warning.IsVisible);
        Assert.Contains("warning", warning.Classes);

        var timing = Find<Border>(stats, "TimingCard");
        foreach (var name in Cards.Skip(1))
        {
            Assert.True(Shell.TopIn(timing, stats) < Shell.TopIn(Find<Border>(stats, name), stats), $"{name} is above the timing card");
        }

        Assert.Equal(Cards.Select(c => Shell.TopIn(Find<Border>(stats, c), stats)).OrderBy(t => t),
            Cards.Select(c => Shell.TopIn(Find<Border>(stats, c), stats)));

        shell.ViewModel.Diagnostics.Snapshot = shell.ViewModel.Diagnostics.Snapshot with { TimingSource = "audio-clock" };
        Dispatcher.UIThread.RunJobs();

        Assert.False(warning.IsVisible);
    });

    [Fact]
    public void TheClock_RunsOnlyWhileTheWindowIsOpen() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var diagnostics = shell.ViewModel.Diagnostics;

        Assert.False(diagnostics.IsRefreshing);

        var stats = Open(shell);
        Assert.True(diagnostics.IsRefreshing);

        CloseFromTheDesktop(stats);
        Dispatcher.UIThread.RunJobs();
        Assert.False(diagnostics.IsRefreshing);
    });

    [Fact]
    public void TheWindow_FollowsTheMainWindowIntoTheTrayAndBack() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var stats = Open(shell);

        shell.Window.Hide();
        Dispatcher.UIThread.RunJobs();

        // Hidden with the main window, but still "open": the flag and the clock are untouched.
        Assert.False(stats.IsVisible);
        Assert.True(shell.ViewModel.Diagnostics.IsVisible);
        Assert.True(shell.Settings.Current.ShowDiagnostics);

        shell.Window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(stats.IsVisible);
        Assert.Same(stats, shell.Window.Stats);
    });

    [Fact]
    public void TheWindow_ClosesWithTheMainWindow() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var stats = Open(shell);

        shell.Window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(stats.IsVisible);
        Assert.Null(shell.Window.Stats);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AtStart_ItReopensOnlyIfItWasOpenAtExit(bool wasOpen) => headless.Run(() =>
    {
        using var shell = Shell.Show(s => s.ShowDiagnostics = wasOpen);

        // Nothing until the start-up path runs; the window is not opened by construction.
        Assert.Null(shell.Window.Stats);
        Assert.False(shell.ViewModel.Diagnostics.IsRefreshing);

        shell.ViewModel.ReopenStatsIfLeftOpen();
        Dispatcher.UIThread.RunJobs();

        if (wasOpen)
        {
            Assert.NotNull(shell.Window.Stats);
            Assert.True(shell.Window.Stats!.IsVisible);
            Assert.True(shell.ViewModel.Diagnostics.IsRefreshing);
        }
        else
        {
            Assert.Null(shell.Window.Stats);
            Assert.False(shell.ViewModel.Diagnostics.IsRefreshing);
        }

        Assert.Equal(wasOpen, shell.Settings.Current.ShowDiagnostics);
    });

    [Fact]
    public void TheInlinePanel_IsGone()
    {
        var views = Path.Combine(PlayerSource.Directory(), "Views");

        Assert.False(File.Exists(Path.Combine(views, "DiagnosticsView.axaml")));
        Assert.False(File.Exists(Path.Combine(views, "DiagnosticsView.axaml.cs")));
        Assert.True(File.Exists(Path.Combine(views, "StatsWindow.axaml")));

        var mentions = PlayerSource.SourceFiles()
            .Where(f => DiagnosticsView().IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(mentions.Count == 0, "DiagnosticsView still named in:\n" + string.Join('\n', mentions));
    }

    private static StatsWindow Open(Shell shell)
    {
        shell.ViewModel.IsSettingsOpen = true;
        Dispatcher.UIThread.RunJobs();

        var body = shell.Find<SettingsView>("SettingsBody");
        shell.FindIn<Button>(body, "OpenStatsButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var stats = shell.Window.Stats;
        Assert.NotNull(stats);
        return stats!;
    }

    private static T Find<T>(StatsWindow window, string name)
        where T : Control
    {
        var control = window.FindControl<T>(name);
        Assert.True(control is not null, $"no {typeof(T).Name} named {name} in the Stats window");
        return control!;
    }

    /// <summary>
    /// The desktop's close request — the title-bar button — which is the non-programmatic path
    /// that <see cref="Window.Close"/> cannot take. The headless platform exposes it as the
    /// window impl's <c>Closing</c> callback, which is what a real backend invokes.
    /// </summary>
    /// <returns>Whether the window cancelled the close.</returns>
    private static bool CloseFromTheDesktop(Window window, WindowCloseReason reason = WindowCloseReason.WindowClosing)
    {
        var impl = window.PlatformImpl;
        Assert.NotNull(impl);

        var closing = impl!.GetType().GetProperty("Closing")?.GetValue(impl) as Func<WindowCloseReason, bool>;
        Assert.NotNull(closing);

        return closing!(reason);
    }

    [GeneratedRegex(@"\bDiagnosticsView\b")]
    private static partial Regex DiagnosticsView();
}
