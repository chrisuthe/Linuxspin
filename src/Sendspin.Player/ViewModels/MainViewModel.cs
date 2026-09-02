using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
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
using Sendspin.Player.Threading;
using Sendspin.SDK.Client;
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

    /// <summary>
    /// The veil's opacity over the ambient glow, as a factor on the <c>VeilBrush</c> token's own
    /// 0.75: together they make 0.5, which lets the glow read as colour rather than as a tint.
    /// Over the blurred art the veil stays at the token's full strength.
    /// </summary>
    internal const double AmbientVeilFactor = 0.5 / 0.75;

    /// <summary>
    /// Advances the progress bar between the server's position reports, which arrive only when
    /// metadata changes.
    /// </summary>
    /// <remarks>
    /// The position is projected from the last report by measured time, not stepped by the
    /// timer's nominal interval: the timer only decides how often the bar repaints, and being
    /// late on one head (see <see cref="UiClock"/>) then costs smoothness rather than accuracy.
    /// </remarks>
    private readonly UiClock _progressClock = new(TimeSpan.FromMilliseconds(500));
    private readonly AnchoredPosition _anchoredPosition = new();

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

    /// <summary>
    /// The blurred backdrop's source size. Small on purpose: the blur is applied once to this
    /// bitmap and cached, and at 64 px the whole layer costs a few tenths of a millisecond per
    /// frame (the "Effect loop cost" table in <c>docs/ARCHITECTURE.md</c>, <c>blur-once</c>).
    /// </summary>
    internal static readonly PixelSize BackdropSize = new(64, 64);

    private string? _artworkPath;
    private string? _promptServerId;
    private bool _isDisposed;
    private bool _suppressVolumePush;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStatus))]
    [NotifyPropertyChangedFor(nameof(HasFooterStatus))]
    [NotifyPropertyChangedFor(nameof(HasFooter))]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    [NotifyPropertyChangedFor(nameof(ShowsNowPlaying))]
    [NotifyPropertyChangedFor(nameof(ShowsWelcome))]
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
    [NotifyPropertyChangedFor(nameof(HasFooterStatus))]
    [NotifyPropertyChangedFor(nameof(HasFooter))]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseLabel))]
    [NotifyPropertyChangedFor(nameof(IsPlaying))]
    [NotifyPropertyChangedFor(nameof(HasKnownDuration))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    [NotifyPropertyChangedFor(nameof(RepeatTooltip))]
    [NotifyPropertyChangedFor(nameof(ProgressFraction))]
    private MediaSessionState _state = MediaSessionState.Idle;

    [ObservableProperty]
    private Bitmap? _artwork;

    /// <summary>
    /// The artwork scaled to <see cref="BackdropSize"/>, for the blurred layer behind the content.
    /// </summary>
    [ObservableProperty]
    private Bitmap? _artBackdrop;

    /// <summary>
    /// Whether the once-per-server auto-connect question is showing.
    /// </summary>
    [ObservableProperty]
    private bool _isAutoConnectPromptOpen;

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
    [NotifyPropertyChangedFor(nameof(ElapsedText))]
    private TimeSpan _position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNowPlaying))]
    [NotifyPropertyChangedFor(nameof(ShowsWelcome))]
    private bool _isSettingsOpen;

    /// <summary>
    /// Whether the blurred-artwork layer has a bitmap to show: connected, <see cref="ArtBackdrop"/>
    /// is set, and the ambient glow is not running over it (the glow hides the blurred art, as the
    /// reference does; both at once is mud).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackdrop))]
    private bool _hasArtBackdrop;

    /// <summary>
    /// Whether the ambient glow is running: connected, and <see cref="AmbientBackdropViewModel.IsActive"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackdrop))]
    [NotifyPropertyChangedFor(nameof(VeilOpacity))]
    private bool _hasAmbientBackdrop;

    public MainViewModel(
        SendspinPlayerService player,
        PlayerCommandRouter router,
        SettingsService settings,
        IMediaSession mediaSession,
        NotificationDispatcher notifications,
        IPresenceService presence,
        SettingsViewModel settingsViewModel,
        DiagnosticsViewModel diagnostics,
        AmbientBackdropViewModel backdrop,
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
        ArgumentNullException.ThrowIfNull(backdrop);
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
        Backdrop = backdrop;
        _work = new BackgroundTaskSet(logger);

        Volume = settings.Current.Volume;
        IsMuted = settings.Current.Muted;
        ManualServerUrl = settings.Current.ManualServerUrl ?? string.Empty;

        // Read once, like the player service reads it: the mode a user changes in Settings takes
        // effect after a restart, and the Welcome card describes what the service is doing now.
        IsAdvertising = settings.Current.ConnectionMode == ConnectionMode.AdvertiseOnly;
        IsDiscovering = settings.Current.ConnectionMode == ConnectionMode.DiscoverOnly;

        DiscoveredServers.CollectionChanged += OnDiscoveredServersCollectionChanged;

        _player.ConnectionChanged += OnConnectionChanged;
        _player.StateChanged += OnStateChanged;
        _player.DiscoveredServersChanged += OnDiscoveredServersChanged;
        _player.PaletteChanged += OnPaletteChanged;
        _player.VisualizerFrameReceived += OnVisualizerFrameReceived;
        _mediaSession.IntentReceived += OnMediaSessionIntent;
        _router.LocalActionRequested += OnLocalActionRequested;
        Backdrop.PropertyChanged += OnBackdropPropertyChanged;

        _progressClock.Tick += OnProgressTick;
    }

    /// <summary>
    /// Raised when the user asks for the Stats window, whether or not it is already open, so the
    /// window can come forward either way. <see cref="DiagnosticsViewModel.IsVisible"/> says
    /// whether it should be open at all.
    /// </summary>
    public event EventHandler? StatsRequested;

    /// <summary>Gets the settings view model.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>Gets the diagnostics view model.</summary>
    public DiagnosticsViewModel Diagnostics { get; }

    /// <summary>Gets the living backdrop's view model: the palette, the signal, and the style that runs.</summary>
    public AmbientBackdropViewModel Backdrop { get; }

    /// <summary>Gets the servers currently visible on the network.</summary>
    public ObservableCollection<DiscoveredServer> DiscoveredServers { get; } = [];

    /// <summary>Gets a one-line description of the connection.</summary>
    public string ConnectionStatus => IsConnecting
        ? "Connecting…"
        : IsConnected
            ? $"Connected to {ServerName ?? "server"}"
            : "Not connected";

    /// <summary>
    /// Gets whether Now Playing is on screen: connected, and the settings card is not over it.
    /// </summary>
    /// <remarks>
    /// The card's surface is translucent so the blurred backdrop tints it, and that only reads
    /// while what is under it is the backdrop: over the art tile and the title the rows were
    /// illegible. So the body content steps aside while the card is open and the backdrop layers
    /// stay.
    /// </remarks>
    public bool ShowsNowPlaying => IsConnected && !IsSettingsOpen;

    /// <summary>Gets whether Welcome is on screen: not connected, and the settings card is not over it.</summary>
    public bool ShowsWelcome => !IsConnected && !IsSettingsOpen;

    /// <summary>Gets whether either backdrop layer is showing, which is when the veil is needed.</summary>
    public bool HasBackdrop => HasArtBackdrop || HasAmbientBackdrop;

    /// <summary>
    /// Gets the veil's opacity: <see cref="AmbientVeilFactor"/> over the glow, full over the art.
    /// </summary>
    public double VeilOpacity => HasAmbientBackdrop ? AmbientVeilFactor : 1.0;

    /// <summary>
    /// Gets whether the footer shows the status message in place of the volume row: there is
    /// one, and no connection for the volume row to be about.
    /// </summary>
    public bool HasFooterStatus => !IsConnected && StatusMessage is not null;

    /// <summary>
    /// Gets whether the footer has anything to show: the volume row while connected, or a
    /// status message while not. Disconnected with nothing to say, it collapses rather than
    /// showing a disabled volume row under a screen that has no connection.
    /// </summary>
    public bool HasFooter => IsConnected || HasFooterStatus;

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

    /// <summary>Gets whether the track has a duration, which is when a progress bar can fill.</summary>
    /// <remarks>
    /// The protocol's absent duration is how a live stream is expressed, not missing data, and
    /// <see cref="MediaSessionState.IsLive"/> is the one rule for it.
    /// </remarks>
    public bool HasKnownDuration => !State.IsLive;

    /// <summary>Gets the elapsed time, for the left of the progress row.</summary>
    public string ElapsedText => Format(Position);

    /// <summary>
    /// Gets the duration, for the right of the progress row: the track's length, or
    /// <c>LIVE</c> for a stream that has none.
    /// </summary>
    public string DurationText =>
        State.Duration is { } duration && HasKnownDuration ? Format(duration) : "LIVE";

    /// <summary>Gets the repeat button's tooltip, naming the mode it currently shows.</summary>
    public string RepeatTooltip => State.Repeat switch
    {
        MediaRepeatMode.One => "Repeat one",
        MediaRepeatMode.All => "Repeat all",
        _ => "Repeat off",
    };

    /// <summary>Gets whether the player is advertising itself for a server to connect to.</summary>
    public bool IsAdvertising { get; }

    /// <summary>Gets whether the player is discovering servers to connect to.</summary>
    public bool IsDiscovering { get; }

    /// <summary>Gets whether discovery is running and has nothing to show yet.</summary>
    public bool IsSearching => IsDiscovering && !IsConnected && DiscoveredServers.Count == 0;

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
        catch (Exception ex)
        {
            // Deliberately broad. Connecting spans mDNS, a socket, a WebSocket handshake and the
            // SDK's protocol handling, and the exception types are not enumerable from here. What
            // matters is that IsConnecting is cleared: only OnConnectionChanged clears it otherwise,
            // and that never fires on a failed connect — so anything unhandled would leave the
            // Connect button disabled for the rest of the session.
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
        catch (Exception ex)
        {
            // Broad for the same reason as ConnectAsync: clearing IsConnecting matters more than
            // enumerating the failure modes of the whole network stack.
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
    private async Task DisconnectAsync()
    {
        try
        {
            await _player.DisconnectAsync();
        }
        catch (Exception ex)
        {
            // A disconnect that throws has still disconnected as far as the user is concerned, and
            // CommunityToolkit rethrows an unhandled command exception onto the UI thread.
            _logger.LogWarning(ex, "Error while disconnecting");
        }
    }

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
    /// Shows or hides the settings card.
    /// </summary>
    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    /// <summary>
    /// Closes the settings card: the card's Done button. Nothing is applied here, because
    /// every setting has already been written.
    /// </summary>
    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    /// <summary>
    /// Opens the Stats window, or brings it forward when it is already open.
    /// </summary>
    [RelayCommand]
    private void OpenStats()
    {
        SetStatsVisible(true);
        StatsRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens or closes the Stats window, and remembers which for the next start.
    /// </summary>
    /// <remarks>
    /// <see cref="DiagnosticsViewModel.IsVisible"/> is the one fact the window follows and the
    /// one that runs the refresh clock; the window's own close comes back through here so the
    /// clock, the window and the persisted flag cannot disagree.
    /// </remarks>
    internal void SetStatsVisible(bool visible)
    {
        Diagnostics.SetVisible(visible);
        _settings.Update(s => s.ShowDiagnostics = visible);
    }

    /// <summary>
    /// Reopens the Stats window if it was open when the player last exited.
    /// </summary>
    /// <remarks>
    /// <see cref="PlayerSettings.ShowDiagnostics"/> is written on every open and close, so at
    /// start it says whether the window was up at exit. Called from the start-up path, on the
    /// UI thread; the flag is already right, so only the view model changes.
    /// </remarks>
    internal void ReopenStatsIfLeftOpen()
    {
        if (_settings.Current.ShowDiagnostics)
        {
            Diagnostics.SetVisible(true);
        }
    }

    /// <summary>Answers the auto-connect question with "just once".</summary>
    [RelayCommand]
    private void AutoConnectJustOnce() => AnswerAutoConnectPrompt(AutoConnectPolicy.JustOnce);

    /// <summary>Answers the auto-connect question with "always".</summary>
    [RelayCommand]
    private void AutoConnectAlways() => AnswerAutoConnectPrompt(AutoConnectPolicy.Always);

    /// <summary>Answers the auto-connect question with "not now", which leaves the policy alone.</summary>
    [RelayCommand]
    private void AutoConnectNotNow() => AnswerAutoConnectPrompt(AutoConnectPolicy.Never);

    /// <summary>
    /// Shows the auto-connect question if this connection is one to ask it for.
    /// </summary>
    /// <remarks>
    /// Asked once per server, after a connection the user made in discover mode while the
    /// policy is <see cref="AutoConnectPolicy.Never"/>. The server's id is read from
    /// <see cref="PlayerSettings.LastServerId"/>, which the player service writes before it
    /// raises the connection event; an auto-connect the service made itself never gets here with
    /// <c>Never</c> unless it was "just once", and that server has already been asked about.
    /// </remarks>
    private void OfferAutoConnectPrompt()
    {
        var settings = _settings.Current;

        if (!IsDiscovering
            || settings.AutoConnect != AutoConnectPolicy.Never
            || settings.LastServerId is not { } serverId
            || serverId == settings.AutoConnectPromptedServerId)
        {
            return;
        }

        _promptServerId = serverId;
        IsAutoConnectPromptOpen = true;
    }

    /// <remarks>
    /// The policy goes through <see cref="SettingsViewModel"/> rather than straight to the
    /// service so the Settings combo shows the answer; it writes through to the same
    /// <see cref="SettingsService.Update"/>. The record of having asked is written here.
    /// </remarks>
    private void AnswerAutoConnectPrompt(AutoConnectPolicy policy)
    {
        var serverId = _promptServerId;

        _promptServerId = null;
        IsAutoConnectPromptOpen = false;

        if (serverId is null)
        {
            return;
        }

        if (policy != AutoConnectPolicy.Never)
        {
            Settings.AutoConnect = policy;
        }

        _settings.Update(s => s.AutoConnectPromptedServerId = serverId);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        _progressClock.Tick -= OnProgressTick;
        _progressClock.Dispose();

        _player.ConnectionChanged -= OnConnectionChanged;
        _player.StateChanged -= OnStateChanged;
        _player.DiscoveredServersChanged -= OnDiscoveredServersChanged;
        _player.PaletteChanged -= OnPaletteChanged;
        _player.VisualizerFrameReceived -= OnVisualizerFrameReceived;
        _mediaSession.IntentReceived -= OnMediaSessionIntent;
        _router.LocalActionRequested -= OnLocalActionRequested;
        Backdrop.PropertyChanged -= OnBackdropPropertyChanged;
        DiscoveredServers.CollectionChanged -= OnDiscoveredServersCollectionChanged;

        // Cancel and drain outstanding work before releasing what it touches.
        //
        // ConfigureAwait(false) is load-bearing, not habit. Shutdown runs on the UI thread and
        // blocks waiting for the service container to dispose; a continuation captured onto the
        // Avalonia synchronisation context here would be queued behind that block and never run, so
        // this method would never finish, the container would never get past it, and the audio
        // device, the media session and the artwork cache would all be left as they are.
        await _work.DisposeAsync().ConfigureAwait(false);

        Diagnostics.Dispose();
        Backdrop.Dispose();

        // Same ordering as above: ShutdownRequested fires before windows close, so the images are
        // still bound at this point.
        ClearArtwork();
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

            await Dispatcher.UIThread.InvokeAsync(ReopenStatsIfLeftOpen);

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
        Dispatcher.UIThread.Post(() => ApplyConnection(e));

        _work.Run(
            "connection notification",
            token => _notifications.OnConnectionAsync(e.ServerName ?? "Sendspin server", e.IsConnected, token));
    }

    /// <summary>
    /// Applies a connection change on the UI thread: what <see cref="OnConnectionChanged"/>
    /// posts, and what the UI tests call directly.
    /// </summary>
    internal void ApplyConnection(ConnectionChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_isDisposed)
        {
            // Posted before disposal, run after it: the clock is gone, and so is the reason.
            return;
        }

        IsConnected = e.IsConnected;
        IsConnecting = false;
        ServerName = e.ServerName;

        if (e.IsConnected)
        {
            _progressClock.Start();
            OfferAutoConnectPrompt();
        }
        else
        {
            _progressClock.Stop();
            Position = TimeSpan.Zero;
            _anchoredPosition.Anchor(TimeSpan.Zero, _progressClock.Elapsed);
            State = MediaSessionState.Idle;
            IsAutoConnectPromptOpen = false;
            _promptServerId = null;
            _artworkPath = null;
            ClearArtwork();
            Backdrop.Reset();
        }

        UpdateBackdropLayers();
    }

    private void OnStateChanged(object? sender, MediaSessionState state)
    {
        Dispatcher.UIThread.Post(() => ApplyState(state));

        _mediaSession.Publish(state);
        _presence.Publish(state, _player.ServerName);
        _work.Run("state notification", token => _notifications.OnStateAsync(state, token));
    }

    /// <summary>
    /// Applies a state report on the UI thread: what <see cref="OnStateChanged"/> posts, and
    /// what the UI tests call directly.
    /// </summary>
    internal void ApplyState(MediaSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (_isDisposed)
        {
            return;
        }

        State = state;
        Position = state.Position;
        _anchoredPosition.Anchor(state.Position, _progressClock.Elapsed);
        Backdrop.SetPlaying(IsPlaying);

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
    }

    private void OnDiscoveredServersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(IsSearching));

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
        var now = _progressClock.Elapsed;

        if (!IsPlaying)
        {
            // Hold the anchor at the paused position, so a resume without a fresh report
            // continues from here rather than crediting the time spent paused.
            _anchoredPosition.Anchor(Position, now);
            return;
        }

        Position = _anchoredPosition.At(now);
    }

    /// <summary>
    /// Loads artwork from the cache file the player service wrote, and scales it down for the
    /// blurred backdrop. Both are produced once per artwork change, not per state report.
    /// </summary>
    private void LoadArtwork(string? path)
    {
        if (path == _artworkPath)
        {
            return;
        }

        _artworkPath = path;

        if (path is null || !File.Exists(path))
        {
            ClearArtwork();
            return;
        }

        Bitmap? artwork = null;
        Bitmap? backdrop = null;

        try
        {
            artwork = new Bitmap(path);

            // A decoded Bitmap, which is what CreateScaledBitmap accepts; it throws for a
            // WriteableBitmap, and the spike measured why the source has to be this small.
            backdrop = artwork.CreateScaledBitmap(BackdropSize);
        }
        catch (ArgumentException ex)
        {
            // Bitmap throws this for data it cannot decode. Artwork is decoration; a picture the
            // renderer refuses must not stop playback.
            _logger.LogWarning(ex, "Artwork at {Path} could not be decoded", path);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Artwork at {Path} could not be read", path);
        }

        var previous = Artwork;
        var previousBackdrop = ArtBackdrop;

        Artwork = artwork;
        ArtBackdrop = backdrop;
        UpdateBackdropLayers();

        previous?.Dispose();
        previousBackdrop?.Dispose();
    }

    /// <summary>
    /// Unbinds both images, then disposes them.
    /// </summary>
    /// <remarks>
    /// In that order. The compositor renders from <c>Image.Source</c> on its own thread, so
    /// releasing a bitmap while it is still the bound source is a native fault on the render
    /// thread rather than a managed exception here.
    /// </remarks>
    private void ClearArtwork()
    {
        var artwork = Artwork;
        var backdrop = ArtBackdrop;

        Artwork = null;
        ArtBackdrop = null;
        UpdateBackdropLayers();

        artwork?.Dispose();
        backdrop?.Dispose();
    }

    /// <summary>
    /// Which backdrop layer shows, from the three facts that decide it: connected, a scaled
    /// bitmap, and the glow being active. The glow wins when both could show.
    /// </summary>
    private void UpdateBackdropLayers()
    {
        HasAmbientBackdrop = IsConnected && Backdrop.IsActive;
        HasArtBackdrop = IsConnected && ArtBackdrop is not null && !HasAmbientBackdrop;
    }

    private void OnBackdropPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AmbientBackdropViewModel.IsActive))
        {
            UpdateBackdropLayers();
        }
    }

    // Both arrive on the SDK's threads; the backdrop view model marshals them itself.
    private void OnPaletteChanged(object? sender, SDK.Models.ColorPalette palette) => Backdrop.ReceivePalette(palette);

    private void OnVisualizerFrameReceived(object? sender, SDK.Models.VisualizerFrame frame) => Backdrop.ReceiveFrame(frame);

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
