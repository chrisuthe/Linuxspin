using Microsoft.Extensions.Logging;
using Sendspin.Core.Configuration;
using Sendspin.Core.MediaSession;
using Sendspin.Core.Notifications;

namespace Sendspin.Platform.Shared.Notifications;

/// <summary>
/// Decides whether a notification should be raised at all, then hands it to the platform
/// service.
/// </summary>
/// <remarks>
/// <para>
/// The per-event toggles and the change detection live here, once, rather than in each
/// platform backend. A backend's job is to render a notification; deciding that a track change
/// happened, and that the user wants to hear about it, is not platform-specific.
/// </para>
/// <para>
/// Change detection is the load-bearing part. Playback state and metadata both arrive on every
/// <c>server/state</c>, including updates that change nothing, so dispatching on receipt alone
/// produces a stream of identical toasts.
/// </para>
/// </remarks>
public sealed class NotificationDispatcher
{
    private readonly INotificationService _service;
    private readonly SettingsService _settings;
    private readonly ILogger<NotificationDispatcher> _logger;

    private string? _lastTrackIdentity;
    private MediaPlaybackStatus? _lastStatus;

    public NotificationDispatcher(
        INotificationService service,
        SettingsService settings,
        ILogger<NotificationDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _service = service;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Raises whatever notifications this state change warrants.
    /// </summary>
    public async Task OnStateAsync(MediaSessionState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var preferences = _settings.Current.Notifications;

        if (state.TrackIdentity != _lastTrackIdentity)
        {
            _lastTrackIdentity = state.TrackIdentity;

            if (preferences.TrackChange && state.Title is not null)
            {
                await ShowAsync(
                    new NotificationRequest(
                        NotificationKind.TrackChange,
                        state.Title,
                        BuildTrackBody(state),
                        preferences.IncludeArtwork ? state.ArtworkFilePath : null),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (state.Status != _lastStatus)
        {
            var previous = _lastStatus;
            _lastStatus = state.Status;

            // Suppress the very first status report: arriving at "Stopped" because nothing has
            // started yet is not an event the user did anything to cause.
            if (previous is not null && preferences.PlaybackState)
            {
                await ShowAsync(
                    new NotificationRequest(
                        NotificationKind.PlaybackState,
                        state.Status switch
                        {
                            MediaPlaybackStatus.Playing => "Playing",
                            MediaPlaybackStatus.Paused => "Paused",
                            _ => "Stopped"
                        },
                        state.Title),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Raises a connect or disconnect notification, if the user wants them.
    /// </summary>
    public async Task OnConnectionAsync(
        string serverName,
        bool connected,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Current.Notifications.ConnectionState)
        {
            return;
        }

        await ShowAsync(
            new NotificationRequest(
                NotificationKind.ConnectionState,
                connected ? "Connected" : "Disconnected",
                serverName),
            cancellationToken).ConfigureAwait(false);

        if (!connected)
        {
            // Nothing is playing any more, so the next connection's first state report should
            // be treated as new rather than compared against a stale track.
            _lastTrackIdentity = null;
            _lastStatus = null;
        }
    }

    private static string? BuildTrackBody(MediaSessionState state)
    {
        if (state.Artist is null)
        {
            return state.Album;
        }

        return state.Album is null ? state.Artist : $"{state.Artist} — {state.Album}";
    }

    private async Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        if (!_service.IsAvailable)
        {
            return;
        }

        try
        {
            await _service.ShowAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A notification daemon that goes away mid-session, a D-Bus timeout, a revoked
            // permission: none of these are worth interrupting playback for, but all of them
            // are worth a log line rather than a silent disappearance.
            _logger.LogWarning(ex, "Notification of kind {Kind} could not be shown", request.Kind);
        }
    }
}
