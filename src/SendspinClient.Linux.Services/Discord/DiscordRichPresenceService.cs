using DiscordRPC;
using DiscordRPC.Logging;
using Microsoft.Extensions.Logging;

namespace SendspinClient.Linux.Services.Discord;

/// <summary>
/// Service for integrating Discord Rich Presence to show current playback status.
/// </summary>
public interface IDiscordRichPresenceService : IAsyncDisposable
{
    bool IsConnected { get; }
    Task InitializeAsync(CancellationToken ct = default);
    void UpdatePresence(string? trackTitle, string? artist, string? serverName, bool isPlaying);
    void ClearPresence();
}

/// <summary>
/// Implementation of Discord Rich Presence for Sendspin Linux client.
/// </summary>
public sealed class DiscordRichPresenceService : IDiscordRichPresenceService
{
    // TODO: Replace with actual Discord Application ID
    private const string DiscordApplicationId = "1234567890";
    private const string LargeImageKey = "sendspin-logo";
    private const string LargeImageText = "Sendspin";

    private readonly ILogger<DiscordRichPresenceService> _logger;
    private readonly object _lock = new();
    private DiscordRpcClient? _client;
    private bool _isInitialized;
    private bool _isDisposed;
    private DateTime? _playbackStartTime;

    public DiscordRichPresenceService(ILogger<DiscordRichPresenceService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _client?.IsInitialized == true && !_client.IsDisposed;
            }
        }
    }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(DiscordRichPresenceService));
            if (_isInitialized) return Task.CompletedTask;

            try
            {
                _client = new DiscordRpcClient(DiscordApplicationId)
                {
                    Logger = new DiscordLoggerBridge(_logger)
                };

                _client.OnReady += (s, e) => _logger.LogInformation("Discord ready: {User}", e.User.Username);
                _client.OnError += (s, e) => _logger.LogWarning("Discord error: {Msg}", e.Message);
                _client.OnConnectionFailed += (s, e) => _logger.LogWarning("Discord connection failed");
                _client.OnClose += (s, e) => _logger.LogInformation("Discord closed: {Reason}", e.Reason);

                if (_client.Initialize())
                {
                    _isInitialized = true;
                    _logger.LogInformation("Discord Rich Presence initialized");
                }
                else
                {
                    _logger.LogWarning("Discord not running");
                    DisposeClientInternal();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to init Discord Rich Presence");
                DisposeClientInternal();
            }
        }
        return Task.CompletedTask;
    }

    public void UpdatePresence(string? trackTitle, string? artist, string? serverName, bool isPlaying)
    {
        lock (_lock)
        {
            if (_isDisposed || _client == null || !_isInitialized) return;

            try
            {
                var presence = new RichPresence
                {
                    Details = string.IsNullOrWhiteSpace(trackTitle)
                        ? (isPlaying ? "Listening" : "Idle")
                        : Truncate(trackTitle, 128),
                    Assets = new Assets
                    {
                        LargeImageKey = LargeImageKey,
                        LargeImageText = LargeImageText,
                        SmallImageKey = isPlaying ? "playing" : "paused",
                        SmallImageText = isPlaying ? "Playing" : "Paused"
                    }
                };

                var state = BuildStateText(artist, serverName);
                if (!string.IsNullOrWhiteSpace(state))
                    presence.State = Truncate(state, 128);

                if (isPlaying)
                {
                    _playbackStartTime ??= DateTime.UtcNow;
                    presence.Timestamps = new Timestamps(_playbackStartTime.Value);
                }
                else
                {
                    _playbackStartTime = null;
                }

                _client.SetPresence(presence);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update Discord presence");
            }
        }
    }

    public void ClearPresence()
    {
        lock (_lock)
        {
            if (_isDisposed || _client == null || !_isInitialized) return;
            try
            {
                _client.ClearPresence();
                _playbackStartTime = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clear Discord presence");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_isDisposed) return ValueTask.CompletedTask;
            _isDisposed = true;
            DisposeClientInternal();
        }
        return ValueTask.CompletedTask;
    }

    private void DisposeClientInternal()
    {
        if (_client == null) return;
        try { _client.ClearPresence(); } catch { }
        try { _client.Dispose(); } catch { }
        _client = null;
        _isInitialized = false;
    }

    private static string BuildStateText(string? artist, string? serverName)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(artist)) parts.Add($"by {artist}");
        if (!string.IsNullOrWhiteSpace(serverName)) parts.Add($"on {serverName}");
        return string.Join(" ", parts);
    }

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..(max - 3)] + "...";

    private sealed class DiscordLoggerBridge : DiscordRPC.Logging.ILogger
    {
        private readonly Microsoft.Extensions.Logging.ILogger<DiscordRichPresenceService> _logger;
        public DiscordLoggerBridge(Microsoft.Extensions.Logging.ILogger<DiscordRichPresenceService> logger) => _logger = logger;
        public DiscordRPC.Logging.LogLevel Level { get; set; } = DiscordRPC.Logging.LogLevel.Warning;
        public void Error(string message, params object[] args) => _logger.LogError(message, args);
        public void Warning(string message, params object[] args) => _logger.LogWarning(message, args);
        public void Info(string message, params object[] args) => _logger.LogInformation(message, args);
        public void Trace(string message, params object[] args) => _logger.LogTrace(message, args);
    }
}
