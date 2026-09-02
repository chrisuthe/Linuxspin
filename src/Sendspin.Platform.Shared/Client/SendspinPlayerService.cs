using Microsoft.Extensions.Logging;
using Sendspin.Core.Audio;
using Sendspin.Core.Configuration;
using Sendspin.Core.Control;
using Sendspin.Core.Diagnostics;
using Sendspin.Core.MediaSession;
using Sendspin.Platform.Shared.Audio;
using Sendspin.Platform.Shared.Media;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;

namespace Sendspin.Platform.Shared.Client;

/// <summary>
/// Owns the Sendspin session: discovery, the connection in whichever mode the user chose, the
/// audio pipeline, and the state everything else observes.
/// </summary>
/// <remarks>
/// <para>
/// Discovery and connection go entirely through the SDK — <see cref="MdnsServerDiscovery"/>,
/// <see cref="SendspinHostService"/> and <see cref="SendspinClientService"/>. Nothing here speaks
/// the protocol directly: connecting to N discovered servers opens N sockets, and the roles
/// advertised are whatever <see cref="PlayerCapabilities"/> declares.
/// </para>
/// <para>
/// It is also the <see cref="IPlayerCommandSink"/>, which is what makes the single command path
/// real: a media key from SMTC, MPRIS or Control Center, and a click in the window, both reach
/// the server through the same method here.
/// </para>
/// </remarks>
public sealed class SendspinPlayerService : IPlayerCommandSink, IDiagnosticsProvider, IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SendspinPlayerService> _logger;
    private readonly SettingsService _settings;
    private readonly IStaticDelayStore _staticDelayStore;
    private readonly IAudioDeviceEnumerator _deviceEnumerator;
    private readonly Func<IAudioPlayer> _playerFactory;
    private readonly ArtworkCache _artworkCache;
    private readonly SyncCorrectionPolicy _syncPolicy;
    private readonly string _softwareVersion;
    private readonly BackgroundTaskSet _background;
    private readonly Lock _sessionGate = new();

    /// <summary>
    /// The channel whose picture is published as the track's artwork. <see cref="PlayerCapabilities"/>
    /// advertises exactly one channel, the album one, so this is 0; the schedulers are still kept per
    /// channel so a second channel (artist art) cannot clear or replace the album's picture.
    /// </summary>
    private const int AlbumArtworkChannel = 0;

    /// <summary>
    /// Scheduled artwork by channel, and scheduled track metadata. The spec's pending model
    /// (<see cref="ScheduledValue{T}"/>): what is shown is the current value, and the next track's
    /// picture and metadata wait here until their timestamps, converted to this machine's clock.
    /// Guarded by <see cref="_sessionGate"/>.
    /// </summary>
    private readonly Dictionary<int, ScheduledValue<string>> _artwork = [];
    private readonly ScheduledValue<TrackMetadata> _metadata = new();

    /// <summary>
    /// Fires <see cref="PromoteDue"/> when the earliest pending value falls due. A thread-pool timer
    /// rather than a UI one: promotion is session state, and the view model marshals what it
    /// publishes as it does for every other state change.
    /// </summary>
    private readonly Timer _promotion;

    /// <summary>
    /// The metadata instance last offered to the scheduler. The SDK builds a new
    /// <see cref="TrackMetadata"/> for every <c>server/state</c> that carries a metadata object and
    /// leaves the instance alone for one that does not, so reference identity is what tells a
    /// metadata update apart from a volume change on the same group.
    /// </summary>
    private TrackMetadata? _offeredMetadata;
    private bool _loggedHeldArtwork;
    private bool _loggedHeldMetadata;

    private MdnsServerDiscovery? _discovery;
    private SendspinHostService? _host;
    private SendspinClientService? _client;
    private ISendspinConnection? _connection;
    private IClockSynchronizer? _clockSync;
    private IAudioPipeline? _pipeline;
    private SyncCorrectedSampleSource? _sampleSource;
    private AudioPlayerBase? _activePlayer;

    /// <summary>
    /// Makes the transport a dialled session runs over. The real one opens a WebSocket; the tests
    /// substitute a recording connection so the client path can be driven end to end without a
    /// server, which is the only way to prove what this service forwards from it.
    /// </summary>
    internal Func<ISendspinConnection> ConnectionFactory { get; set; }

    /// <summary>
    /// Makes the clock synchroniser the session converts server timestamps with. The real one is
    /// the SDK's Kalman filter; the tests substitute one with a known offset so that "held until its
    /// timestamp" can be asserted against a clock they control.
    /// </summary>
    internal Func<IClockSynchronizer> ClockSynchronizerFactory { get; set; }

    /// <summary>
    /// Reads this machine's clock in microseconds, in the domain
    /// <see cref="IClockSynchronizer.ServerToClientTime"/> converts into: the SDK's
    /// <see cref="HighPrecisionTimer.Shared"/>, which also stamps its <c>client/time</c> probes and
    /// schedules its audio. The tests substitute a clock they advance by hand.
    /// </summary>
    internal Func<long> LocalClock { get; set; } = HighPrecisionTimer.Shared.GetCurrentTimeMicroseconds;

    private string? _adoptedServerId;
    private GroupState? _group;
    private MediaSessionState _mediaState = MediaSessionState.Idle;
    private string? _serverName;
    private bool _isDisposed;

    public SendspinPlayerService(
        ILoggerFactory loggerFactory,
        SettingsService settings,
        IStaticDelayStore staticDelayStore,
        IAudioDeviceEnumerator deviceEnumerator,
        Func<IAudioPlayer> playerFactory,
        ArtworkCache artworkCache,
        SyncCorrectionPolicy syncPolicy,
        string softwareVersion)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(staticDelayStore);
        ArgumentNullException.ThrowIfNull(deviceEnumerator);
        ArgumentNullException.ThrowIfNull(playerFactory);
        ArgumentNullException.ThrowIfNull(artworkCache);
        ArgumentNullException.ThrowIfNull(syncPolicy);
        ArgumentException.ThrowIfNullOrEmpty(softwareVersion);

        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SendspinPlayerService>();
        _settings = settings;
        _staticDelayStore = staticDelayStore;
        _deviceEnumerator = deviceEnumerator;
        _playerFactory = playerFactory;
        _artworkCache = artworkCache;
        _syncPolicy = syncPolicy;
        _softwareVersion = softwareVersion;
        _background = new BackgroundTaskSet(_logger);
        _promotion = new Timer(_ => OnPromotionDue(), state: null, Timeout.Infinite, Timeout.Infinite);
        ConnectionFactory = () => new SendspinConnection(
            _loggerFactory.CreateLogger<SendspinConnection>(),
            new ConnectionOptions());
        ClockSynchronizerFactory = () =>
            new KalmanClockSynchronizer(_loggerFactory.CreateLogger<KalmanClockSynchronizer>());
    }

    /// <summary>Raised when the connection comes up or goes down.</summary>
    public event EventHandler<ConnectionChangedEventArgs>? ConnectionChanged;

    /// <summary>Raised whenever the server's group state changes.</summary>
    public event EventHandler<MediaSessionState>? StateChanged;

    /// <summary>Raised when a server appears in or disappears from discovery.</summary>
    public event EventHandler? DiscoveredServersChanged;

    /// <summary>
    /// Raised with the group's colour palette whenever the server sends one (the <c>color@v1</c>
    /// role), from whichever connection path is carrying the session.
    /// </summary>
    /// <remarks>
    /// Forwarded as the SDK raises it: on a background thread, and on every <c>server/state</c>
    /// that carries a colour object, including ones that change nothing. The view model marshals
    /// and deduplicates, as it does for state.
    /// </remarks>
    public event EventHandler<ColorPalette>? PaletteChanged;

    /// <summary>
    /// Raised for each visualizer feature frame (the <c>visualizer@v1</c> role), loudness or beat
    /// as advertised, from whichever connection path is carrying the session. Background thread.
    /// </summary>
    public event EventHandler<VisualizerFrame>? VisualizerFrameReceived;

    /// <inheritdoc/>
    public bool CanSend => _client is { ConnectionState: ConnectionState.Connected }
                           || _host is { ConnectedServers.Count: > 0 };

    /// <inheritdoc/>
    public MediaSessionState CurrentState
    {
        get { lock (_sessionGate) return _mediaState; }
    }

    /// <summary>Gets the connected server's name, or null.</summary>
    public string? ServerName
    {
        get { lock (_sessionGate) return _serverName; }
    }

    /// <summary>
    /// Gets the servers currently visible via mDNS.
    /// </summary>
    public IReadOnlyList<DiscoveredServer> DiscoveredServers =>
        _discovery?.Servers.ToList() ?? [];

    /// <summary>
    /// Starts discovery and advertising as the configured
    /// <see cref="PlayerSettings.ConnectionMode"/> requires, then honours the auto-connect
    /// policy.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var settings = _settings.Current;
        var mode = settings.ConnectionMode;

        _logger.LogInformation("Starting Sendspin player {ClientId} ({PlayerName}) in {Mode} mode",
            settings.ClientId, settings.PlayerName, mode);

        // Exactly one of the two, never both: connection.md allows a client one connection
        // method at a time, which is why the SDK retires ConnectionMode.Auto in 10.0.0.
        if (mode == ConnectionMode.DiscoverOnly)
        {
            await StartDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await StartAdvertisingAsync(cancellationToken).ConfigureAwait(false);
        }

        if (settings.AutoConnect != AutoConnectPolicy.Never && settings.LastServerId is { } serverId)
        {
            // "Just once" is consumed on use, so an auto-connect the user asked for exactly
            // once does not silently become permanent.
            if (settings.AutoConnect == AutoConnectPolicy.JustOnce)
            {
                _settings.Update(s => s.AutoConnect = AutoConnectPolicy.Never);
            }

            _background.Run(
                $"auto-connect to {serverId}",
                token => AutoConnectAsync(serverId, token));
        }
    }

    /// <summary>
    /// Connects to a discovered server.
    /// </summary>
    public Task ConnectAsync(DiscoveredServer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        return ConnectAsync(server.GetWebSocketUri(), server.ServerId, server.Name, cancellationToken);
    }

    /// <summary>
    /// Connects to a server by URL, for the manual-connect path.
    /// </summary>
    /// <exception cref="UriFormatException">Thrown when <paramref name="url"/> is not a URI.</exception>
    public Task ConnectAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var normalised = url.Contains("://", StringComparison.Ordinal) ? url : $"ws://{url}";
        var uri = new Uri(normalised);

        return ConnectAsync(uri, serverId: null, serverName: uri.Host, cancellationToken);
    }

    /// <summary>
    /// Disconnects and tears down the pipeline, leaving discovery and advertising running.
    /// </summary>
    public async Task DisconnectAsync()
    {
        SendspinClientService? client;
        IAudioPipeline? pipeline;

        // Stop arbitrating for this session before the socket goes: an adoption left behind
        // would keep refusing every server that dials in, with no session left to protect.
        ReleaseAdoption();

        lock (_sessionGate)
        {
            client = _client;
            pipeline = _pipeline;
            _client = null;
            _connection = null;
            _group = null;
            _mediaState = MediaSessionState.Idle;
            _serverName = null;
            ResetSchedules();
        }

        if (client is not null)
        {
            client.ConnectionStateChanged -= OnConnectionStateChanged;
            client.GroupStateChanged -= OnGroupStateChanged;
            client.PlayerStateChanged -= OnPlayerStateChanged;
            client.ArtworkReceived -= OnArtworkReceived;
            client.ArtworkCleared -= OnArtworkCleared;
            client.ColorChanged -= OnColorChanged;
            client.VisualizationReceived -= OnVisualizationReceived;

            try
            {
                await client.DisconnectAsync("user requested").ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Socket already closed while disconnecting");
            }
            catch (System.Net.WebSockets.WebSocketException ex)
            {
                _logger.LogDebug(ex, "WebSocket already closed while disconnecting");
            }

            await client.DisposeAsync().ConfigureAwait(false);
        }

        // Stop the pipeline rather than dispose it. It is shared with the host service, which may
        // still be advertising and have a server connect a moment later; disposing here would
        // leave that service holding a dead pipeline.
        if (pipeline is not null)
        {
            await pipeline.StopAsync().ConfigureAwait(false);
        }

        RaiseConnectionChanged(connected: false, serverName: null);
        PublishState();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The token is observed before the send, not during it: the SDK's command methods take no
    /// cancellation token, so there is nothing to hand it to. Checking here is what lets
    /// <see cref="BackgroundTaskSet"/>'s shutdown actually stop queued commands rather than waiting
    /// out its timeout on every one of them.
    /// </remarks>
    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        cancellationToken.ThrowIfCancellationRequested();

        if (_client is { ConnectionState: ConnectionState.Connected } client)
        {
            await client.SendCommandAsync(command, parameters: null).ConfigureAwait(false);
            return;
        }

        if (_host is { ConnectedServers.Count: > 0 } host)
        {
            await host.SendCommandAsync(command, parameters: null, serverId: null).ConfigureAwait(false);
            return;
        }

        _logger.LogDebug("Dropping command {Command}: no server connected", command);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Volume is server-authoritative. This asks the server to change it; the value that
    /// actually takes effect arrives back as group state, and only then is it applied to the
    /// output and persisted. Applying it locally first would show the user a volume the group
    /// does not have.
    /// </remarks>
    public async Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clamped = Math.Clamp(volume, 0, 100);

        if (_client is { ConnectionState: ConnectionState.Connected } client)
        {
            await client.SetVolumeAsync(clamped).ConfigureAwait(false);
            return;
        }

        // Not connected: still remember the choice, and apply it to the output so the control
        // is not inert.
        _settings.Update(s => s.Volume = clamped);
        _pipeline?.SetVolume(clamped);
    }

    /// <inheritdoc/>
    public async Task SetMuteAsync(bool muted, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_client is { ConnectionState: ConnectionState.Connected } client)
        {
            await client.SetMuteAsync(muted).ConfigureAwait(false);
            return;
        }

        _settings.Update(s => s.Muted = muted);
        _pipeline?.SetMuted(muted);
    }

    /// <summary>
    /// Applies a new static delay: persists it, tells the clock synchroniser, and re-anchors
    /// timing so the change takes effect without dumping the buffer.
    /// </summary>
    public async Task SetStaticDelayAsync(double staticDelayMs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _staticDelayStore.Save(staticDelayMs);

        if (_clockSync is not null)
        {
            _clockSync.StaticDelayMs = staticDelayMs;
        }

        // Re-anchor rather than clear: the buffered audio is still good, it just needs to be
        // scheduled against the new delay. Clearing would stall until the buffer refilled,
        // which with a server that transmits far ahead can be a long silence.
        _pipeline?.ReanchorTiming();

        if (_client is { ConnectionState: ConnectionState.Connected } client)
        {
            var settings = _settings.Current;
            await client.SendPlayerStateAsync(settings.Volume, settings.Muted, staticDelayMs).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Switches the audio output device without dropping the server connection.
    /// </summary>
    public async Task SwitchAudioDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        _settings.Update(s => s.AudioDeviceId = deviceId);

        if (_activePlayer is { } player)
        {
            player.ManualLatencyOffsetMs = _settings.Current.GetManualLatencyOffsetMs(deviceId);
        }

        if (_pipeline is { } pipeline)
        {
            await pipeline.SwitchDeviceAsync(deviceId ?? string.Empty, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Audio output switched to {DeviceId}", deviceId ?? "system default");
        }
    }

    /// <summary>
    /// Applies a manual latency offset to the current device, taking effect immediately.
    /// </summary>
    public void SetManualLatencyOffset(string deviceId, double offsetMs)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);

        _settings.Update(s => s.SetManualLatencyOffsetMs(deviceId, offsetMs));

        if (_activePlayer is { } player && player.CurrentDeviceId == deviceId)
        {
            player.ManualLatencyOffsetMs = offsetMs;
        }
    }

    /// <inheritdoc/>
    public PlayerDiagnosticsSnapshot Capture()
    {
        var settings = _settings.Current;
        var activePlayer = Volatile.Read(ref _activePlayer);
        var sampleSource = Volatile.Read(ref _sampleSource);
        var stats = _pipeline?.BufferStats;
        var buffer = sampleSource?.Buffer;
        var provider = sampleSource?.CorrectionProvider;
        var clockStatus = _clockSync?.GetStatus();
        var format = _pipeline?.CurrentFormat;

        return new PlayerDiagnosticsSnapshot
        {
            IsConnected = CanSend,
            ServerName = ServerName,
            Codec = format?.Codec,
            SampleRate = format?.SampleRate ?? 0,
            Channels = format?.Channels ?? 0,
            BitDepth = format?.BitDepth,
            SyncErrorMicroseconds = buffer?.SyncErrorMicroseconds ?? 0,
            SmoothedSyncErrorMicroseconds = buffer?.SmoothedSyncErrorMicroseconds ?? 0,
            CorrectionMode = provider?.CurrentMode.ToString(),
            PlaybackRate = provider?.TargetPlaybackRate ?? 1.0,
            BufferedMilliseconds = buffer?.BufferedMilliseconds ?? 0,
            ClockOffsetMilliseconds = clockStatus?.OffsetMilliseconds ?? 0,
            ClockDriftMicrosecondsPerSecond = clockStatus?.DriftMicrosecondsPerSecond ?? 0,
            ClockOffsetUncertaintyMicroseconds = clockStatus?.OffsetUncertaintyMicroseconds ?? 0,
            ClockConverged = clockStatus?.IsConverged ?? false,
            RoundTripMicroseconds = clockStatus?.AvgRttMicroseconds ?? 0,
            OutputLatencyMs = activePlayer?.OutputLatencyMs ?? _pipeline?.DetectedOutputLatencyMs ?? 0,
            ManualLatencyOffsetMs = settings.GetManualLatencyOffsetMs(settings.AudioDeviceId),
            StaticDelayMs = settings.StaticDelayMs,
            ClockDriftMs = stats?.ClockDriftMs ?? 0,
            TimingSource = stats?.TimingSourceName ?? buffer?.TimingSourceName,
            AudioDeviceName = Volatile.Read(ref _activeDeviceName),
            PlatformName = ClientIdentity.PlatformLabel
        };
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        // Cancel tracked work before tearing down what it touches.
        await _background.DisposeAsync().ConfigureAwait(false);
        await DisconnectAsync().ConfigureAwait(false);

        if (_host is { } host)
        {
            // Already released by DisconnectAsync above in every normal path; this is the
            // backstop for a session adopted after that ran.
            ReleaseAdoption();

            _host = null;
            host.ServerConnected -= OnHostServerConnected;
            host.ServerDisconnected -= OnHostServerDisconnected;
            host.GroupStateChanged -= OnGroupStateChanged;
            host.ArtworkReceived -= OnArtworkReceived;
            host.ArtworkCleared -= OnArtworkCleared;
            host.ColorChanged -= OnColorChanged;
            host.VisualizationReceived -= OnVisualizationReceived;
            await host.DisposeAsync().ConfigureAwait(false);
        }

        if (_discovery is { } discovery)
        {
            _discovery = null;
            discovery.ServerFound -= OnServerFound;
            discovery.ServerLost -= OnServerLost;
            discovery.ServerUpdated -= OnServerUpdated;
            await discovery.DisposeAsync().ConfigureAwait(false);
        }

        // The pipeline is shared, so this is the only place that disposes it — after both the
        // client and the host service are gone, so neither can still be handing it audio.
        IAudioPipeline? pipeline;
        SyncCorrectedSampleSource? source;

        lock (_sessionGate)
        {
            pipeline = _pipeline;
            source = _sampleSource;
            _pipeline = null;
            _sampleSource = null;
            _clockSync = null;
            _activePlayer = null;

            // Under the gate so a promotion or re-arm racing the shutdown cannot touch a disposed
            // timer.
            _promotion.Dispose();
        }

        if (pipeline is not null)
        {
            await pipeline.DisposeAsync().ConfigureAwait(false);
        }

        source?.Dispose();

        _artworkCache.Clear();
    }

    private async Task StartDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (_discovery is not null)
        {
            return;
        }

        var discovery = new MdnsServerDiscovery(_loggerFactory.CreateLogger<MdnsServerDiscovery>());
        discovery.ServerFound += OnServerFound;
        discovery.ServerLost += OnServerLost;
        discovery.ServerUpdated += OnServerUpdated;
        _discovery = discovery;

        await discovery.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Discovering Sendspin servers");
    }

    private async Task StartAdvertisingAsync(CancellationToken cancellationToken)
    {
        if (_host is not null)
        {
            return;
        }

        var settings = _settings.Current;
        var device = ResolveDevice(settings.AudioDeviceId);
        var capabilities = BuildCapabilities(settings, device);
        var (clockSync, pipeline) = EnsureAudioSession(device);

        var host = new SendspinHostService(
            _loggerFactory,
            capabilities,
            new ListenerOptions(),
            new AdvertiserOptions
            {
                ClientId = settings.ClientId,
                PlayerName = settings.PlayerName
            },
            pipeline,
            clockSync,
            settings.LastServerId);

        host.ServerConnected += OnHostServerConnected;
        host.ServerDisconnected += OnHostServerDisconnected;
        host.GroupStateChanged += OnGroupStateChanged;
        host.ArtworkReceived += OnArtworkReceived;
        host.ArtworkCleared += OnArtworkCleared;
        host.ColorChanged += OnColorChanged;
        host.VisualizationReceived += OnVisualizationReceived;

        lock (_sessionGate)
        {
            _host = host;
        }

        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Advertising as a Sendspin player for servers to connect to");
    }

    private async Task ConnectAsync(
        Uri uri,
        string? serverId,
        string? serverName,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Tear down *any* existing client, not only a connected one. A client that failed to
        // connect, or that dropped, is still subscribed to these seven events and still owns a
        // socket; overwriting the field would leak it and — if its own reconnect later succeeded —
        // leave two clients driving one UI and one settings file.
        if (_client is not null)
        {
            _logger.LogInformation("Replacing the existing client connection");
            await DisconnectAsync().ConfigureAwait(false);
        }

        var settings = _settings.Current;
        var device = ResolveDevice(settings.AudioDeviceId);
        var capabilities = BuildCapabilities(settings, device);
        var (clockSync, pipeline) = EnsureAudioSession(device);

        var connection = ConnectionFactory();

        var client = new SendspinClientService(
            _loggerFactory.CreateLogger<SendspinClientService>(),
            connection,
            clockSync,
            capabilities,
            pipeline,
            _staticDelayStore);

        client.ConnectionStateChanged += OnConnectionStateChanged;
        client.GroupStateChanged += OnGroupStateChanged;
        client.PlayerStateChanged += OnPlayerStateChanged;
        client.ArtworkReceived += OnArtworkReceived;
        client.ArtworkCleared += OnArtworkCleared;
        client.ColorChanged += OnColorChanged;
        client.VisualizationReceived += OnVisualizationReceived;

        lock (_sessionGate)
        {
            _client = client;
            _connection = connection;
        }

        _logger.LogInformation("Connecting to {Uri}", uri);
        await client.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

        var resolvedName = client.ServerName ?? serverName ?? uri.Host;
        var resolvedId = client.ServerId ?? serverId;

        lock (_sessionGate)
        {
            _serverName = resolvedName;
        }

        if (resolvedId is not null)
        {
            _settings.Update(s => s.LastServerId = resolvedId);
        }

        AdoptIntoHost(client, resolvedId);

        _logger.LogInformation("Connected to {ServerName}", resolvedName);
        RaiseConnectionChanged(connected: true, resolvedName);
    }

    /// <summary>
    /// Registers a session this player dialled with the host service, so a server connecting in
    /// is arbitrated against it rather than against nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dropping <c>ConnectionMode.Auto</c> does not make this unnecessary. Advertising is the
    /// default mode, and both <see cref="ConnectAsync(string, CancellationToken)"/> and the
    /// auto-connect policy dial out regardless of it — so a host and a client still coexist,
    /// which is exactly the shape that let an incoming server be accepted and announced while a
    /// dialled session was playing, resetting the shared clock synchroniser and pipeline under it
    /// (SDK #253).
    /// </para>
    /// <para>
    /// Ownership does not move: the SDK never disconnects or disposes an adopted client, so
    /// <see cref="DisconnectAsync"/> tears the session down exactly as it did before.
    /// </para>
    /// <para>
    /// Arbitration is keyed by server id, and the manual-connect path starts with none. The id is
    /// therefore taken after the connect completes, from the server's own hello where possible.
    /// A session with no id from either source is left unadopted and logged rather than adopted
    /// under null: the SDK matches the release against the id it was given, so a placeholder would
    /// be a lock nothing could open.
    /// </para>
    /// </remarks>
    private void AdoptIntoHost(SendspinClientService client, string? resolvedId)
    {
        SendspinHostService? host;

        lock (_sessionGate)
        {
            host = _host;
        }

        if (host is null)
        {
            return;
        }

        if (resolvedId is null)
        {
            _logger.LogWarning(
                "Connected without a server id; the session is not adopted, so an incoming server "
                + "will not be arbitrated against it");
            return;
        }

        host.AdoptClientInitiated(client, resolvedId);

        lock (_sessionGate)
        {
            _adoptedServerId = resolvedId;
        }

        _logger.LogDebug("Adopted client-initiated session with {ServerId} for arbitration", resolvedId);
    }

    /// <summary>
    /// Stops arbitrating on behalf of the adopted session, if there is one.
    /// </summary>
    /// <remarks>
    /// Released under the same id it was adopted with — the SDK matches on it and ignores anything
    /// else. Idempotent, because both <see cref="DisconnectAsync"/> and
    /// <see cref="DisposeAsync"/> call it and a dropped session may already have released itself.
    /// </remarks>
    private void ReleaseAdoption()
    {
        SendspinHostService? host;
        string? serverId;

        lock (_sessionGate)
        {
            host = _host;
            serverId = _adoptedServerId;
            _adoptedServerId = null;
        }

        if (host is null || serverId is null)
        {
            return;
        }

        host.ReleaseClientInitiated(serverId);
        _logger.LogDebug("Released the adopted session with {ServerId}", serverId);
    }

    private async Task AutoConnectAsync(string serverId, CancellationToken cancellationToken)
    {
        // Give discovery a moment to populate before deciding the server is not there. mDNS is
        // not instant and failing immediately would make auto-connect useless on a cold start.
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

        var match = DiscoveredServers.FirstOrDefault(s => s.ServerId == serverId);
        if (match is not null)
        {
            await ConnectAsync(match, cancellationToken).ConfigureAwait(false);
            return;
        }

        var manualUrl = _settings.Current.ManualServerUrl;
        if (!string.IsNullOrWhiteSpace(manualUrl))
        {
            _logger.LogInformation("Auto-connect target {ServerId} not discovered; trying {Url}", serverId, manualUrl);
            await ConnectAsync(manualUrl, cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Auto-connect target {ServerId} not found on the network", serverId);
    }

    /// <summary>
    /// Returns the one clock synchroniser and audio pipeline for this process, creating them on
    /// first use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is exactly one of each, shared by the host service and the client service, because
    /// there is exactly one output device. The two services coexist even now that only one
    /// connection method runs: <see cref="ConnectAsync(string, CancellationToken)"/> and the
    /// auto-connect policy both dial out while the host is advertising. Giving each its own
    /// pipeline would put two audio players on the same device, and tearing one down would leave
    /// the other holding a disposed pipeline.
    /// </para>
    /// <para>
    /// Sharing is safe because only one of them can be streaming: a player belongs to one group at
    /// a time, and the pipeline is stopped between streams. The synchroniser is shared for the same
    /// reason, and its <see cref="IClockSynchronizer.StaticDelayMs"/> is a property of this machine
    /// rather than of a particular connection.
    /// </para>
    /// </remarks>
    private (IClockSynchronizer ClockSync, IAudioPipeline Pipeline) EnsureAudioSession(AudioDeviceInfo? device)
    {
        lock (_sessionGate)
        {
            if (_clockSync is { } existingClock && _pipeline is { } existingPipeline)
            {
                return (existingClock, existingPipeline);
            }

            var clockSync = ClockSynchronizerFactory();
            clockSync.StaticDelayMs = _settings.Current.StaticDelayMs;

            var pipeline = CreatePipeline(clockSync, device);

            _clockSync = clockSync;
            _pipeline = pipeline;

            return (clockSync, pipeline);
        }
    }

    /// <summary>
    /// Builds the decoded buffer a stream plays out of, configured with the buffering this
    /// installation advertises.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No capacity argument: the decoded buffer takes the SDK's 30 s default, which is the same
    /// figure <c>ClientCapabilities</c> derives the advertised <c>buffer_capacity</c> from. Passing
    /// a different one here is how the two sides come to disagree.
    /// </para>
    /// <para>
    /// Lifted out of <see cref="CreatePipeline"/> and made reachable because
    /// <see cref="PlayerCapabilities.DefaultMinBufferMs"/> is one promise with two uses — it is
    /// advertised as <c>min_buffer_ms</c> and it is what the buffer is asked to hold — and drift
    /// between them is invisible on both sides. A test can build the buffer the pipeline gets and
    /// compare it against the figure that actually went out on the wire.
    /// </para>
    /// </remarks>
    public static TimedAudioBuffer CreateDecodedBuffer(
        AudioFormat format,
        IClockSynchronizer clockSync,
        SyncCorrectionOptions syncOptions,
        ILogger<TimedAudioBuffer>? logger = null) =>
        new(format, clockSync, syncOptions: syncOptions, logger: logger)
        {
            TargetBufferMilliseconds = PlayerCapabilities.DefaultMinBufferMs
        };

    /// <summary>
    /// Builds the audio pipeline, wiring our correction policy and latency figures into it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>waitForConvergence: true</c> is passed because the spec requires availability to be
    /// withheld until the time filter has converged. Note that SDK 9.3.2 still does not fully
    /// honour it: on timeout it logs "Starting playback without full convergence" and proceeds.
    /// 9.3.0 reworked the filter's probe cadence — a link that stays noisy now falls back to the
    /// steady-state interval and withholds <c>IsClockSynced</c> — but the timeout path itself is
    /// unchanged. That gap is upstream and is recorded in <c>docs/COMPLIANCE.md</c> rather than
    /// papered over here with an inflated timeout, which would only hide it.
    /// </para>
    /// <para>
    /// <c>useMonotonicTimer: true</c> so that a wall-clock jump — a VM host resuming, an NTP
    /// step — cannot be read as an enormous sync error.
    /// </para>
    /// </remarks>
    private IAudioPipeline CreatePipeline(IClockSynchronizer clockSync, AudioDeviceInfo? device)
    {
        var syncOptions = _syncPolicy.ToSdkOptions();
        var manualOffsetMs = _settings.Current.GetManualLatencyOffsetMs(device?.Id);

        return new AudioPipeline(
            _loggerFactory.CreateLogger<AudioPipeline>(),
            new AudioDecoderFactory(_loggerFactory),
            clockSync,
            bufferFactory: (format, sync) => CreateDecodedBuffer(
                format,
                sync,
                syncOptions,
                _loggerFactory.CreateLogger<TimedAudioBuffer>()),
            playerFactory: () =>
            {
                var player = _playerFactory();

                if (player is AudioPlayerBase platformPlayer)
                {
                    platformPlayer.ManualLatencyOffsetMs = manualOffsetMs;

                    // The SDK invokes this factory on its own thread while the diagnostics view
                    // reads the field from the UI thread, so the reference is published rather
                    // than merely assigned.
                    Volatile.Write(ref _activePlayer, platformPlayer);
                }

                return player;
            },
            sourceFactory: (buffer, timeFunc) =>
            {
                var calculator = new SyncCorrectionCalculator(
                    syncOptions,
                    buffer.Format.SampleRate,
                    buffer.Format.Channels);

                var source = new SyncCorrectedSampleSource(
                    buffer,
                    calculator,
                    timeFunc,
                    _loggerFactory.CreateLogger<SyncCorrectedSampleSource>());

                // A new source per stream, so the previous one has to go: each holds a scratch
                // buffer and a correction calculator.
                var previous = Interlocked.Exchange(ref _sampleSource, source);
                previous?.Dispose();

                return source;
            },
            precisionTimer: null,
            waitForConvergence: true,
            convergenceTimeoutMs: 5_000,
            useMonotonicTimer: true);
    }

    /// <summary>
    /// The active device's display name, remembered so that <see cref="Capture"/> does not have to
    /// enumerate devices.
    /// </summary>
    /// <remarks>
    /// Diagnostics polls twice a second on the UI thread. Enumerating there means constructing an
    /// <c>MMDeviceEnumerator</c> and walking every endpoint on Windows, or re-reading the device
    /// string list on Linux — tens of milliseconds of work on the thread that renders the UI, for a
    /// value that changes only when the user picks a different output.
    /// </remarks>
    private string? _activeDeviceName;

    /// <summary>
    /// Builds the capabilities to advertise and records the codec order they carry.
    /// </summary>
    /// <remarks>
    /// The order is logged because it was previously invisible: a <c>preferred_codec</c> that had
    /// drifted in settings looked, from the outside, exactly like the server choosing the codec,
    /// and nothing on this side said otherwise. One line at connect turns that into a glance.
    /// </remarks>
    private ClientCapabilities BuildCapabilities(PlayerSettings settings, AudioDeviceInfo? device)
    {
        var capabilities = PlayerCapabilities.Build(settings, device, _softwareVersion);

        _logger.LogInformation(
            "Advertising codecs, preferred first: {Codecs}",
            string.Join(
                ", ",
                capabilities.AudioFormats
                    .Select(format => format.Codec)
                    .Distinct(StringComparer.OrdinalIgnoreCase)));

        return capabilities;
    }

    private AudioDeviceInfo? ResolveDevice(string? deviceId)
    {
        try
        {
            if (!string.IsNullOrEmpty(deviceId))
            {
                var match = _deviceEnumerator.GetDevices().FirstOrDefault(d => d.Id == deviceId);
                if (match is not null)
                {
                    Volatile.Write(ref _activeDeviceName, match.Name);
                    return match;
                }

                _logger.LogInformation(
                    "Configured audio device {DeviceId} is not present; using the system default", deviceId);
            }

            var fallback = _deviceEnumerator.GetDefaultDevice();
            Volatile.Write(ref _activeDeviceName, fallback?.Name);
            return fallback;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Could not enumerate audio devices");
            return null;
        }
    }

    private void OnServerFound(object? sender, DiscoveredServer server)
    {
        _logger.LogInformation("Discovered {Name} at {Host}:{Port}", server.Name, server.Host, server.Port);
        DiscoveredServersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnServerUpdated(object? sender, DiscoveredServer server) =>
        DiscoveredServersChanged?.Invoke(this, EventArgs.Empty);

    private void OnServerLost(object? sender, DiscoveredServer server)
    {
        _logger.LogInformation("Lost {Name}", server.Name);
        DiscoveredServersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnHostServerConnected(object? sender, ConnectedServerInfo info)
    {
        lock (_sessionGate)
        {
            _serverName = info.ServerName;
        }

        _settings.Update(s => s.LastServerId = info.ServerId);
        _logger.LogInformation("Server {ServerName} connected to us", info.ServerName);
        RaiseConnectionChanged(connected: true, info.ServerName);
    }

    private void OnHostServerDisconnected(object? sender, string serverId)
    {
        lock (_sessionGate)
        {
            _serverName = null;
            _group = null;
            _mediaState = MediaSessionState.Idle;
            ResetSchedules();
        }

        _logger.LogInformation("Server {ServerId} disconnected", serverId);
        RaiseConnectionChanged(connected: false, serverName: null);
        PublishState();
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        _logger.LogInformation("Connection {OldState} -> {NewState} ({Reason})", e.OldState, e.NewState, e.Reason);

        if (e.NewState == ConnectionState.Disconnected)
        {
            lock (_sessionGate)
            {
                _serverName = null;
                _group = null;
                _mediaState = MediaSessionState.Idle;
                ResetSchedules();
            }

            RaiseConnectionChanged(connected: false, serverName: null);
            PublishState();
        }
    }

    /// <summary>
    /// Takes a group state: playback state, volume, mute and commands apply at once; the track
    /// metadata is scheduled for its timestamp.
    /// </summary>
    /// <remarks>
    /// Only the metadata carries a timestamp, and only the metadata describes something that has an
    /// audible moment. The SDK holds nothing pending — <see cref="GroupState.Metadata"/> is whatever
    /// arrived last — so the scheduling happens here, and <see cref="PublishState"/> reads the
    /// current metadata from the scheduler rather than from the group.
    /// </remarks>
    private void OnGroupStateChanged(object? sender, GroupState group)
    {
        var held = false;
        long aheadMicros = 0;

        lock (_sessionGate)
        {
            _group = group;

            if (group.Metadata is { } metadata && !ReferenceEquals(metadata, _offeredMetadata))
            {
                _offeredMetadata = metadata;

                var now = LocalClock();
                var due = metadata.Timestamp is { } timestamp ? ToLocalTime(timestamp, now) : now;
                held = _metadata.Offer(metadata, due, now) == ScheduledOffer.Held;
                aheadMicros = due - now;
                RearmPromotion(now);
            }
        }

        // Volume is server-authoritative: this is where the value that actually took effect
        // arrives, so this is where it is persisted for the next connection's initial state.
        PersistVolumeIfChanged(group.Volume, group.Muted);

        if (held)
        {
            LogFirstHold(ref _loggedHeldMetadata, "metadata", aheadMicros);
        }

        PublishState();
    }

    private void OnPlayerStateChanged(object? sender, PlayerState state)
    {
        PersistVolumeIfChanged(state.Volume, state.Muted);
        PublishState();
    }

    /// <summary>
    /// Persists volume and mute, but only when they actually changed.
    /// </summary>
    /// <remarks>
    /// Group and player state arrive on every <c>server/state</c>, including updates that change
    /// neither — so writing unconditionally rewrites the settings file and fans out a
    /// <c>Changed</c> event several times a second for the life of the session. Under Flatpak that
    /// file often lives on a size-limited tmpfs.
    /// </remarks>
    private void PersistVolumeIfChanged(int volume, bool muted)
    {
        var clamped = Math.Clamp(volume, 0, 100);
        var current = _settings.Current;

        if (current.Volume == clamped && current.Muted == muted)
        {
            return;
        }

        _settings.Update(s =>
        {
            s.Volume = clamped;
            s.Muted = muted;
        });
    }

    // Written to disk on arrival, published at its timestamp. The file is named by its bytes rather
    // than by the group's current metadata, which can lag the picture: see
    // MediaSessionMapper.ArtworkFileName.
    private void OnArtworkReceived(object? sender, ArtworkReceivedEventArgs e) =>
        ScheduleArtwork(e.Channel, _artworkCache.Write(e.ImageData), e.Timestamp);

    // A clear is an empty image and keeps the same timing, so a future timestamp schedules it.
    private void OnArtworkCleared(object? sender, ArtworkClearedEventArgs e) =>
        ScheduleArtwork(e.Channel, path: null, e.Timestamp);

    /// <summary>
    /// Offers a picture (or a clear, when <paramref name="path"/> is null) to its channel's
    /// scheduler for the server time <paramref name="serverTimestamp"/>.
    /// </summary>
    /// <remarks>
    /// Built on the SDK's one-event-per-complete-image surface rather than on the wire format.
    /// The spec now transfers an image as an announce and parts, which 9.3.2 predates; when an SDK
    /// bump adopts it, "transfer complete" moves into the SDK and this method is unchanged.
    /// </remarks>
    private void ScheduleArtwork(int channel, string? path, long serverTimestamp)
    {
        bool held;
        long aheadMicros;

        lock (_sessionGate)
        {
            if (!_artwork.TryGetValue(channel, out var slot))
            {
                slot = new ScheduledValue<string>();
                _artwork[channel] = slot;
            }

            var now = LocalClock();
            var due = ToLocalTime(serverTimestamp, now);
            held = slot.Offer(path, due, now) == ScheduledOffer.Held;
            aheadMicros = due - now;
            RearmPromotion(now);
        }

        if (held)
        {
            LogFirstHold(ref _loggedHeldArtwork, "artwork", aheadMicros);
            return;
        }

        PublishState();
    }

    /// <summary>
    /// Converts a server-clock timestamp to this machine's clock with the synchroniser's current
    /// best estimate — the spec asks for exactly that, not for convergence.
    /// </summary>
    /// <remarks>
    /// The conversion includes this player's static delay, which is right: the display should change
    /// when this player's audio does. With no synchroniser there is no session for the timestamp to
    /// belong to, so the value applies at once rather than being held for a conversion that will
    /// never come — a scheduled update is never held indefinitely. In practice the synchroniser
    /// exists before any event can arrive, because <see cref="EnsureAudioSession"/> runs before a
    /// connection is made in either mode.
    /// </remarks>
    private long ToLocalTime(long serverTimestamp, long nowLocalMicros) =>
        _clockSync?.ServerToClientTime(serverTimestamp) ?? nowLocalMicros;

    /// <summary>
    /// Arms the promotion timer for the earliest pending value, or disarms it when nothing is
    /// pending. Call under <see cref="_sessionGate"/>.
    /// </summary>
    private void RearmPromotion(long nowLocalMicros)
    {
        if (_isDisposed)
        {
            return;
        }

        long? next = _metadata.NextDue;

        foreach (var slot in _artwork.Values)
        {
            if (slot.NextDue is { } due && (next is null || due < next))
            {
                next = due;
            }
        }

        if (next is null)
        {
            _promotion.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        // Rounded up: a timer that fires a fraction early finds nothing due and has to re-arm.
        var delayMs = Math.Max(0, (next.Value - nowLocalMicros + 999) / 1000);
        _promotion.Change(delayMs, Timeout.Infinite);
    }

    private void OnPromotionDue()
    {
        try
        {
            PromoteDue();
        }
        catch (Exception ex)
        {
            // A timer callback has no caller to throw to; an unhandled exception here takes the
            // process down. The SDK's receive loop guards its handlers the same way.
            _logger.LogError(ex, "Promoting a scheduled update failed");
        }
    }

    /// <summary>
    /// Makes every pending value whose time has come current, and publishes if anything changed.
    /// </summary>
    /// <remarks>
    /// The timer's callback, and the tests' hand crank: they advance <see cref="LocalClock"/> past
    /// a due time and call this rather than waiting real seconds for the timer.
    /// </remarks>
    internal void PromoteDue()
    {
        var promoted = false;

        lock (_sessionGate)
        {
            if (_isDisposed)
            {
                return;
            }

            var now = LocalClock();
            promoted |= _metadata.Promote(now);

            foreach (var slot in _artwork.Values)
            {
                promoted |= slot.Promote(now);
            }

            RearmPromotion(now);
        }

        if (promoted)
        {
            PublishState();
        }
    }

    /// <summary>
    /// Forgets every current and pending value: the stream they belonged to is gone. Call under
    /// <see cref="_sessionGate"/>.
    /// </summary>
    /// <remarks>
    /// Called on disconnect. The spec also discards pending values on <c>stream/end</c>, but SDK
    /// 9.3.2 raises no event for it, so a pending value survives a stream end here until its
    /// timestamp — at most the 20 s the spec lets a server schedule ahead — and then promotes.
    /// </remarks>
    private void ResetSchedules()
    {
        _metadata.Reset();
        _offeredMetadata = null;
        _artwork.Clear();
        RearmPromotion(LocalClock());
    }

    /// <summary>
    /// Logs the first time an update is held, so the start-up log shows the schedule being honoured
    /// the way the backdrop's first-frame lines show it running.
    /// </summary>
    private void LogFirstHold(ref bool logged, string what, long aheadMicros)
    {
        if (logged)
        {
            return;
        }

        logged = true;
        _logger.LogInformation(
            "Holding the first scheduled {What} update for {AheadMs} ms until its timestamp",
            what,
            aheadMicros / 1000);
    }

    // Both forwarded as they arrive, on the SDK's thread. There is nothing to merge or persist:
    // the palette and the frames are display state, and the view model owns their marshalling.
    private void OnColorChanged(object? sender, ColorPalette palette) => PaletteChanged?.Invoke(this, palette);

    private void OnVisualizationReceived(object? sender, VisualizerFrame frame) =>
        VisualizerFrameReceived?.Invoke(this, frame);

    /// <summary>
    /// Rebuilds the media-session snapshot from the server's group state, the metadata and artwork
    /// currently in effect, and the time since that metadata took effect, and announces it.
    /// </summary>
    private void PublishState()
    {
        MediaSessionState state;

        lock (_sessionGate)
        {
            var metadata = _metadata.Current;
            var elapsed = metadata is null ? 0 : LocalClock() - _metadata.CurrentSince;
            var artwork = _artwork.TryGetValue(AlbumArtworkChannel, out var slot) ? slot.Current : null;

            state = MediaSessionMapper.FromGroupState(_group, metadata, artwork, elapsed);
            _mediaState = state;
        }

        StateChanged?.Invoke(this, state);
    }

    private void RaiseConnectionChanged(bool connected, string? serverName) =>
        ConnectionChanged?.Invoke(this, new ConnectionChangedEventArgs(connected, serverName));
}

/// <summary>
/// Reports that the connection came up or went down.
/// </summary>
/// <param name="IsConnected">Whether a server is now connected.</param>
/// <param name="ServerName">The server's name when connected, otherwise null.</param>
public sealed record ConnectionChangedEventArgs(bool IsConnected, string? ServerName);
