using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Sendspin.Core.Configuration;
using Sendspin.Player;
using Sendspin.Player.Controls;
using Sendspin.Player.Views;
using Sendspin.SDK.Client;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins the settings card: its header, the sections in order with every setting reachable, the
/// version and the Done button, and that everything still writes through with no Save in sight.
/// </summary>
[Collection(HeadlessCollection.Name)]
public sealed class SettingsCardTests(HeadlessSession headless)
{
    /// <summary>Top to bottom.</summary>
    private static readonly string[] Headings =
        ["GeneralHeading", "ConnectionHeading", "AudioHeading", "AudioSyncHeading", "DiagnosticsHeading"];

    private static readonly string[] Switches =
    [
        "NotifyTrackChangeSwitch", "NotifyPlaybackStateSwitch", "NotifyConnectionStateSwitch", "IncludeArtworkSwitch",
        "DiscordSwitch", "StartMinimizedSwitch", "CloseToTraySwitch", "ShowSwitchGroupSwitch",
    ];

    [Fact]
    public void TheCard_HasTheHeaderTheSectionsInOrderTheVersionAndDone() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var overlay = Open(shell);
        var body = shell.Find<SettingsView>("SettingsBody");

        // Header: the gear and "Settings".
        var header = shell.Find<StackPanel>("SettingsHeader");
        var glyph = Assert.Single(header.Children.OfType<PathIcon>());
        Assert.Same(shell.Resolve<Geometry>("GearIcon"), glyph.Data);
        var title = shell.Find<TextBlock>("SettingsTitle");
        Assert.Equal("Settings", title.Text);
        Assert.Contains("subtitle", title.Classes);

        // The sections, each a caption heading, in order down the card.
        var tops = new List<double>();
        foreach (var name in Headings)
        {
            var heading = shell.FindIn<TextBlock>(body, name);
            Assert.Contains("sectionCaption", heading.Classes);
            Assert.Equal(heading.Text!.ToUpperInvariant(), heading.Text);
            tops.Add(Shell.TopIn(heading, overlay));
        }

        Assert.Equal(tops.OrderBy(t => t), tops);
        Assert.True(Shell.TopIn(header, overlay) < tops[0]);

        // Footer: the version on the left, Done on the right, below the body.
        var version = shell.Find<TextBlock>("VersionText");
        var done = shell.Find<Button>("DoneButton");
        Assert.Equal($"Sendspin Player {AppInfo.DisplayVersion}", version.Text);
        Assert.NotEmpty(AppInfo.DisplayVersion);
        Assert.DoesNotContain('+', AppInfo.DisplayVersion);
        Assert.Contains("caption", version.Classes);
        Assert.Equal("Done", done.Content);
        Assert.Contains("primary", done.Classes);
        Assert.True(version.Bounds.Right <= done.Bounds.Left);
        Assert.True(Shell.TopIn(done, overlay) > Shell.TopIn(body, overlay));

        // Done closes the card, and nothing else: there is no Save, Apply or Cancel to click.
        var buttons = overlay.GetLogicalDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
        Assert.DoesNotContain("Save", buttons);
        Assert.DoesNotContain("Apply", buttons);
        Assert.DoesNotContain("Cancel", buttons);

