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
/// This replaces a manager that opened a second raw WebSocket to every discovered server to
/// hand-build its own <c>client/hello</c> and read back a display name. That duplicated work the
/// SDK already does, doubled the socket count, advertised only <c>player@v1</c>, and could not
/// survive a spec-current server. Discovery and connection here go entirely through
/// <see cref="MdnsServerDiscovery"/>, <see cref="SendspinHostService"/> and
/// <see cref="SendspinClientService"/>.
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

    private MdnsServerDiscovery? _discovery;
    private SendspinHostService? _host;
    private SendspinClientService? _client;
    private SendspinConnection? _connection;
    private IClockSynchronizer? _clockSync;
    private IAudioPipeline? _pipeline;
    private SyncCorrectedSampleSource? _sampleSource;
    private AudioPlayerBase? _activePlayer;

    private GroupState? _group;
    private MediaSessionState _mediaState = MediaSessionState.Idle;
    private string? _serverName;
    private string? _artworkPath;
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
    }

    /// <summary>Raised when the connection comes up or goes down.</summary>
    public event EventHandler<ConnectionChangedEventArgs>? ConnectionChanged;

    /// <summary>Raised whenever the server's group state changes.</summary>
    public event EventHandler<MediaSessionState>? StateChanged;

    /// <summary>Raised when a server appears in or disappears from discovery.</summary>
    public event EventHandler? DiscoveredServersChanged;

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

        if (mode is ConnectionMode.Auto or ConnectionMode.DiscoverOnly)
        {
            await StartDiscoveryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (mode is ConnectionMode.Auto or ConnectionMode.AdvertiseOnly)
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

        lock (_sessionGate)
        {
            client = _client;
            pipeline = _pipeline;
            _client = null;
            _connection = null;
            _group = null;
            _mediaState = MediaSessionState.Idle;
            _serverName = null;
        }

        if (client is not null)
        {
            client.ConnectionStateChanged -= OnConnectionStateChanged;
            client.GroupStateChanged -= OnGroupStateChanged;
            client.PlayerStateChanged -= OnPlayerStateChanged;
            client.ArtworkReceived -= OnArtworkReceived;
            client.ArtworkCleared -= OnArtworkCleared;

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

        // Stop the pipeline rather than dispose it. It is shared with the host service, which in
        // Auto mode is still advertising and may have a server connect a moment later; disposing
        // here would leave that service holding a dead pipeline.
        if (pipeline is not null)
        {
            await pipeline.StopAsync().ConfigureAwait(false);
        }

        RaiseConnectionChanged(connected: false, serverName: null);
        PublishState();
    }

    /// <inheritdoc/>
    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);

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
        var device = ResolveDevice(settings.AudioDeviceId);

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
            TimingSource = stats?.TimingSourceName ?? buffer?.TimingSourceName,
            AudioDeviceName = device?.Name,
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
            _host = null;
            host.ServerConnected -= OnHostServerConnected;
            host.ServerDisconnected -= OnHostServerDisconnected;
            host.GroupStateChanged -= OnGroupStateChanged;
            host.ArtworkReceived -= OnArtworkReceived;
            host.ArtworkCleared -= OnArtworkCleared;
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
        var capabilities = PlayerCapabilities.Build(settings, device, _softwareVersion);
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

        if (_client is { ConnectionState: ConnectionState.Connected })
        {
            _logger.LogInformation("Already connected; disconnecting first");
            await DisconnectAsync().ConfigureAwait(false);
        }

        var settings = _settings.Current;
        var device = ResolveDevice(settings.AudioDeviceId);
        var capabilities = PlayerCapabilities.Build(settings, device, _softwareVersion);
        var (clockSync, pipeline) = EnsureAudioSession(device);

        var connection = new SendspinConnection(
            _loggerFactory.CreateLogger<SendspinConnection>(),
            new ConnectionOptions());

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

        _logger.LogInformation("Connected to {ServerName}", resolvedName);
        RaiseConnectionChanged(connected: true, resolvedName);
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
    /// there is exactly one output device. In <see cref="ConnectionMode.Auto"/> both services run
    /// at once; giving each its own pipeline would put two audio players on the same device, and
    /// tearing one down would leave the other holding a disposed pipeline.
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

            var clockSync = new KalmanClockSynchronizer(_loggerFactory.CreateLogger<KalmanClockSynchronizer>())
            {
                StaticDelayMs = _settings.Current.StaticDelayMs
            };

            var pipeline = CreatePipeline(clockSync, device);

            _clockSync = clockSync;
            _pipeline = pipeline;

            return (clockSync, pipeline);
        }
    }

    /// <summary>
    /// Builds the audio pipeline, wiring our correction policy and latency figures into it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>waitForConvergence: true</c> is passed because the spec requires availability to be
    /// withheld until the time filter has converged. Note that SDK 9.1.0 does not fully honour
    /// it: on timeout it logs "Starting playback without full convergence" and proceeds. That
    /// gap is upstream and is recorded in <c>docs/COMPLIANCE.md</c> rather than papered over
    /// here with an inflated timeout, which would only hide it.
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
            bufferFactory: (format, sync) =>
            {
                var buffer = new TimedAudioBuffer(
                    format,
                    sync,
                    PlayerCapabilities.BufferCapacityMs,
                    syncOptions,
                    _loggerFactory.CreateLogger<TimedAudioBuffer>())
                {
                    TargetBufferMilliseconds = PlayerCapabilities.DefaultMinBufferMs
                };

                return buffer;
            },
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

                Volatile.Write(ref _sampleSource, source);
                return source;
            },
            precisionTimer: null,
            waitForConvergence: true,
            convergenceTimeoutMs: 5_000,
            useMonotonicTimer: true);
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
                    return match;
                }

                _logger.LogInformation(
                    "Configured audio device {DeviceId} is not present; using the system default", deviceId);
            }

            return _deviceEnumerator.GetDefaultDevice();
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
            }

            RaiseConnectionChanged(connected: false, serverName: null);
            PublishState();
        }
    }

    private void OnGroupStateChanged(object? sender, GroupState group)
    {
        lock (_sessionGate)
        {
            _group = group;
        }

        // Volume is server-authoritative: this is where the value that actually took effect
        // arrives, so this is where it is persisted for the next connection's initial state.
        _settings.Update(s =>
        {
            s.Volume = Math.Clamp(group.Volume, 0, 100);
            s.Muted = group.Muted;
        });

        PublishState();
    }

    private void OnPlayerStateChanged(object? sender, PlayerState state)
    {
        _settings.Update(s =>
        {
            s.Volume = Math.Clamp(state.Volume, 0, 100);
            s.Muted = state.Muted;
        });

        PublishState();
    }

    private void OnArtworkReceived(object? sender, ArtworkReceivedEventArgs e)
    {
        GroupState? group;
        lock (_sessionGate)
        {
            group = _group;
        }

        var identity = MediaSessionMapper.BuildTrackIdentity(group?.Metadata);
        _artworkPath = _artworkCache.Write(identity, e.ImageData);

        PublishState();
    }

    private void OnArtworkCleared(object? sender, ArtworkClearedEventArgs e)
    {
        _artworkPath = null;
        PublishState();
    }

    /// <summary>
    /// Rebuilds the media-session snapshot from the server's group state and announces it.
    /// </summary>
    private void PublishState()
    {
        MediaSessionState state;

        lock (_sessionGate)
        {
            state = MediaSessionMapper.FromGroupState(_group, _artworkPath);
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
