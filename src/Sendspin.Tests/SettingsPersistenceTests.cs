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
        Assert.Equal(ConnectionMode.Auto, settings.ConnectionMode);
        Assert.Equal(AutoConnectPolicy.Never, settings.AutoConnect);

        // Per-track notifications must default off: neither Linux shell deduplicates a
        // now-playing toast against the app's own media entry.
        Assert.False(settings.Notifications.TrackChange);

        // Discord is a feature, not a platform integration, and must not be on by default.
        Assert.False(settings.DiscordRichPresence);
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
            ManualServerUrl = "ws://10.0.0.5:8927/sendspin",
            Volume = 42,
            Muted = true,
            StaticDelayMs = 137.5,
            AudioDeviceId = "device-abc",
            PreferredCodec = "opus",
            StartMinimizedToTray = true,
            CloseToTray = false,
            DiscordRichPresence = true,
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
        Assert.Equal(original.ManualServerUrl, loaded.ManualServerUrl);
        Assert.Equal(original.Volume, loaded.Volume);
        Assert.Equal(original.Muted, loaded.Muted);
        Assert.Equal(original.StaticDelayMs, loaded.StaticDelayMs);
        Assert.Equal(original.AudioDeviceId, loaded.AudioDeviceId);
        Assert.Equal(original.PreferredCodec, loaded.PreferredCodec);
        Assert.Equal(original.StartMinimizedToTray, loaded.StartMinimizedToTray);
        Assert.Equal(original.CloseToTray, loaded.CloseToTray);
        Assert.Equal(original.DiscordRichPresence, loaded.DiscordRichPresence);
        Assert.Equal(original.ShowDiagnostics, loaded.ShowDiagnostics);
        Assert.Equal(-25.0, loaded.GetManualLatencyOffsetMs("device-abc"));
        Assert.True(loaded.Notifications.TrackChange);
        Assert.False(loaded.Notifications.IncludeArtwork);
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
}
