using DiscordRPC;
using Microsoft.Extensions.Logging;
using Sendspin.Core.MediaSession;
using Sendspin.Core.Presence;

namespace Sendspin.Discord;

/// <summary>
/// Publishes what is playing to Discord Rich Presence.
/// </summary>
/// <remarks>
/// <para>
/// Off unless the user turns it on, and deliberately not on the critical path: this is a
/// feature, not a platform integration. Discord not being installed, not running, or refusing
/// the connection are all ordinary outcomes that must leave playback untouched.
/// </para>
/// <para>
/// Publishing is rate-limited. Discord's own IPC drops rapid updates, and presence has no need
/// to be more current than a few seconds.
/// </para>
/// </remarks>
public sealed class DiscordPresenceService : IPresenceService
{
    /// <summary>
    /// Discord application id this presence is published under.
    /// </summary>
    /// <remarks>
    /// A placeholder. Registering a real application at the Discord developer portal is a
    /// project-owner action, not a code change: it needs an account, a name and the uploaded
    /// artwork assets that <see cref="LargeImageKey"/> refers to. Until that exists Discord
    /// will refuse the handshake, which this class reports as unavailable rather than treating
    /// as an error.
    /// </remarks>
    private const string ApplicationId = "0";

    /// <summary>
    /// Asset key for the large presence image, as uploaded to the Discord application.
    /// </summary>
    private const string LargeImageKey = "sendspin";

    /// <summary>
    /// Minimum gap between presence updates.
    /// </summary>
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger<DiscordPresenceService> _logger;
    private readonly Lock _gate = new();

    private DiscordRpcClient? _client;
    private DateTime _lastUpdateUtc = DateTime.MinValue;
    private string? _lastPresenceKey;
    private bool _enabled;
    private bool _isDisposed;

    public DiscordPresenceService(ILogger<DiscordPresenceService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _client is { IsInitialized: true, IsDisposed: false };
            }
        }
    }

    /// <inheritdoc/>
    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        lock (_gate)
        {
            if (enabled == _enabled)
            {
                return Task.CompletedTask;
            }

            _enabled = enabled;

            if (enabled)
            {
                Connect();
            }
            else
            {
                Disconnect();
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Publish(MediaSessionState state, string? serverName)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_gate)
        {
            if (!_enabled || _client is not { IsInitialized: true })
            {
                return;
            }

            if (state.Status == MediaPlaybackStatus.Stopped || state.Title is null)
            {
                if (_lastPresenceKey is not null)
                {
                    _lastPresenceKey = null;
                    _client.ClearPresence();
                }

                return;
            }

            // Rate-limit by content as well as by time: a state report that changes nothing
            // should not consume an update slot, and Discord drops rapid updates anyway.
            var key = $"{state.Status}|{state.Title}|{state.Artist}|{serverName}";
            var now = DateTime.UtcNow;

            if (key == _lastPresenceKey && now - _lastUpdateUtc < UpdateInterval)
            {
                return;
            }

            _lastPresenceKey = key;
            _lastUpdateUtc = now;

            _client.SetPresence(new RichPresence
            {
                Details = Truncate(state.Title, 128),
                State = Truncate(BuildStateLine(state, serverName), 128),
                Assets = new Assets
                {
                    LargeImageKey = LargeImageKey,
                    LargeImageText = serverName is null ? "Sendspin" : $"Sendspin — {serverName}"
                },

                // Only claim a timestamp while actually playing: a paused player showing an
                // advancing elapsed time is worse than showing none.
                Timestamps = state.Status == MediaPlaybackStatus.Playing
                    ? new Timestamps(now - state.Position)
                    : null
            });
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return ValueTask.CompletedTask;
        }

        _isDisposed = true;

        lock (_gate)
        {
            _enabled = false;
            Disconnect();
        }

        return ValueTask.CompletedTask;
    }

    private static string BuildStateLine(MediaSessionState state, string? serverName)
    {
        if (state.Artist is not null && state.Album is not null)
        {
            return $"{state.Artist} — {state.Album}";
        }

        return state.Artist ?? state.Album ?? serverName ?? "Sendspin";
    }

    /// <summary>
    /// Truncates to Discord's per-field limit.
    /// </summary>
    /// <remarks>
    /// Discord rejects the whole presence update if any field is too long, so a long track
    /// title would otherwise silently stop presence working rather than just being clipped.
    /// </remarks>
    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private void Connect()
    {
        try
        {
            var client = new DiscordRpcClient(ApplicationId);

            client.OnReady += (_, args) =>
                _logger.LogInformation("Discord Rich Presence connected as {User}", args.User.Username);
            client.OnConnectionFailed += (_, _) =>
                _logger.LogInformation("Discord is not reachable; Rich Presence will stay off");
            client.OnError += (_, args) =>
                _logger.LogWarning("Discord Rich Presence error: {Message}", args.Message);

            if (client.Initialize())
            {
                _client = client;
                return;
            }

            _logger.LogInformation("Discord is not running; Rich Presence will stay off");
            client.Dispose();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Discord Rich Presence could not be started");
        }
        catch (IOException ex)
        {
            // The IPC pipe or socket is absent, which is simply what "Discord is not installed"
            // looks like from here.
            _logger.LogInformation(ex, "Discord IPC is unavailable; Rich Presence will stay off");
        }
    }

    private void Disconnect()
    {
        var client = _client;
        _client = null;
        _lastPresenceKey = null;

        if (client is null)
        {
            return;
        }

        try
        {
            if (client.IsInitialized)
            {
                client.ClearPresence();
            }
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "Discord client already disposed while clearing presence");
        }
        finally
        {
            client.Dispose();
        }
    }
}
