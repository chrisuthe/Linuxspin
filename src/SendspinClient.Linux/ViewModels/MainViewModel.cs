using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Discovery;
using SendspinClient.Linux.Services.Client;
using SendspinClient.Linux.Services.Discord;
using SendspinClient.Linux.Services.Notifications;

namespace SendspinClient.Linux.ViewModels;

/// <summary>
/// Main view model for the Sendspin Linux client.
/// Manages server connection state, playback controls, and audio settings.
/// </summary>
public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ILogger<MainViewModel>? _logger;
    private readonly SendspinClientManager? _clientManager;
    private readonly INotificationService? _notificationService;
    private readonly IDiscordRichPresenceService? _discordService;

    #region Observable Properties

    /// <summary>
    /// Gets or sets the name of the connected server.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    private string _serverName = string.Empty;

    /// <summary>
    /// Gets or sets the title of the currently playing track.
    /// </summary>
    [ObservableProperty]
    private string _trackTitle = "No Track Playing";

    /// <summary>
    /// Gets or sets the artist of the currently playing track.
    /// </summary>
    [ObservableProperty]
    private string _artist = string.Empty;

    /// <summary>
    /// Gets or sets the album of the currently playing track.
    /// </summary>
    [ObservableProperty]
    private string _album = string.Empty;

    /// <summary>
    /// Gets or sets the album artwork image.
    /// </summary>
    [ObservableProperty]
    private Bitmap? _albumArtwork;

    /// <summary>
    /// Gets or sets the current volume level (0-100).
    /// </summary>
    [ObservableProperty]
    private double _volume = 100.0;

    /// <summary>
    /// Gets or sets a value indicating whether the client is connected to a server.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    [NotifyPropertyChangedFor(nameof(PlayPauseButtonText))]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextTrackCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousTrackCommand))]
    [NotifyCanExecuteChangedFor(nameof(SwitchGroupCommand))]
    private bool _isConnected;

    /// <summary>
    /// Gets or sets a value indicating whether playback is currently paused.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseButtonText))]
    [NotifyPropertyChangedFor(nameof(PlaybackStatusText))]
    private bool _isPaused;

    /// <summary>
    /// Gets or sets a value indicating whether a connection attempt is in progress.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isConnecting;

    /// <summary>
    /// Gets or sets a value indicating whether discovery is running.
    /// </summary>
    [ObservableProperty]
    private bool _isDiscovering;

    /// <summary>
    /// Gets or sets the currently selected audio output device identifier.
    /// </summary>
    [ObservableProperty]
    private string? _selectedDeviceId;

    /// <summary>
    /// Gets or sets the selected server for connection.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private DiscoveredServer? _selectedServer;

    /// <summary>
    /// Gets the list of discovered servers.
    /// </summary>
    public ObservableCollection<DiscoveredServer> DiscoveredServers { get; } = new();

    #endregion

    #region Computed Properties

    /// <summary>
    /// Gets the text to display for the current connection status.
    /// </summary>
    public string ConnectionStatusText
    {
        get
        {
            if (IsConnecting)
                return "Connecting...";
            if (IsConnected)
                return $"Connected to {ServerName}";
            return "Disconnected";
        }
    }

    /// <summary>
    /// Gets the text to display on the play/pause button.
    /// </summary>
    public string PlayPauseButtonText => IsPaused ? "Play" : "Pause";

    /// <summary>
    /// Gets the text to display for the current playback status.
    /// </summary>
    public string PlaybackStatusText => IsPaused ? "Paused" : "Playing";

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="MainViewModel"/> class.
    /// </summary>
    public MainViewModel()
    {
        // Parameterless constructor for design-time support
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MainViewModel"/> class with all services.
    /// </summary>
    public MainViewModel(
        ILogger<MainViewModel> logger,
        SendspinClientManager clientManager,
        INotificationService notificationService,
        IDiscordRichPresenceService discordService)
    {
        _logger = logger;
        _clientManager = clientManager;
        _notificationService = notificationService;
        _discordService = discordService;

        // Subscribe to client manager events
        _clientManager.ServerDiscovered += OnServerDiscovered;
        _clientManager.ServerLost += OnServerLost;
        _clientManager.ConnectionStateChanged += OnConnectionStateChanged;
        _clientManager.TrackChanged += OnTrackChanged;
        _clientManager.ArtworkChanged += OnArtworkChanged;
        _clientManager.PlaybackStateChanged += OnPlaybackStateChanged;

        _logger.LogDebug("MainViewModel initialized with platform services");

        _ = InitializeServicesAsync();
    }

    /// <summary>
    /// Initializes platform services asynchronously.
    /// </summary>
    private async Task InitializeServicesAsync()
    {
        try
        {
            var tasks = new[]
            {
                _notificationService?.InitializeAsync() ?? Task.CompletedTask,
                _discordService?.InitializeAsync() ?? Task.CompletedTask
            };
            await Task.WhenAll(tasks);
            _logger?.LogInformation("Platform services initialized");

            // Start server discovery automatically
            await StartDiscoveryAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Some platform services failed to initialize");
        }
    }

    #region Discovery

    /// <summary>
    /// Starts discovering Sendspin servers on the local network.
    /// </summary>
    private async Task StartDiscoveryAsync()
    {
        if (_clientManager == null || IsDiscovering) return;

        try
        {
            IsDiscovering = true;
            await _clientManager.StartDiscoveryAsync();
            _logger?.LogInformation("Server discovery started");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start server discovery");
            IsDiscovering = false;
        }
    }

    private void OnServerDiscovered(object? sender, DiscoveredServer server)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!DiscoveredServers.Contains(server))
            {
                DiscoveredServers.Add(server);
                _logger?.LogInformation("Added server to list: {Name}", server.Name);
            }
        });
    }

    private void OnServerLost(object? sender, DiscoveredServer server)
    {
        Dispatcher.UIThread.Post(() =>
        {
            DiscoveredServers.Remove(server);
            _logger?.LogInformation("Removed server from list: {Name}", server.Name);
        });
    }

    #endregion

    #region Commands

    /// <summary>
    /// Connects to the selected Sendspin server.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (IsConnected || IsConnecting || _clientManager == null)
            return;

        var server = SelectedServer;
        if (server == null)
        {
            _logger?.LogWarning("No server selected for connection");
            return;
        }

        try
        {
            IsConnecting = true;
            _logger?.LogInformation("Connecting to server: {Name}", server.Name);

            await _clientManager.ConnectAsync(server);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to connect to server");
            await DispatcherInvokeAsync(() =>
            {
                IsConnecting = false;
                IsConnected = false;
            });
        }
    }

    private bool CanConnect() => !IsConnected && !IsConnecting && SelectedServer != null;

    /// <summary>
    /// Disconnects from the current Sendspin server.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        if (!IsConnected || _clientManager == null)
            return;

        try
        {
            _logger?.LogInformation("Disconnecting from server: {ServerName}", ServerName);
            await _clientManager.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during disconnect");
        }
    }

    private bool CanDisconnect() => IsConnected;

    /// <summary>
    /// Toggles playback between play and pause states.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPlayPause))]
    private async Task PlayPauseAsync()
    {
        if (!IsConnected || _clientManager == null)
            return;

        try
        {
            _logger?.LogDebug("Toggling playback state. Current state: {IsPaused}", IsPaused ? "Paused" : "Playing");

            if (IsPaused)
                await _clientManager.PlayAsync();
            else
                await _clientManager.PauseAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error toggling playback state");
        }
    }

    private bool CanPlayPause() => IsConnected;

    /// <summary>
    /// Skips to the next track.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSkipTrack))]
    private async Task NextTrackAsync()
    {
        if (!IsConnected || _clientManager == null)
            return;

        try
        {
            _logger?.LogDebug("Skipping to next track");
            await _clientManager.NextAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error skipping to next track");
        }
    }

    /// <summary>
    /// Returns to the previous track.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSkipTrack))]
    private async Task PreviousTrackAsync()
    {
        if (!IsConnected || _clientManager == null)
            return;

        try
        {
            _logger?.LogDebug("Returning to previous track");
            await _clientManager.PreviousAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error returning to previous track");
        }
    }

    private bool CanSkipTrack() => IsConnected;

    /// <summary>
    /// Switches to the next player group.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSwitchGroup))]
    private async Task SwitchGroupAsync()
    {
        if (!IsConnected || _clientManager == null)
            return;

        try
        {
            _logger?.LogDebug("Switching player group");
            await _clientManager.SwitchGroupAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error switching player group");
        }
    }

    private bool CanSwitchGroup() => IsConnected;

    #endregion

    #region Event Handlers

    private void OnConnectionStateChanged(object? sender, ConnectionStateEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = e.IsConnected;
            IsConnecting = false;
            ServerName = e.ServerName ?? string.Empty;

            if (!e.IsConnected)
            {
                TrackTitle = "No Track Playing";
                Artist = string.Empty;
                Album = string.Empty;
                IsPaused = false;
                AlbumArtwork?.Dispose();
                AlbumArtwork = null;
            }
        });

        // Notify connection status change
        _ = _notificationService?.ShowConnectionStatusAsync(e.ServerName ?? "Server", e.IsConnected);

        // Update Discord presence
        if (e.IsConnected)
            _discordService?.UpdatePresence(null, null, e.ServerName, false);
        else
            _discordService?.ClearPresence();
    }

    private void OnTrackChanged(object? sender, TrackMetadataEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TrackTitle = string.IsNullOrWhiteSpace(e.Title) ? "No Track Playing" : e.Title;
            Artist = e.Artist ?? string.Empty;
            Album = e.Album ?? string.Empty;
        });

        _logger?.LogDebug("Track info updated: {Title} - {Artist}", e.Title, e.Artist);

        // Fire-and-forget notification
        _ = _notificationService?.ShowTrackChangeAsync(e.Title, e.Artist);

        // Update Discord Rich Presence
        _discordService?.UpdatePresence(e.Title, e.Artist, ServerName, !IsPaused);
    }

    private void OnArtworkChanged(object? sender, ArtworkEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Dispose old bitmap to free resources
            AlbumArtwork?.Dispose();

            if (e.ImageData == null || e.ImageData.Length == 0)
            {
                AlbumArtwork = null;
                _logger?.LogDebug("Artwork cleared");
                return;
            }

            try
            {
                using var stream = new MemoryStream(e.ImageData);
                AlbumArtwork = new Bitmap(stream);
                _logger?.LogDebug("Artwork loaded: {Width}x{Height}",
                    AlbumArtwork.PixelSize.Width, AlbumArtwork.PixelSize.Height);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load artwork");
                AlbumArtwork = null;
            }
        });
    }

    private void OnPlaybackStateChanged(object? sender, PlaybackStateEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsPaused = !e.IsPlaying;
            _logger?.LogDebug("Playback state updated: IsPaused={IsPaused}", IsPaused);
        });

        // Update Discord presence with correct playing state
        _discordService?.UpdatePresence(TrackTitle, Artist, ServerName, e.IsPlaying);
    }

    #endregion

    #region Volume Control

    partial void OnVolumeChanged(double value)
    {
        if (_clientManager != null && IsConnected)
        {
            _ = _clientManager.SetVolumeAsync((int)value);
        }
    }

    #endregion

    #region Private Helpers

    private static Task DispatcherInvokeAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    #endregion

    #region Cleanup

    public async ValueTask DisposeAsync()
    {
        if (_clientManager != null)
        {
            _clientManager.ServerDiscovered -= OnServerDiscovered;
            _clientManager.ServerLost -= OnServerLost;
            _clientManager.ConnectionStateChanged -= OnConnectionStateChanged;
            _clientManager.TrackChanged -= OnTrackChanged;
            _clientManager.ArtworkChanged -= OnArtworkChanged;
            _clientManager.PlaybackStateChanged -= OnPlaybackStateChanged;

            await _clientManager.DisposeAsync();
        }

        AlbumArtwork?.Dispose();
    }

    #endregion
}
