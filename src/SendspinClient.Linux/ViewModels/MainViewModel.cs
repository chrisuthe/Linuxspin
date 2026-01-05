using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace SendspinClient.Linux.ViewModels;

/// <summary>
/// Main view model for the Sendspin Linux client.
/// Manages server connection state, playback controls, and audio settings.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ILogger<MainViewModel>? _logger;

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
    /// Gets or sets the currently selected audio output device identifier.
    /// </summary>
    [ObservableProperty]
    private string? _selectedDeviceId;

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
    /// Initializes a new instance of the <see cref="MainViewModel"/> class with logging support.
    /// </summary>
    /// <param name="logger">The logger instance for this view model.</param>
    public MainViewModel(ILogger<MainViewModel> logger)
    {
        _logger = logger;
        _logger.LogDebug("MainViewModel initialized");
    }

    #region Commands

    /// <summary>
    /// Connects to a Sendspin server.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (IsConnected || IsConnecting)
            return;

        try
        {
            IsConnecting = true;
            _logger?.LogInformation("Attempting to connect to server...");

            // TODO: Implement actual server discovery and connection via Sendspin.SDK
            // This is a placeholder that simulates the connection process
            await Task.Delay(1000);

            // Simulate successful connection
            await DispatcherInvokeAsync(() =>
            {
                ServerName = "Music Assistant";
                IsConnected = true;
                IsConnecting = false;
                IsPaused = true;
            });

            _logger?.LogInformation("Successfully connected to server: {ServerName}", ServerName);
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

    /// <summary>
    /// Determines whether the connect command can execute.
    /// </summary>
    /// <returns><c>true</c> if the command can execute; otherwise, <c>false</c>.</returns>
    private bool CanConnect() => !IsConnected && !IsConnecting;

    /// <summary>
    /// Disconnects from the current Sendspin server.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        if (!IsConnected)
            return;

        try
        {
            _logger?.LogInformation("Disconnecting from server: {ServerName}", ServerName);

            // TODO: Implement actual disconnection via Sendspin.SDK
            await Task.Delay(100);

            await DispatcherInvokeAsync(() =>
            {
                IsConnected = false;
                ServerName = string.Empty;
                TrackTitle = "No Track Playing";
                Artist = string.Empty;
                IsPaused = false;
            });

            _logger?.LogInformation("Disconnected from server");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during disconnect");
        }
    }

    /// <summary>
    /// Determines whether the disconnect command can execute.
    /// </summary>
    /// <returns><c>true</c> if the command can execute; otherwise, <c>false</c>.</returns>
    private bool CanDisconnect() => IsConnected;

    /// <summary>
    /// Toggles playback between play and pause states.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand(CanExecute = nameof(CanPlayPause))]
    private async Task PlayPauseAsync()
    {
        if (!IsConnected)
            return;

        try
        {
            _logger?.LogDebug("Toggling playback state. Current state: {IsPaused}", IsPaused ? "Paused" : "Playing");

            // TODO: Implement actual playback control via Sendspin.SDK
            await Task.Delay(50);

            await DispatcherInvokeAsync(() =>
            {
                IsPaused = !IsPaused;
            });

            _logger?.LogDebug("Playback state changed to: {IsPaused}", IsPaused ? "Paused" : "Playing");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error toggling playback state");
        }
    }

    /// <summary>
    /// Determines whether the play/pause command can execute.
    /// </summary>
    /// <returns><c>true</c> if the command can execute; otherwise, <c>false</c>.</returns>
    private bool CanPlayPause() => IsConnected;

    #endregion

    #region Public Methods

    /// <summary>
    /// Updates the current track information from the server.
    /// </summary>
    /// <param name="title">The track title.</param>
    /// <param name="artist">The artist name.</param>
    public void UpdateTrackInfo(string title, string artist)
    {
        DispatcherInvokeAsync(() =>
        {
            TrackTitle = string.IsNullOrWhiteSpace(title) ? "No Track Playing" : title;
            Artist = artist ?? string.Empty;
        }).ConfigureAwait(false);

        _logger?.LogDebug("Track info updated: {Title} - {Artist}", title, artist);
    }

    /// <summary>
    /// Updates the connection state.
    /// </summary>
    /// <param name="isConnected">Whether the client is connected.</param>
    /// <param name="serverName">The name of the server, if connected.</param>
    public void UpdateConnectionState(bool isConnected, string? serverName = null)
    {
        DispatcherInvokeAsync(() =>
        {
            IsConnected = isConnected;
            ServerName = serverName ?? string.Empty;

            if (!isConnected)
            {
                TrackTitle = "No Track Playing";
                Artist = string.Empty;
                IsPaused = false;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the playback state.
    /// </summary>
    /// <param name="isPaused">Whether playback is paused.</param>
    public void UpdatePlaybackState(bool isPaused)
    {
        DispatcherInvokeAsync(() =>
        {
            IsPaused = isPaused;
        }).ConfigureAwait(false);
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Invokes an action on the UI thread via the Avalonia dispatcher.
    /// </summary>
    /// <param name="action">The action to invoke.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
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
}
