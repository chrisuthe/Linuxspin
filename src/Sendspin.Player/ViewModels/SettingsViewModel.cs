using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sendspin.Core.Audio;
using Sendspin.Core.Configuration;
using Sendspin.Core.Presence;
using Sendspin.Platform.Shared.Client;
using Sendspin.SDK.Client;

namespace Sendspin.Player.ViewModels;

/// <summary>
/// The settings surface.
/// </summary>
/// <remarks>
/// <para>
/// What is here and what is not is deliberate. <c>static_delay_ms</c> and the per-device latency
/// offset are exposed because they are physical facts about a room and a cable that no API can
/// report. Buffer depths, correction ceilings and thresholds are not exposed, because they are
/// internals with a correct answer, and a knob invites a user to make the player worse.
/// </para>
/// <para>
/// Every change writes through <see cref="SettingsService"/> immediately. There is no apply
/// button: the settings file is the single authority and a change the user made should already
/// be in it.
/// </para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly SendspinPlayerService _player;
    private readonly IAudioDeviceEnumerator _devices;
    private readonly IPresenceService _presence;

    /// <summary>
    /// Owns the async work a setting change kicks off, so none of it is an unowned task whose
    /// failure would be swallowed as an unobserved exception.
    /// </summary>
    private readonly BackgroundTaskSet _work;

    private bool _isLoading;

    [ObservableProperty]
    private string _playerName = string.Empty;

    [ObservableProperty]
    private ConnectionMode _connectionMode;

    [ObservableProperty]
    private AutoConnectPolicy _autoConnect;

    [ObservableProperty]
    private string _preferredCodec = "flac";

    [ObservableProperty]
    private AudioDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private double _staticDelayMs;

    [ObservableProperty]
    private double _manualLatencyOffsetMs;

    [ObservableProperty]
    private bool _notifyTrackChange;

    [ObservableProperty]
    private bool _notifyPlaybackState;

    [ObservableProperty]
    private bool _notifyConnectionState;

    [ObservableProperty]
    private bool _includeArtworkInNotifications;

    [ObservableProperty]
    private bool _startMinimizedToTray;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _discordRichPresence;

    [ObservableProperty]
    private bool _showSwitchGroupButton;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBackdropIntensityVisible))]
    private BackdropMode _backdropMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackdropIntensityText))]
    private double _backdropIntensity;

    public SettingsViewModel(
        SettingsService settings,
        SendspinPlayerService player,
        IAudioDeviceEnumerator devices,
        IPresenceService presence,
        ILogger<SettingsViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _player = player;
        _devices = devices;
        _presence = presence;
        _work = new BackgroundTaskSet(logger);

        LoadFromSettings();
        RefreshDevices();
    }

    /// <summary>Gets the available output devices.</summary>
    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; } = [];

    /// <summary>Gets the connection modes, for a bound selector.</summary>
    /// <remarks>
    /// Two, not three. <c>ConnectionMode.Auto</c> ran discovery and advertising at once, which
    /// connection.md forbids and the SDK removes in 10.0.0, so it is not offered.
    /// </remarks>
    public IReadOnlyList<ConnectionMode> ConnectionModes { get; } =
        [ConnectionMode.AdvertiseOnly, ConnectionMode.DiscoverOnly];

    /// <summary>Gets the auto-connect policies, for a bound selector.</summary>
    public IReadOnlyList<AutoConnectPolicy> AutoConnectPolicies { get; } =
        [AutoConnectPolicy.Never, AutoConnectPolicy.JustOnce, AutoConnectPolicy.Always];

    /// <summary>Gets the codecs this build can decode.</summary>
    public IReadOnlyList<string> Codecs { get; } = PlayerCapabilities.SupportedCodecs;

    /// <summary>Gets the backdrop styles, for a bound selector, in the order the row offers them.</summary>
    public IReadOnlyList<BackdropMode> BackdropModes { get; } =
        [BackdropMode.Off, BackdropMode.AmbientGlow, BackdropMode.BreathingArt];

    /// <summary>Gets whether the intensity row has anything to control: a style other than Off is chosen.</summary>
    public bool IsBackdropIntensityVisible => BackdropMode != BackdropMode.Off;

    /// <summary>Gets the intensity as the slider's label shows it: a percentage of the tuned default.</summary>
    public string BackdropIntensityText => $"{Math.Round(BackdropIntensity * 100.0)}%";

    /// <summary>Gets the version the settings card's footer shows.</summary>
    public string AppVersion => AppInfo.DisplayVersion;

    /// <summary>
    /// Re-reads the available output devices.
    /// </summary>
    [RelayCommand]
    public void RefreshDevices()
    {
        AudioDevices.Clear();

        foreach (var device in _devices.GetDevices())
        {
            AudioDevices.Add(device);
        }

        var configuredId = _settings.Current.AudioDeviceId;

        // Fall back to the default rather than leaving the selection empty: an empty box reads
        // as "no device" when what is true is "the system default".
        SelectedDevice = AudioDevices.FirstOrDefault(d => d.Id == configuredId)
                         ?? AudioDevices.FirstOrDefault(d => d.IsDefault)
                         ?? AudioDevices.FirstOrDefault();
    }

    /// <summary>
    /// Notes that a connection-mode change needs a restart to take effect.
    /// </summary>
    /// <remarks>
    /// Discovery and advertising are started once. Rebuilding them live would mean tearing down
    /// a connection the user did not ask to lose, so the mode is read at startup and this
    /// surfaces that honestly instead of silently doing nothing.
    /// </remarks>
    public bool ConnectionModeNeedsRestart { get; private set; }

    partial void OnPlayerNameChanged(string value)
    {
        if (_isLoading || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _settings.Update(s => s.PlayerName = value.Trim());
    }

    partial void OnConnectionModeChanged(ConnectionMode value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Update(s => s.ConnectionMode = value);
        ConnectionModeNeedsRestart = true;
        OnPropertyChanged(nameof(ConnectionModeNeedsRestart));
    }

    partial void OnAutoConnectChanged(AutoConnectPolicy value)
    {
        if (!_isLoading)
        {
            _settings.Update(s => s.AutoConnect = value);
        }
    }

    partial void OnPreferredCodecChanged(string value)
    {
        if (!_isLoading)
        {
            _settings.Update(s => s.PreferredCodec = value);
        }
    }

    partial void OnSelectedDeviceChanged(AudioDeviceInfo? value)
    {
        if (_isLoading || value is null)
        {
            return;
        }

        // Load this device's own calibration before switching, so the offset applied is the one
        // that belongs to the device being opened.
        ManualLatencyOffsetMs = _settings.Current.GetManualLatencyOffsetMs(value.Id);

        _work.Run(
            $"switch audio device to {value.Name}",
            token => _player.SwitchAudioDeviceAsync(value.Id, token));
    }

    partial void OnStaticDelayMsChanged(double value)
    {
        if (_isLoading)
        {
            return;
        }

        _work.Run($"apply static delay {value} ms", token => _player.SetStaticDelayAsync(value, token));
    }

    partial void OnManualLatencyOffsetMsChanged(double value)
    {
        if (_isLoading || SelectedDevice is null)
        {
            return;
        }

        _player.SetManualLatencyOffset(SelectedDevice.Id, value);
    }

    partial void OnNotifyTrackChangeChanged(bool value)
    {
        if (!_isLoading)
        {
            _settings.Update(s => s.Notifications.TrackChange = value);
        }
    }

    partial void OnNotifyPlaybackStateChanged(bool value)
    {
        if (!_isLoading)
        {
            _settings.Update(s => s.Notifications.PlaybackState = value);
        }
    }

    partial void OnNotifyConnectionStateChanged(bool value)
    {
        if (!_isLoading)
        {
            _settings.Update(s => s.Notifications.ConnectionState = value);
        }
    }

    partial void OnIncludeArtworkInNotificationsChanged(bool value)
    {
        if (!_isLoading)
        {
            _settings.Update(s => s.Notifications.IncludeArtwork = value);
        }
    }

    partial void OnStartMinimizedToTrayChanged(bool value)
    {
        if (!_isLoading)
        {
            _settings.Update(s => s.StartMinimizedToTray = value);
        }
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (!_isLoading)
        {
            _settings.Update(s => s.CloseToTray = value);
        }
    }

    partial void OnShowSwitchGroupButtonChanged(bool value)
    {
        if (!_isLoading)
        {
            _settings.Update(s => s.ShowSwitchGroupButton = value);
        }
    }

    partial void OnBackdropModeChanged(BackdropMode value)
    {
        if (!_isLoading)
        {
            _settings.Update(s => s.Backdrop.Mode = value);
        }
    }

    partial void OnBackdropIntensityChanged(double value)
    {
        if (!_isLoading)
        {
            _settings.Update(s => s.Backdrop.Intensity = Math.Clamp(value, 0.0, BackdropSettings.MaxIntensity));
        }
    }

    partial void OnDiscordRichPresenceChanged(bool value)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.Update(s => s.DiscordRichPresence = value);

        _work.Run($"set Discord Rich Presence {value}", token => _presence.SetEnabledAsync(value, token));
    }

    /// <summary>
    /// Copies the persisted settings onto the bound properties.
    /// </summary>
    /// <remarks>
    /// <see cref="_isLoading"/> suppresses the change handlers while this runs; without it,
    /// loading would write every value straight back out and, worse, would fire a device switch
    /// and a static-delay re-anchor during startup.
    /// </remarks>
    private void LoadFromSettings()
    {
        _isLoading = true;

        try
        {
            var settings = _settings.Current;

            PlayerName = settings.PlayerName;
            ConnectionMode = settings.ConnectionMode;
            AutoConnect = settings.AutoConnect;
            PreferredCodec = settings.PreferredCodec;
            StaticDelayMs = settings.StaticDelayMs;
            ManualLatencyOffsetMs = settings.GetManualLatencyOffsetMs(settings.AudioDeviceId);
            NotifyTrackChange = settings.Notifications.TrackChange;
            NotifyPlaybackState = settings.Notifications.PlaybackState;
            NotifyConnectionState = settings.Notifications.ConnectionState;
            IncludeArtworkInNotifications = settings.Notifications.IncludeArtwork;
            StartMinimizedToTray = settings.StartMinimizedToTray;
            CloseToTray = settings.CloseToTray;
            DiscordRichPresence = settings.DiscordRichPresence;
            ShowSwitchGroupButton = settings.ShowSwitchGroupButton;
            BackdropMode = settings.Backdrop.Mode;
            BackdropIntensity = settings.Backdrop.Intensity;
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _work.DisposeAsync();
}
