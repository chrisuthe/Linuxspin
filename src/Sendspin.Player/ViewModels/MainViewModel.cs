using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sendspin.Core.Configuration;
using Sendspin.Core.Control;
using Sendspin.Core.MediaSession;
using Sendspin.Core.Platform;
using Sendspin.Core.Presence;
using Sendspin.Platform.Shared.Client;
using Sendspin.Platform.Shared.Notifications;
using Sendspin.SDK.Discovery;

namespace Sendspin.Player.ViewModels;

/// <summary>
/// The main window's view model: connection, transport, and what is playing.
/// </summary>
/// <remarks>
/// <para>
/// Every dependency is required. There is no parameterless constructor and none of the fields
/// are nullable, which is what removes the <c>_clientManager?.</c> and
/// <c>if (x == null) return;</c> pattern that used to run through this class — those made a
/// missing dependency look like a working no-op.
/// </para>
/// <para>
/// The consequence is that the XAML designer has no preview data for the main window: there is
/// no constructor it can call, and the session service cannot be meaningfully stubbed. That is
/// the intended trade — a design-time constructor is only reachable by making every dependency
/// nullable again, and the nullable dependencies were the actual defect.
/// </para>
/// <para>
/// Transport never acts locally. Every command goes through
/// <see cref="PlayerCommandRouter"/> to the server, and the UI updates when the server's group
/// state comes back. That is why the buttons reflect the group rather than local optimism: in a
/// multi-room group this player is not the authority, and pretending otherwise makes it
/// disagree with every other client.
/// </para>
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SendspinPlayerService _player;
    private readonly PlayerCommandRouter _router;
    private readonly SettingsService _settings;
    private readonly IMediaSession _mediaSession;
    private readonly NotificationDispatcher _notifications;
    private readonly IPresenceService _presence;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherTimer _progressTimer;

    /// <summary>
    /// Owns every piece of work this view model starts outside a command.
    /// </summary>
    /// <remarks>
    /// A view model reacting to a property change or an inbound event has to start async work
    /// from a synchronous context. Doing that as <c>_ = SomethingAsync()</c> leaves the task
    /// unowned: nothing cancels it at shutdown and a failure is swallowed as an unobserved
    /// exception, so the work simply stops happening. Routing it through here makes each one
    /// tracked, cancellable and logged.
    /// </remarks>
    private readonly BackgroundTaskSet _work;

    private string? _artworkPath;
    private bool _isDisposed;
    private bool _suppressVolumePush;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStatus))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleShuffleCommand))]
    [NotifyCanExecuteChangedFor(nameof(CycleRepeatCommand))]
    [NotifyCanExecuteChangedFor(nameof(SwitchGroupCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStatus))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isConnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStatus))]
    private string? _serverName;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseLabel))]
    [NotifyPropertyChangedFor(nameof(IsPlaying))]
    private MediaSessionState _state = MediaSessionState.Idle;

    [ObservableProperty]
    private Bitmap? _artwork;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private DiscoveredServer? _selectedServer;

    [ObservableProperty]
    private string _manualServerUrl = string.Empty;

    [ObservableProperty]
    private int _volume = 100;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressFraction))]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private TimeSpan _position;

    [ObservableProperty]
    private bool _isSettingsOpen;

    public MainViewModel(
        SendspinPlayerService player,
        PlayerCommandRouter router,
        SettingsService settings,
        IMediaSession mediaSession,
        NotificationDispatcher notifications,
        IPresenceService presence,
        SettingsViewModel settingsViewModel,
        DiagnosticsViewModel diagnostics,
        ILogger<MainViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mediaSession);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(settingsViewModel);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _player = player;
        _router = router;
        _settings = settings;
        _mediaSession = mediaSession;
        _notifications = notifications;
        _presence = presence;
        _logger = logger;

        Settings = settingsViewModel;
        Diagnostics = diagnostics;
        _work = new BackgroundTaskSet(logger);

        Volume = settings.Current.Volume;
        IsMuted = settings.Current.Muted;
        ManualServerUrl = settings.Current.ManualServerUrl ?? string.Empty;

        _player.ConnectionChanged += OnConnectionChanged;
        _player.StateChanged += OnStateChanged;
        _player.DiscoveredServersChanged += OnDiscoveredServersChanged;
        _mediaSession.IntentReceived += OnMediaSessionIntent;
        _router.LocalActionRequested += OnLocalActionRequested;

        // The protocol reports position only when metadata changes, so a progress bar driven
        // purely by those reports would jump once a track. This advances it between reports and
        // is corrected by each one.
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _progressTimer.Tick += OnProgressTick;
    }

    /// <summary>Gets the settings view model.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Gets the diagnostics view model.</summary>
    public DiagnosticsViewModel Diagnostics { get; }

    /// <summary>Gets the servers currently visible on the network.</summary>
    public ObservableCollection<DiscoveredServer> DiscoveredServers { get; } = [];

    /// <summary>Gets a one-line description of the connection.</summary>
    public string ConnectionStatus => IsConnecting
        ? "Connecting…"
        : IsConnected
            ? $"Connected to {ServerName ?? "server"}"
            : "Not connected";

    /// <summary>Gets whether the group is playing.</summary>
    public bool IsPlaying => State.Status == MediaPlaybackStatus.Playing;

    /// <summary>Gets the play/pause button label.</summary>
    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    /// <summary>
    /// Gets progress through the track as 0-1, or 0 for a live stream.
    /// </summary>
    public double ProgressFraction
    {
        get
        {
            if (State.Duration is not { } duration || duration <= TimeSpan.Zero)
            {
                return 0.0;
            }

            return Math.Clamp(Position.TotalSeconds / duration.TotalSeconds, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Gets the position text.
    /// </summary>
    /// <remarks>
    /// A live stream shows elapsed time only. Showing "3:41 / 0:00", or a progress bar that can
    /// never fill, is worse than showing no total at all — and the protocol's absent duration is
    /// how a live stream is expressed, not missing data.
    /// </remarks>
    public string PositionText
    {
        get
        {
            var elapsed = Format(Position);

            if (State.Duration is not { } duration || duration <= TimeSpan.Zero)
            {
                return $"{elapsed} · live";
            }

            return $"{elapsed} / {Format(duration)}";
        }
    }

    /// <summary>
    /// Starts the session. Called once, after the window exists.
    /// </summary>
    /// <remarks>
    /// A method rather than constructor work: the old view model started async work from its
    /// constructor, which meant nothing could observe a failure and nothing could cancel it.
    /// </remarks>
    public void BeginStartup(IPlatformInitializer platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        _work.Run("startup", _ => StartupAsync(platform));
    }

    /// <summary>
    /// Connects to the selected server.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var server = SelectedServer;
        if (server is null)
        {
            return;
        }

        IsConnecting = true;
        StatusMessage = null;

        try
        {
            await _player.ConnectAsync(server);
        }
        catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException
                                       or TimeoutException or OperationCanceledException)
        {
            _logger.LogError(ex, "Could not connect to {Server}", server.Name);
            StatusMessage = $"Could not connect to {server.Name}: {ex.Message}";
            IsConnecting = false;
        }
    }

    private bool CanConnect() => !IsConnected && !IsConnecting && SelectedServer is not null;

    /// <summary>
    /// Connects to <see cref="ManualServerUrl"/>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnectManually))]
    private async Task ConnectManuallyAsync()
    {
        var url = ManualServerUrl.Trim();
        _settings.Update(s => s.ManualServerUrl = url);

        IsConnecting = true;
        StatusMessage = null;

        try
        {
            await _player.ConnectAsync(url);
        }
        catch (UriFormatException ex)
        {
            _logger.LogWarning(ex, "Manual server URL {Url} is not usable", url);
            StatusMessage = $"'{url}' is not a valid server address.";
            IsConnecting = false;
        }
        catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException
                                       or TimeoutException or OperationCanceledException)
        {
            _logger.LogError(ex, "Could not connect to {Url}", url);
            StatusMessage = $"Could not connect to {url}: {ex.Message}";
            IsConnecting = false;
        }
    }

    private bool CanConnectManually() => !string.IsNullOrWhiteSpace(ManualServerUrl) && !IsConnecting;

    /// <summary>
    /// Disconnects from the current server.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync() => await _player.DisconnectAsync();

    private bool CanDisconnect() => IsConnected;

    /// <summary>
    /// Toggles play and pause.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanControl))]
    private Task PlayPauseAsync() => Route(MediaSessionIntent.TogglePlayPause);

    /// <summary>
    /// Skips to the next track.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanControl))]
    private Task NextAsync() => Route(MediaSessionIntent.Next);

    /// <summary>
    /// Returns to the previous track.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanControl))]
    private Task PreviousAsync() => Route(MediaSessionIntent.Previous);

    /// <summary>
    /// Toggles shuffle.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanControl))]
    private Task ToggleShuffleAsync() => Route(MediaSessionIntent.ToggleShuffle);

    /// <summary>
    /// Advances the repeat mode.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanControl))]
    private Task CycleRepeatAsync() => Route(MediaSessionIntent.CycleRepeat);

    /// <summary>
    /// Asks the server to move this player to the next group.
    /// </summary>
    /// <remarks>
    /// The command is <c>switch</c>. This app previously sent <c>switch_group</c>, which no
    /// server implements.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanControl))]
    private async Task SwitchGroupAsync() =>
        await _player.SendCommandAsync(SDK.Protocol.Messages.Commands.Switch);

    private bool CanControl() => IsConnected;

    /// <summary>
    /// Shows or hides the settings pane.
    /// </summary>
    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    /// <summary>
    /// Shows or hides the diagnostics pane.
    /// </summary>
    [RelayCommand]
    private void ToggleDiagnostics()
    {
        Diagnostics.SetVisible(!Diagnostics.IsVisible);
        _settings.Update(s => s.ShowDiagnostics = Diagnostics.IsVisible);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        _progressTimer.Stop();
        _progressTimer.Tick -= OnProgressTick;

        _player.ConnectionChanged -= OnConnectionChanged;
        _player.StateChanged -= OnStateChanged;
        _player.DiscoveredServersChanged -= OnDiscoveredServersChanged;
        _mediaSession.IntentReceived -= OnMediaSessionIntent;
        _router.LocalActionRequested -= OnLocalActionRequested;

        // Cancel and drain outstanding work before releasing what it touches.
        await _work.DisposeAsync();

        Diagnostics.Dispose();
        Artwork?.Dispose();
    }

    private static string Format(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    private async Task StartupAsync(IPlatformInitializer platform)
    {
        try
        {
            await platform.InitializeAsync();
            await _mediaSession.InitializeAsync();

            if (_settings.Current.DiscordRichPresence)
            {
                await _presence.SetEnabledAsync(enabled: true);
            }

            if (_settings.Current.ShowDiagnostics)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Diagnostics.SetVisible(true));
            }

            await _player.StartAsync();
        }
        catch (Exception ex)
        {
            // Startup covers discovery, advertising, D-Bus and the audio stack, any of which can
            // be absent on a given machine. The app must still run, and the reason must reach
            // the user rather than only the log.
            _logger.LogError(ex, "Startup did not complete");
            await Dispatcher.UIThread.InvokeAsync(() =>
                StatusMessage = $"Startup problem: {ex.Message}");
        }
    }

    private async Task Route(MediaSessionIntent intent)
    {
        try
        {
            await _router.RouteAsync(new MediaSessionIntentEventArgs(intent));
        }
        catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException)
        {
            _logger.LogWarning(ex, "Command {Intent} could not be sent", intent);
            StatusMessage = "The server connection dropped.";
        }
    }

    /// <summary>
    /// Handles an intent from an OS media surface.
    /// </summary>
    /// <remarks>
    /// Arrives on whatever thread the platform used — an MTA pool thread from SMTC, a D-Bus
    /// reader thread from MPRIS — so it is marshalled here. It then takes the same route as a
    /// button press, which is what keeps a media key and a click from behaving differently.
    /// </remarks>
    private void OnMediaSessionIntent(object? sender, MediaSessionIntentEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
            _work.Run($"media session intent {e.Intent}", token => _router.RouteAsync(e, token)));

    private void OnLocalActionRequested(object? sender, LocalAction action)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (action)
            {
                case LocalAction.Raise:
                    (Avalonia.Application.Current as App)?.ShowMainWindow();
                    break;

                case LocalAction.Quit:
                    (Avalonia.Application.Current as App)?.RequestShutdown();
                    break;

                default:
                    _logger.LogWarning("Unhandled local action {Action}", action);
                    break;
            }
        });
    }

    private void OnConnectionChanged(object? sender, ConnectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = e.IsConnected;
            IsConnecting = false;
            ServerName = e.ServerName;

            if (e.IsConnected)
            {
                _progressTimer.Start();
            }
            else
            {
                _progressTimer.Stop();
                Position = TimeSpan.Zero;
                State = MediaSessionState.Idle;
                Artwork?.Dispose();
                Artwork = null;
                _artworkPath = null;
            }
        });

        _work.Run(
            "connection notification",
            token => _notifications.OnConnectionAsync(e.ServerName ?? "Sendspin server", e.IsConnected, token));
    }

    private void OnStateChanged(object? sender, MediaSessionState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            State = state;
            Position = state.Position;

            _suppressVolumePush = true;
            try
            {
                Volume = state.Volume;
                IsMuted = state.Muted;
            }
            finally
            {
                _suppressVolumePush = false;
            }

            LoadArtwork(state.ArtworkFilePath);
        });

        _mediaSession.Publish(state);
        _presence.Publish(state, _player.ServerName);
        _work.Run("state notification", token => _notifications.OnStateAsync(state, token));
    }

    private void OnDiscoveredServersChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var current = _player.DiscoveredServers;
            var selectedId = SelectedServer?.ServerId;

            DiscoveredServers.Clear();
            foreach (var server in current.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                DiscoveredServers.Add(server);
            }

            // Keep the user's selection across a refresh; losing it mid-click is how a
            // connect button ends up doing nothing.
            SelectedServer = DiscoveredServers.FirstOrDefault(s => s.ServerId == selectedId)
                             ?? DiscoveredServers.FirstOrDefault();
        });
    }

    private void OnProgressTick(object? sender, EventArgs e)
    {
        if (!IsPlaying)
        {
            return;
        }

        Position += _progressTimer.Interval;
    }

    /// <summary>
    /// Loads artwork from the cache file the player service wrote.
    /// </summary>
    private void LoadArtwork(string? path)
    {
        if (path == _artworkPath)
        {
            return;
        }

        _artworkPath = path;

        var previous = Artwork;

        if (path is null || !File.Exists(path))
        {
            Artwork = null;
            previous?.Dispose();
            return;
        }

        try
        {
            Artwork = new Bitmap(path);
        }
        catch (ArgumentException ex)
        {
            // Bitmap throws this for data it cannot decode. Artwork is decoration; a picture the
            // renderer refuses must not stop playback.
            _logger.LogWarning(ex, "Artwork at {Path} could not be decoded", path);
            Artwork = null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Artwork at {Path} could not be read", path);
            Artwork = null;
        }

        previous?.Dispose();
    }

    partial void OnVolumeChanged(int value)
    {
        if (_suppressVolumePush || _isDisposed)
        {
            return;
        }

        _work.Run($"set volume {value}", token => _player.SetVolumeAsync(value, token));
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (_suppressVolumePush || _isDisposed)
        {
            return;
        }

        _work.Run($"set mute {value}", token => _player.SetMuteAsync(value, token));
    }
}
