using Microsoft.Extensions.Logging;
using Sendspin.Core.MediaSession;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.Core.Control;

/// <summary>
/// Where a routed command ends up: the live server connection.
/// </summary>
/// <remarks>
/// An interface rather than the SDK client directly, for two reasons. It lets
/// <see cref="PlayerCommandRouter"/> be tested without a socket, and it keeps the router
/// ignorant of connection lifetime — which is the app's problem, not the router's.
/// </remarks>
public interface IPlayerCommandSink
{
    /// <summary>
    /// Gets whether commands can currently be delivered.
    /// </summary>
    bool CanSend { get; }

    /// <summary>
    /// Gets the server's authoritative state, which the router needs in order to resolve
    /// relative commands such as toggle and cycle.
    /// </summary>
    MediaSessionState CurrentState { get; }

    /// <summary>
    /// Sends a controller command.
    /// </summary>
    Task SendCommandAsync(string command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the volume, 0-100.
    /// </summary>
    Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the mute state.
    /// </summary>
    Task SetMuteAsync(bool muted, CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised by the router for the two intents the app itself has to answer, rather than the
/// server.
/// </summary>
public enum LocalAction
{
    /// <summary>Bring the main window forward.</summary>
    Raise,

    /// <summary>Shut down.</summary>
    Quit
}

/// <summary>
/// The one path from "something asked for playback to change" to "a command reached the
/// server".
/// </summary>
/// <remarks>
/// <para>
/// Every origin funnels through here: a button in the window, a tray menu item, a hardware
/// media key arriving via SMTC, MPRIS or <c>MPRemoteCommandCenter</c>. The alternative —
/// each platform callback acting on the audio pipeline itself — produces a player whose local
/// state and whose group's state disagree, because the server owns transport for the whole
/// group and a local pause is invisible to it.
/// </para>
/// <para>
/// The router is also where relative commands are resolved. "Toggle play/pause" and "cycle
/// repeat" are only meaningful against the server's current state, so they are resolved from
/// <see cref="IPlayerCommandSink.CurrentState"/> here rather than from whatever the UI
/// happens to be showing.
/// </para>
/// </remarks>
public sealed class PlayerCommandRouter
{
    private readonly IPlayerCommandSink _sink;
    private readonly ILogger<PlayerCommandRouter> _logger;

    public PlayerCommandRouter(IPlayerCommandSink sink, ILogger<PlayerCommandRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(logger);

        _sink = sink;
        _logger = logger;
    }

    /// <summary>
    /// Raised for intents the app handles itself instead of forwarding.
    /// </summary>
    public event EventHandler<LocalAction>? LocalActionRequested;

    /// <summary>
    /// Routes an intent.
    /// </summary>
    /// <remarks>
    /// Returns without acting, having logged at debug level, when there is no connection.
    /// A media key pressed while disconnected is ordinary, not an error.
    /// </remarks>
    public async Task RouteAsync(MediaSessionIntentEventArgs intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        if (intent.Intent is MediaSessionIntent.Raise or MediaSessionIntent.Quit)
        {
            LocalActionRequested?.Invoke(
                this,
                intent.Intent == MediaSessionIntent.Raise ? LocalAction.Raise : LocalAction.Quit);
            return;
        }

        if (!_sink.CanSend)
        {
            _logger.LogDebug("Ignoring {Intent}: not connected to a server", intent.Intent);
            return;
        }

        var state = _sink.CurrentState;

        switch (intent.Intent)
        {
            case MediaSessionIntent.Play:
                await _sink.SendCommandAsync(Commands.Play, cancellationToken);
                break;

            case MediaSessionIntent.Pause:
                await _sink.SendCommandAsync(Commands.Pause, cancellationToken);
                break;

            case MediaSessionIntent.TogglePlayPause:
                await _sink.SendCommandAsync(
                    state.Status == MediaPlaybackStatus.Playing ? Commands.Pause : Commands.Play,
                    cancellationToken);
                break;

            case MediaSessionIntent.Stop:
                await _sink.SendCommandAsync(Commands.Stop, cancellationToken);
                break;

            case MediaSessionIntent.Next:
                await _sink.SendCommandAsync(Commands.Next, cancellationToken);
                break;

            case MediaSessionIntent.Previous:
                await _sink.SendCommandAsync(Commands.Previous, cancellationToken);
                break;

            case MediaSessionIntent.ToggleShuffle:
                await _sink.SendCommandAsync(
                    MediaSessionMapper.ToggleShuffleCommand(state.Shuffle),
                    cancellationToken);
                break;

            case MediaSessionIntent.CycleRepeat:
                await _sink.SendCommandAsync(
                    MediaSessionMapper.NextRepeatCommand(state.Repeat),
                    cancellationToken);
                break;

            case MediaSessionIntent.SetVolume:
                if (intent.Volume is { } volume)
                {
                    await _sink.SetVolumeAsync(Math.Clamp(volume, 0, 100), cancellationToken);
                }
                else
                {
                    _logger.LogWarning("SetVolume intent carried no volume; ignoring");
                }

                break;

            case MediaSessionIntent.SetMute:
                if (intent.Muted is { } muted)
                {
                    await _sink.SetMuteAsync(muted, cancellationToken);
                }
                else
                {
                    _logger.LogWarning("SetMute intent carried no mute state; ignoring");
                }

                break;

            case MediaSessionIntent.Seek:
                // The Sendspin protocol has no seek command for the player role: position is
                // the server's business, and a player asking to seek would be asking the
                // group to move. Media surfaces still offer a scrubber, so the intent
                // arrives; declining it explicitly beats silently dropping it in a default
                // case.
                _logger.LogDebug("Seek is not part of the player role; ignoring request for {Position}",
                    intent.Position);
                break;

            default:
                _logger.LogWarning("Unhandled media session intent {Intent}", intent.Intent);
                break;
        }
    }
}
