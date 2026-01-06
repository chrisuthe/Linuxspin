using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;
using SendspinClient.Linux.Services.Audio;

namespace SendspinClient.Linux.Services.Client;

/// <summary>
/// Manages Sendspin server discovery, connection, and audio playback.
/// Coordinates between the SDK components and the Linux audio player.
/// </summary>
public sealed class SendspinClientManager : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SendspinClientManager> _logger;
    private readonly Func<IAudioPlayer> _playerFactory;

    private MdnsServerDiscovery? _discovery;
    private SendspinConnection? _connection;
    private IClockSynchronizer? _clockSync;
    private IAudioPipeline? _audioPipeline;
    private SendspinClientService? _client;
    private bool _isDisposed;

    /// <summary>
    /// Fired when a server is discovered on the network.
    /// </summary>
    public event EventHandler<DiscoveredServer>? ServerDiscovered;

    /// <summary>
    /// Fired when a previously discovered server is lost.
    /// </summary>
    public event EventHandler<DiscoveredServer>? ServerLost;

    /// <summary>
    /// Fired when connection state changes.
    /// </summary>
    public event EventHandler<ConnectionStateEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// Fired when track metadata changes.
    /// </summary>
    public event EventHandler<TrackMetadataEventArgs>? TrackChanged;

    /// <summary>
    /// Gets whether the client is currently connected to a server.
    /// </summary>
    public bool IsConnected => _connection?.State == ConnectionState.Connected;

    /// <summary>
    /// Gets the current audio buffer statistics for sync monitoring.
    /// </summary>
    public AudioBufferStats? BufferStats => _audioPipeline?.BufferStats;

    /// <summary>
    /// Gets the current server name if connected.
    /// </summary>
    public string? ServerName => _client?.ServerName;

    public SendspinClientManager(
        ILoggerFactory loggerFactory,
        Func<IAudioPlayer> playerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<SendspinClientManager>();
        _playerFactory = playerFactory ?? throw new ArgumentNullException(nameof(playerFactory));
    }

    /// <summary>
    /// Starts discovering Sendspin servers on the local network.
    /// </summary>
    public async Task StartDiscoveryAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        _logger.LogInformation("Starting server discovery...");

        _discovery = new MdnsServerDiscovery(_loggerFactory.CreateLogger<MdnsServerDiscovery>());
        _discovery.ServerFound += OnServerFound;
        _discovery.ServerLost += OnServerLost;

        await _discovery.StartAsync(ct);
    }

    /// <summary>
    /// Stops server discovery.
    /// </summary>
    public async Task StopDiscoveryAsync()
    {
        if (_isDisposed || _discovery == null) return;

        _discovery.ServerFound -= OnServerFound;
        _discovery.ServerLost -= OnServerLost;
        await _discovery.StopAsync();
        _discovery = null;

        _logger.LogInformation("Server discovery stopped");
    }

    /// <summary>
    /// Gets the list of currently discovered servers.
    /// </summary>
    public IReadOnlyList<DiscoveredServer> GetDiscoveredServers()
    {
        return _discovery?.Servers.ToList() ?? [];
    }

    /// <summary>
    /// Connects to a discovered server and starts audio playback.
    /// </summary>
    public async Task ConnectAsync(DiscoveredServer server, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (IsConnected)
        {
            _logger.LogWarning("Already connected. Disconnect first.");
            return;
        }

        _logger.LogInformation("Connecting to {ServerName} at {Host}:{Port}",
            server.Name, server.Host, server.Port);

        try
        {
            // Create clock synchronizer
            _clockSync = new KalmanClockSynchronizer(_loggerFactory.CreateLogger<KalmanClockSynchronizer>());

            // Create audio pipeline with factories
            _audioPipeline = CreateAudioPipeline();

            // Create connection
            _connection = new SendspinConnection(_loggerFactory.CreateLogger<SendspinConnection>());

            // Create client capabilities
            var capabilities = new ClientCapabilities
            {
                ClientName = "Sendspin Linux",
                ProductName = "Sendspin Linux Client",
                Manufacturer = "Sendspin Contributors",
                SoftwareVersion = "1.0.0"
            };

            // Create client service with audio pipeline
            _client = new SendspinClientService(
                _loggerFactory.CreateLogger<SendspinClientService>(),
                _connection,
                _clockSync,
                capabilities,
                _audioPipeline);

            // Subscribe to events
            _client.ConnectionStateChanged += OnConnectionStateChanged;
            _client.GroupStateChanged += OnGroupStateChanged;

            // Build WebSocket URI and connect
            // Prefer IP address over hostname since mDNS hostnames may not be resolvable
            var host = server.IpAddresses.FirstOrDefault() ?? server.Host;
            var uri = new Uri($"ws://{host}:{server.Port}/sendspin");
            _logger.LogDebug("Connecting to URI: {Uri}", uri);
            await _client.ConnectAsync(uri, ct);

            _logger.LogInformation("Connected to {ServerName}", server.Name);
            ConnectionStateChanged?.Invoke(this, new ConnectionStateEventArgs(true, server.Name));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to {ServerName}", server.Name);
            await DisconnectAsync();
            throw;
        }
    }

    /// <summary>
    /// Connects to a server by host and port.
    /// </summary>
    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        // Create a minimal DiscoveredServer for manual connection
        var server = new DiscoveredServer
        {
            ServerId = $"{host}:{port}",
            Name = host,
            Host = host,
            Port = port,
            IpAddresses = [host]
        };
        await ConnectAsync(server, ct);
    }

    /// <summary>
    /// Creates the audio pipeline with all required factories.
    /// </summary>
    private IAudioPipeline CreateAudioPipeline()
    {
        var bufferCapacityMs = 8000; // 8 seconds of buffer capacity
        var bufferTargetMs = 250.0; // 250ms target buffer for low latency

        return new AudioPipeline(
            _loggerFactory.CreateLogger<AudioPipeline>(),
            new AudioDecoderFactory(),
            _clockSync!,
            bufferFactory: (format, sync) =>
            {
                var buffer = new TimedAudioBuffer(
                    format,
                    sync,
                    bufferCapacityMs,
                    syncOptions: SyncCorrectionOptions.CliDefaults,
                    _loggerFactory.CreateLogger<TimedAudioBuffer>());
                buffer.TargetBufferMilliseconds = bufferTargetMs;
                return buffer;
            },
            playerFactory: _playerFactory,
            sourceFactory: (buffer, timeFunc) => new BufferedAudioSampleSource(buffer, timeFunc),
            precisionTimer: null,
            waitForConvergence: true,
            convergenceTimeoutMs: 5000);
    }

    /// <summary>
    /// Disconnects from the current server and stops playback.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _logger.LogInformation("Disconnecting...");

        try
        {
            if (_client != null)
            {
                _client.ConnectionStateChanged -= OnConnectionStateChanged;
                _client.GroupStateChanged -= OnGroupStateChanged;
                await _client.DisconnectAsync();
                await _client.DisposeAsync();
                _client = null;
            }

            if (_audioPipeline != null)
            {
                await _audioPipeline.DisposeAsync();
                _audioPipeline = null;
            }

            _connection = null;
            _clockSync = null;

            ConnectionStateChanged?.Invoke(this, new ConnectionStateEventArgs(false, null));
            _logger.LogInformation("Disconnected");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during disconnect");
        }
    }

    /// <summary>
    /// Sends a play command to the server.
    /// </summary>
    public async Task PlayAsync()
    {
        if (_client != null)
            await _client.SendCommandAsync("play");
    }

    /// <summary>
    /// Sends a pause command to the server.
    /// </summary>
    public async Task PauseAsync()
    {
        if (_client != null)
            await _client.SendCommandAsync("pause");
    }

    /// <summary>
    /// Sets the playback volume (0-100).
    /// </summary>
    public async Task SetVolumeAsync(int volume)
    {
        if (_client != null)
            await _client.SetVolumeAsync(volume);
    }

    /// <summary>
    /// Sets the mute state.
    /// </summary>
    public void SetMuted(bool muted)
    {
        _audioPipeline?.SetMuted(muted);
    }

    private void OnServerFound(object? sender, DiscoveredServer server)
    {
        _logger.LogInformation("Discovered server: {Name} at {Host}:{Port}",
            server.Name, server.Host, server.Port);
        ServerDiscovered?.Invoke(this, server);
    }

    private void OnServerLost(object? sender, DiscoveredServer server)
    {
        _logger.LogInformation("Lost server: {Name}", server.Name);
        ServerLost?.Invoke(this, server);
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        _logger.LogInformation("Connection state: {OldState} -> {NewState}", e.OldState, e.NewState);

        if (e.NewState == ConnectionState.Disconnected)
        {
            ConnectionStateChanged?.Invoke(this, new ConnectionStateEventArgs(false, null));
        }
    }

    private void OnGroupStateChanged(object? sender, GroupState group)
    {
        if (group.Metadata != null)
        {
            _logger.LogDebug("Track: {Title} by {Artist}", group.Metadata.Title, group.Metadata.Artist);
            TrackChanged?.Invoke(this, new TrackMetadataEventArgs(
                group.Metadata.Title ?? "",
                group.Metadata.Artist ?? "",
                group.Metadata.Album));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await DisconnectAsync();

        if (_discovery != null)
        {
            _discovery.ServerFound -= OnServerFound;
            _discovery.ServerLost -= OnServerLost;
            await _discovery.StopAsync();
        }
    }
}

/// <summary>
/// Event args for connection state changes.
/// </summary>
public sealed class ConnectionStateEventArgs : EventArgs
{
    public bool IsConnected { get; }
    public string? ServerName { get; }

    public ConnectionStateEventArgs(bool isConnected, string? serverName)
    {
        IsConnected = isConnected;
        ServerName = serverName;
    }
}

/// <summary>
/// Event args for track metadata changes.
/// </summary>
public sealed class TrackMetadataEventArgs : EventArgs
{
    public string Title { get; }
    public string Artist { get; }
    public string? Album { get; }

    public TrackMetadataEventArgs(string title, string artist, string? album)
    {
        Title = title;
        Artist = artist;
        Album = album;
    }
}