        done.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.ViewModel.IsSettingsOpen);
        Assert.False(overlay.IsVisible);
    });

    [Fact]
    public void TheCard_SitsOverTheBackdropNotOverTheContent() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        var nowPlaying = shell.Find<NowPlayingView>("NowPlaying");
        var welcome = shell.Find<ScrollViewer>("WelcomeScroller");

        Assert.True(welcome.IsVisible);
        Open(shell);
        Assert.False(welcome.IsVisible);
        Assert.False(nowPlaying.IsVisible);

        // The backdrop layers are untouched: they are what the translucent card is meant to tint.
        shell.ViewModel.HasArtBackdrop = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.Find<Panel>("ArtBackdrop").IsVisible);
        Assert.True(shell.Find<Border>("Veil").IsVisible);

        shell.ViewModel.IsConnected = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(nowPlaying.IsVisible);

        shell.ViewModel.IsSettingsOpen = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(nowPlaying.IsVisible);
        Assert.False(welcome.IsVisible);
    });

    [Fact]
    public void EveryBooleanRow_IsASwitchWithNoLabelOfItsOwn() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        Open(shell);
        var body = shell.Find<SettingsView>("SettingsBody");

        var switches = body.GetLogicalDescendants().OfType<ToggleSwitch>().ToList();
        Assert.Equal(Switches.Length, switches.Count);
        Assert.All(switches, s =>
        {
            Assert.Contains("setting", s.Classes);
            Assert.Null(s.OnContent);
            Assert.Null(s.OffContent);
        });

        Assert.Empty(body.GetLogicalDescendants().OfType<CheckBox>());
    });

    [Fact]
    public void EverySwitch_WritesThrough() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        Open(shell);
        var body = shell.Find<SettingsView>("SettingsBody");

        foreach (var name in Switches)
        {
            var toggle = shell.FindIn<ToggleSwitch>(body, name);
            var before = Read(shell.Settings.Current, name);
            Assert.Equal(before, toggle.IsChecked);

            toggle.IsChecked = !before;
            Dispatcher.UIThread.RunJobs();

            Assert.True(Read(shell.Settings.Current, name) == !before, $"{name} did not write through");
        }
    });

    [Fact]
    public void ThePlayerName_WritesThroughAndIsTheWelcomeScreensToo() => headless.Run(() =>
    {
        using var shell = Shell.Show(s => s.PlayerName = "Study");
        Open(shell);
        var body = shell.Find<SettingsView>("SettingsBody");

        var box = shell.FindIn<TextBox>(body, "PlayerNameBox");
        Assert.Equal("Study", box.Text);

        box.Text = "Kitchen";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Kitchen", shell.Settings.Current.PlayerName);
        Assert.Equal("Kitchen", shell.FindIn<TextBox>(shell.Find<WelcomeView>("Welcome"), "PlayerNameBox").Text);
    });

    [Fact]
    public void TheChoices_WriteThrough() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        Open(shell);
        var body = shell.Find<SettingsView>("SettingsBody");

        var mode = shell.FindIn<ComboBox>(body, "ConnectionModeBox");
        var note = shell.FindIn<TextBlock>(body, "RestartNote");
        Assert.Equal(ConnectionMode.AdvertiseOnly, mode.SelectedItem);
        Assert.False(note.IsVisible);

        mode.SelectedItem = ConnectionMode.DiscoverOnly;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ConnectionMode.DiscoverOnly, shell.Settings.Current.ConnectionMode);
        Assert.True(note.IsVisible);
        Assert.Contains("warning", note.Classes);

        var policy = shell.FindIn<ComboBox>(body, "AutoConnectBox");
        policy.SelectedItem = AutoConnectPolicy.Always;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(AutoConnectPolicy.Always, shell.Settings.Current.AutoConnect);

        var codec = shell.FindIn<ComboBox>(body, "CodecBox");
        var other = shell.ViewModel.Settings.Codecs.First(c => c != shell.Settings.Current.PreferredCodec);
        codec.SelectedItem = other;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(other, shell.Settings.Current.PreferredCodec);

        var devices = shell.FindIn<ComboBox>(body, "OutputDeviceBox");
        var refresh = shell.FindIn<Button>(body, "RefreshDevicesButton");
        Assert.Same(shell.ViewModel.Settings.RefreshDevicesCommand, refresh.Command);
        Assert.True(devices.Bounds.Right <= refresh.Bounds.Left);
    });

    [Fact]
    public void TheConnectionSection_BindsTheMainViewModel() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        Open(shell);
        var body = shell.Find<SettingsView>("SettingsBody");

        var disconnect = shell.FindIn<Button>(body, "DisconnectButton");
        var line = shell.FindIn<TextBlock>(body, "SettingsConnectionLine");

        Assert.Same(shell.ViewModel.DisconnectCommand, disconnect.Command);
        Assert.False(disconnect.IsEffectivelyEnabled);
        Assert.Equal("Not connected", line.Text);
        Assert.True(disconnect.Bounds.Right <= line.Bounds.Left);

        shell.ViewModel.IsConnected = true;
        shell.ViewModel.ServerName = "Living Room";
        Dispatcher.UIThread.RunJobs();

        Assert.True(disconnect.IsEffectivelyEnabled);
        Assert.Equal("Connected to Living Room", line.Text);
        Assert.Equal(shell.Find<TextBlock>("ConnectionLine").Text, line.Text);
    });

    [Fact]
    public void TheSteppers_AreBoundToTheCalibrationValues() => headless.Run(() =>
    {
        using var shell = Shell.Show(s => s.StaticDelayMs = 500);
        Open(shell);
        var body = shell.Find<SettingsView>("SettingsBody");

        var delay = shell.FindIn<StepperRow>(body, "StaticDelayStepper");
        var offset = shell.FindIn<StepperRow>(body, "LatencyOffsetStepper");

        // A persisted value survives the load: the range is set before the value binds.
        Assert.Equal(500, delay.Value);
        Assert.Equal(500, shell.ViewModel.Settings.StaticDelayMs);
        Assert.Equal((0, 2000, 10), (delay.Minimum, delay.Maximum, delay.Step));
        Assert.Equal((-200, 500, 10), (offset.Minimum, offset.Maximum, offset.Step));

        delay.Increment();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(510, shell.ViewModel.Settings.StaticDelayMs);

        offset.Decrement();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(-10, shell.ViewModel.Settings.ManualLatencyOffsetMs);

        // And back the other way: the view model is the source.
        shell.ViewModel.Settings.StaticDelayMs = 250;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(250, delay.Value);
    });

    [Fact]
    public void TheDiagnosticsRow_IsTheStatsButton() => headless.Run(() =>
    {
        using var shell = Shell.Show();
        Open(shell);
        var body = shell.Find<SettingsView>("SettingsBody");

        var row = shell.FindIn<Button>(body, "OpenStatsButton");
        Assert.Same(shell.ViewModel.OpenStatsCommand, row.Command);

        var texts = row.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Stats for Nerds", texts);
        Assert.Contains("Real-time audio sync diagnostics", texts);
        Assert.Same(shell.Resolve<Geometry>("StatsIcon"), Assert.Single(row.GetLogicalDescendants().OfType<PathIcon>()).Data);
    });

    private static Border Open(Shell shell)
    {
        shell.ViewModel.IsSettingsOpen = true;
        Dispatcher.UIThread.RunJobs();

        var overlay = shell.Find<Border>("SettingsOverlay");
        Assert.True(overlay.IsVisible);
        return overlay;
    }

    private static bool Read(PlayerSettings settings, string switchName) => switchName switch
    {
        "NotifyTrackChangeSwitch" => settings.Notifications.TrackChange,
        "NotifyPlaybackStateSwitch" => settings.Notifications.PlaybackState,
        "NotifyConnectionStateSwitch" => settings.Notifications.ConnectionState,
        "IncludeArtworkSwitch" => settings.Notifications.IncludeArtwork,
        "DiscordSwitch" => settings.DiscordRichPresence,
        "StartMinimizedSwitch" => settings.StartMinimizedToTray,
        "CloseToTraySwitch" => settings.CloseToTray,
        "ShowSwitchGroupSwitch" => settings.ShowSwitchGroupButton,
        _ => throw new ArgumentOutOfRangeException(nameof(switchName), switchName, null),
    };
}
