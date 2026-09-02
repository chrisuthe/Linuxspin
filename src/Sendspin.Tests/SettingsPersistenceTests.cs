using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.Core.Configuration;
using Sendspin.SDK.Client;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests that settings actually reach disk and come back.
/// </summary>
/// <remarks>
/// These write real files. The spec requires <c>static_delay_ms</c> to survive a reboot and the
/// client identity to be stable across restarts, and neither claim can be made by reading code —
/// this repo previously declared <c>IPlatformPaths.ConfigFile</c>, implemented it on both
/// platforms, and never read or wrote it.
/// </remarks>
public sealed class SettingsPersistenceTests
{
    [Fact]
    public void Load_WithNoFile_ReturnsUsableDefaults()
    {
        using var paths = new TemporaryPaths();
        var store = new JsonSettingsStore(paths, NullLogger<JsonSettingsStore>.Instance);

        var settings = store.Load();

        Assert.Equal(100, settings.Volume);
        Assert.False(settings.Muted);
        Assert.Equal(ConnectionMode.AdvertiseOnly, settings.ConnectionMode);
        Assert.Equal(AutoConnectPolicy.Never, settings.AutoConnect);

        // Per-track notifications must default off: neither Linux shell deduplicates a
        // now-playing toast against the app's own media entry.
        Assert.False(settings.Notifications.TrackChange);

        // Discord is a feature, not a platform integration, and must not be on by default.
        Assert.False(settings.DiscordRichPresence);

        // The group switcher is part of the footer until someone says otherwise.
        Assert.True(settings.ShowSwitchGroupButton);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryPersistedField()
    {
        using var paths = new TemporaryPaths();
        var store = new JsonSettingsStore(paths, NullLogger<JsonSettingsStore>.Instance);

        var original = new PlayerSettings
        {
            ClientId = "sendspin-desktop-abc123",
            PlayerName = "Kitchen",
            ConnectionMode = ConnectionMode.DiscoverOnly,
            AutoConnect = AutoConnectPolicy.Always,
            LastServerId = "server-7",
            AutoConnectPromptedServerId = "server-3",
            ManualServerUrl = "ws://10.0.0.5:8927/sendspin",
            Volume = 42,
            Muted = true,
            StaticDelayMs = 137.5,
            AudioDeviceId = "device-abc",
            PreferredCodec = "opus",
            StartMinimizedToTray = true,
            CloseToTray = false,
            DiscordRichPresence = true,
            ShowSwitchGroupButton = false,
            ShowDiagnostics = true
        };

        original.SetManualLatencyOffsetMs("device-abc", -25.0);
        original.Notifications.TrackChange = true;
        original.Notifications.IncludeArtwork = false;

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(original.ClientId, loaded.ClientId);
        Assert.Equal(original.PlayerName, loaded.PlayerName);
        Assert.Equal(original.ConnectionMode, loaded.ConnectionMode);
        Assert.Equal(original.AutoConnect, loaded.AutoConnect);
        Assert.Equal(original.LastServerId, loaded.LastServerId);
        Assert.Equal(original.AutoConnectPromptedServerId, loaded.AutoConnectPromptedServerId);
        Assert.Equal(original.ManualServerUrl, loaded.ManualServerUrl);
        Assert.Equal(original.Volume, loaded.Volume);
        Assert.Equal(original.Muted, loaded.Muted);
        Assert.Equal(original.StaticDelayMs, loaded.StaticDelayMs);
        Assert.Equal(original.AudioDeviceId, loaded.AudioDeviceId);
        Assert.Equal(original.PreferredCodec, loaded.PreferredCodec);
        Assert.Equal(original.StartMinimizedToTray, loaded.StartMinimizedToTray);
        Assert.Equal(original.CloseToTray, loaded.CloseToTray);
        Assert.Equal(original.DiscordRichPresence, loaded.DiscordRichPresence);
        Assert.Equal(original.ShowSwitchGroupButton, loaded.ShowSwitchGroupButton);
        Assert.Equal(original.ShowDiagnostics, loaded.ShowDiagnostics);
        Assert.Equal(-25.0, loaded.GetManualLatencyOffsetMs("device-abc"));
        Assert.True(loaded.Notifications.TrackChange);
        Assert.False(loaded.Notifications.IncludeArtwork);
    }

    /// <summary>
    /// A file written by an older build must not leave the player in a mode the spec forbids.
    /// </summary>
    /// <remarks>
    /// <c>Auto</c> ran discovery and advertising together, which connection.md does not allow and
    /// the SDK removes in 10.0.0. It was also this repo's default, so most existing installs have
    /// it on disk. Both cases are covered because they reach the answer by different routes: a
    /// file that says <c>Auto</c> is rewritten by the migration, while a file that predates the
    /// field never sets the property at all and simply keeps its initializer. Written as raw JSON
    /// rather than through the enum, because naming <c>ConnectionMode.Auto</c> anywhere outside
    /// the migration is exactly what this change removes.
    /// </remarks>
    [Theory]
    [InlineData("\"connection_mode\": \"Auto\", ")]
    [InlineData("")]
    public void Load_MigratesARetiredConnectionModeToAdvertiseOnly(string connectionModeField)
    {
        using var paths = new TemporaryPaths();
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(
            paths.ConfigFile,
            $$"""{"version": 1, {{connectionModeField}}"volume": 63}""");

        var settings = new JsonSettingsStore(paths, NullLogger<JsonSettingsStore>.Instance).Load();

        Assert.Equal(ConnectionMode.AdvertiseOnly, settings.ConnectionMode);

        // The rest of the file has to survive the migration, or "migrate" would mean "reset".
        Assert.Equal(63, settings.Volume);
    }

    /// <summary>
    /// A mode the user actually chose must come back as they left it.
    /// </summary>
    [Theory]
    [InlineData("DiscoverOnly", ConnectionMode.DiscoverOnly)]
    [InlineData("AdvertiseOnly", ConnectionMode.AdvertiseOnly)]
    public void Load_LeavesAConformantConnectionModeAlone(string persisted, ConnectionMode expected)
    {
        using var paths = new TemporaryPaths();
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(paths.ConfigFile, $$"""{"version": 1, "connection_mode": "{{persisted}}"}""");

        var settings = new JsonSettingsStore(paths, NullLogger<JsonSettingsStore>.Instance).Load();

        Assert.Equal(expected, settings.ConnectionMode);
    }

    /// <summary>
    /// A corrupt file must not stop the app starting.
    /// </summary>
    [Fact]
    public void Load_WithCorruptFile_FallsBackToDefaults()
    {
        using var paths = new TemporaryPaths();
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(paths.ConfigFile, "{ this is not json");

        var store = new JsonSettingsStore(paths, NullLogger<JsonSettingsStore>.Instance);
        var settings = store.Load();

        Assert.Equal(100, settings.Volume);
    }

    [Fact]
    public void ConfigFileName_IsTheSameOnEveryPlatform()
    {
        using var paths = new TemporaryPaths();

        // The platforms used to disagree — config.json on Linux, settings.json on Windows — which
        // made support and documentation needlessly per-platform for no benefit.
        Assert.Equal("settings.json", Path.GetFileName(paths.ConfigFile));
    }

    /// <summary>
    /// The identity must be minted once and then never change.
    /// </summary>
    [Fact]
    public void SettingsService_GeneratesAStableIdentityAndPersistsItImmediately()
    {
        using var paths = new TemporaryPaths();
        var store = new JsonSettingsStore(paths, NullLogger<JsonSettingsStore>.Instance);

        var first = new SettingsService(store, NullLogger<SettingsService>.Instance);
        var clientId = first.Current.ClientId;

        Assert.False(string.IsNullOrWhiteSpace(clientId));
        Assert.False(string.IsNullOrWhiteSpace(first.Current.PlayerName));

        // Not derived from the machine name: that is neither unique across two installs on one
        // host, nor stable when the host is renamed.
        Assert.DoesNotContain(Environment.MachineName, clientId, StringComparison.OrdinalIgnoreCase);

        // Platform-neutral: the old identifier said "linux" on every platform including Windows.
        Assert.DoesNotContain("linux", clientId, StringComparison.OrdinalIgnoreCase);

        var second = new SettingsService(store, NullLogger<SettingsService>.Instance);
        Assert.Equal(clientId, second.Current.ClientId);
    }

    /// <summary>
    /// The static-delay store is the SDK's persistence seam, and it must write into the same file
    /// as everything else rather than a second one of its own.
    /// </summary>
    [Fact]
    public void StaticDelayStore_RoundTripsThroughTheOneConfigFile()
    {
        using var paths = new TemporaryPaths();
        var store = new JsonSettingsStore(paths, NullLogger<JsonSettingsStore>.Instance);
        var settings = new SettingsService(store, NullLogger<SettingsService>.Instance);

        IStaticDelayStore delayStore =
            new SettingsStaticDelayStore(settings, NullLogger<SettingsStaticDelayStore>.Instance);

        delayStore.Save(275.0);

        Assert.Equal(275.0, delayStore.Load());
        Assert.Equal(275.0, store.Load().StaticDelayMs);
        Assert.Single(Directory.GetFiles(paths.ConfigDirectory));
    }

    /// <summary>
    /// A GroupSync calibration offset may be negative, and the SDK asks for it to be round-tripped
    /// as given rather than clamped.
    /// </summary>
    [Fact]
    public void StaticDelayStore_PreservesANegativeDelay()
    {
        using var paths = new TemporaryPaths();
        var store = new JsonSettingsStore(paths, NullLogger<JsonSettingsStore>.Instance);
        var settings = new SettingsService(store, NullLogger<SettingsService>.Instance);

        IStaticDelayStore delayStore =
            new SettingsStaticDelayStore(settings, NullLogger<SettingsStaticDelayStore>.Instance);

        delayStore.Save(-40.5);

        Assert.Equal(-40.5, store.Load().StaticDelayMs);
    }

    /// <summary>
    /// A snapshot handed out before a change must not be altered by that change.
    /// </summary>
    /// <remarks>
    /// The settings object is mutable and holds a dictionary. Publishing the same instance to every
    /// reader lets one observe half of a multi-field change, and lets a reader iterating
    /// <c>Devices</c> race an insert. Copy-on-write is what prevents both; this asserts it rather
    /// than trusting it.
    /// </remarks>
    [Fact]
    public void Update_PublishesANewSnapshotRatherThanMutatingTheOldOne()
    {
        using var paths = new TemporaryPaths();
        var store = new JsonSettingsStore(paths, NullLogger<JsonSettingsStore>.Instance);
        var settings = new SettingsService(store, NullLogger<SettingsService>.Instance);

        var before = settings.Current;
        var beforeVolume = before.Volume;

        settings.Update(s =>
        {
            s.Volume = 17;
            s.SetManualLatencyOffsetMs("device-x", 12.5);
        });

        Assert.NotSame(before, settings.Current);
        Assert.Equal(beforeVolume, before.Volume);
        Assert.Equal(0.0, before.GetManualLatencyOffsetMs("device-x"));

        Assert.Equal(17, settings.Current.Volume);
        Assert.Equal(12.5, settings.Current.GetManualLatencyOffsetMs("device-x"));
    }

    [Fact]
    public void Clone_CopiesNestedStateIndependently()
    {
        var original = new PlayerSettings { Volume = 44 };
        original.SetManualLatencyOffsetMs("d", 9.0);
        original.Notifications.TrackChange = true;

        var copy = original.Clone();
        copy.Volume = 1;
        copy.SetManualLatencyOffsetMs("d", -1.0);
        copy.Notifications.TrackChange = false;

        Assert.Equal(44, original.Volume);
        Assert.Equal(9.0, original.GetManualLatencyOffsetMs("d"));
        Assert.True(original.Notifications.TrackChange);
    }
}
