using System.Diagnostics;
using AppKit;
using CoreFoundation;
using Foundation;
using MediaPlayer;
using Microsoft.Extensions.Logging;
using Sendspin.Core.MediaSession;

namespace Sendspin.Platform.MacOS.MediaSession;

/// <summary>
/// Publishes to Control Center and the Now Playing widget through
/// <see cref="MPNowPlayingInfoCenter"/>, and receives media keys and remote commands through
/// <see cref="MPRemoteCommandCenter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three rules decide whether this works at all.</strong>
/// <see cref="MPNowPlayingInfoCenter.PlaybackState"/> is set on every playback transition — Apple
/// documents it as macOS-only and warns that remote control "may not work as expected" without it,
/// and omitting it is the usual cause of a Now Playing panel that stays empty. Clearing is done
/// with nil, never a blank dictionary. And there is no elapsed-time timer: the system extrapolates
/// elapsed time itself from the last publish, while frequent metadata updates hit an undocumented
/// throttle and are silently dropped, so <see cref="Publish"/> only reaches the OS on a genuine
/// change.
/// </para>
/// <para>
/// <c>togglePlayPause</c> is registered alongside the discrete pair because the two arrive from
/// different hardware: wired headphone controls send a toggle, Bluetooth sends play and pause.
/// Everything not handled is explicitly disabled — commands default to enabled, and an enabled
/// command whose handler does nothing is worse than one the system knows is unavailable.
/// </para>
/// <para>
/// No <c>CGEventTap</c>. It needs Accessibility permission, competes with every other player for
/// the same keys, and MediaPlayer already delivers media keys to whichever app owns Now Playing.
/// </para>
/// <para>
/// <strong>Forward risk.</strong> macOS 27 beta ships a new Swift-only Now Playing framework which
/// explicitly warns against mixing it with these APIs. It is not consumable from C#, so this
/// player stays on MediaPlayer — and must not adopt both, because the warning is about the
/// combination, not either one alone.
/// </para>
/// </remarks>
public sealed class NowPlayingMediaSession : IMediaSession
{
    /// <summary>
    /// How far the reported position may drift from the extrapolated one before it counts as a
    /// real event rather than the clock simply advancing.
    /// </summary>
    /// <remarks>
    /// Wide enough that ordinary playback never republishes, tight enough that a seek or a track
    /// change with a similar title does.
    /// </remarks>
    private const double PositionDriftToleranceSeconds = 2.0;

    private readonly ILogger<NowPlayingMediaSession> _logger;
    private readonly Lock _gate = new();
    private readonly List<(MPRemoteCommand Command, NSObject Target)> _handlers = [];

    private MediaSessionState? _lastPublished;
    private long _lastPublishedTimestamp;
    private string? _artworkPath;
    private MPMediaItemArtwork? _artwork;
    private bool _isActive;
    private bool _isDisposed;

    public NowPlayingMediaSession(ILogger<NowPlayingMediaSession> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public event EventHandler<MediaSessionIntentEventArgs>? IntentReceived;

    /// <inheritdoc/>
    public bool IsActive => Volatile.Read(ref _isActive);

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        try
        {
            RegisterCommands();
            MPNowPlayingInfoCenter.DefaultCenter.PlaybackState = MPNowPlayingPlaybackState.Stopped;
            Volatile.Write(ref _isActive, true);
            _logger.LogInformation("Now Playing session registered with MPRemoteCommandCenter");
        }
        catch (ObjCRuntime.ObjCException ex)
        {
            // An absent media surface is a normal outcome for this contract, so it is reported
            // through IsActive rather than thrown. Only Objective-C faults are caught: anything
            // else here is a bug in this file and should surface.
            _logger.LogWarning(ex, "MediaPlayer refused to register; Now Playing will be unavailable");
            Volatile.Write(ref _isActive, false);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Publish(MediaSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!IsActive || _isDisposed)
        {
            return;
        }

        lock (_gate)
        {
            if (!ShouldPublish(state))
            {
                return;
            }

            _lastPublished = state;
            _lastPublishedTimestamp = Stopwatch.GetTimestamp();
        }

        // The main queue is serial, so successive publishes stay in order, and AppKit-adjacent
        // work never lands on a pool thread.
        DispatchQueue.MainQueue.DispatchAsync(() => Apply(state));
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return ValueTask.CompletedTask;
        }

        _isDisposed = true;
        Volatile.Write(ref _isActive, false);

        foreach (var (command, target) in _handlers)
        {
            command.RemoveTarget(target);
            command.Enabled = false;
        }

        _handlers.Clear();

        var center = MPNowPlayingInfoCenter.DefaultCenter;
        Clear(center);
        center.PlaybackState = MPNowPlayingPlaybackState.Stopped;

        _artwork?.Dispose();
        _artwork = null;

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Removes the Now Playing entry.
    /// </summary>
    /// <remarks>
    /// Assigning nil is the documented way to clear it; a blank dictionary leaves an empty entry
    /// on screen rather than removing it. The binding forwards a managed null straight through to
    /// nil — verified by running it — but its <c>NowPlaying</c> property carries no nullable
    /// annotation, hence the suppression rather than a real null-safety hole.
    /// </remarks>
    private static void Clear(MPNowPlayingInfoCenter center) => center.NowPlaying = null!;

    private static MPNowPlayingPlaybackState ToPlaybackState(MediaPlaybackStatus status) => status switch
    {
        MediaPlaybackStatus.Playing => MPNowPlayingPlaybackState.Playing,
        MediaPlaybackStatus.Paused => MPNowPlayingPlaybackState.Paused,
        _ => MPNowPlayingPlaybackState.Stopped
    };

    /// <summary>
    /// Turns off every command this session does not answer.
    /// </summary>
    /// <remarks>
    /// Commands are enabled by default, so this is not tidying: leaving <c>seekForward</c> or
    /// <c>rating</c> enabled advertises controls that do nothing when pressed.
    /// </remarks>
    private static void DisableUnhandledCommands(MPRemoteCommandCenter center)
    {
        MPRemoteCommand[] unhandled =
        [
            center.StopCommand,
            center.SeekForwardCommand,
            center.SeekBackwardCommand,
            center.SkipForwardCommand,
            center.SkipBackwardCommand,
            center.ChangePlaybackRateCommand,
            center.ChangeRepeatModeCommand,
            center.ChangeShuffleModeCommand,
            center.RatingCommand,
            center.LikeCommand,
            center.DislikeCommand,
            center.BookmarkCommand,
            center.EnableLanguageOptionCommand,
            center.DisableLanguageOptionCommand
        ];

        foreach (var command in unhandled)
        {
            command.Enabled = false;
        }
    }

    private void RegisterCommands()
    {
        var center = MPRemoteCommandCenter.Shared;

        Handle(center.PlayCommand, _ => new MediaSessionIntentEventArgs(MediaSessionIntent.Play));
        Handle(center.PauseCommand, _ => new MediaSessionIntentEventArgs(MediaSessionIntent.Pause));
        Handle(center.TogglePlayPauseCommand, _ => new MediaSessionIntentEventArgs(MediaSessionIntent.TogglePlayPause));
        Handle(center.NextTrackCommand, _ => new MediaSessionIntentEventArgs(MediaSessionIntent.Next));
        Handle(center.PreviousTrackCommand, _ => new MediaSessionIntentEventArgs(MediaSessionIntent.Previous));

        Handle(center.ChangePlaybackPositionCommand, e => e is MPChangePlaybackPositionCommandEvent position
            ? new MediaSessionIntentEventArgs(MediaSessionIntent.Seek, TimeSpan.FromSeconds(position.PositionTime))
            : null);

        DisableUnhandledCommands(center);
    }

    /// <summary>
    /// Wires one remote command to an intent, and records the target so it can be removed.
    /// </summary>
    private void Handle(MPRemoteCommand command, Func<MPRemoteCommandEvent, MediaSessionIntentEventArgs?> map)
    {
        var target = command.AddTarget(commandEvent =>
        {
            var intent = map(commandEvent);
            if (intent is null)
            {
                return MPRemoteCommandHandlerStatus.CommandFailed;
            }

            // Raise and nothing else. Touching audio from here would bypass the server, which
            // owns transport state, and leave the UI and the actual state disagreeing.
            IntentReceived?.Invoke(this, intent);
            return MPRemoteCommandHandlerStatus.Success;
        });

        command.Enabled = true;
        _handlers.Add((command, target));
    }

    /// <summary>
    /// Decides whether <paramref name="state"/> is a genuine change, as opposed to the position
    /// having advanced.
    /// </summary>
    private bool ShouldPublish(MediaSessionState state)
    {
        var previous = _lastPublished;
        if (previous is null)
        {
            return true;
        }

        if (previous with { Position = TimeSpan.Zero } != state with { Position = TimeSpan.Zero })
        {
            return true;
        }

        var expected = previous.Position;
        if (previous.Status == MediaPlaybackStatus.Playing)
        {
            expected += Stopwatch.GetElapsedTime(_lastPublishedTimestamp);
        }

        return Math.Abs((state.Position - expected).TotalSeconds) > PositionDriftToleranceSeconds;
    }

    /// <summary>
    /// Pushes a state to MediaPlayer. Main queue only.
    /// </summary>
    private void Apply(MediaSessionState state)
    {
        if (_isDisposed)
        {
            return;
        }

        var center = MPNowPlayingInfoCenter.DefaultCenter;

        if (state.Status == MediaPlaybackStatus.Stopped && state.Title is null)
        {
            Clear(center);
            center.PlaybackState = MPNowPlayingPlaybackState.Stopped;
            UpdateCommandAvailability(state);
            return;
        }

        var info = new MPNowPlayingInfo
        {
            Title = state.Title,
            Artist = state.Artist,
            AlbumTitle = state.Album,
            MediaType = MPNowPlayingInfoMediaType.Audio,
            ElapsedPlaybackTime = state.Position.TotalSeconds,

            // The rate is how the system knows whether to advance the elapsed time it renders,
            // which is why no timer is needed here.
            PlaybackRate = state.Status == MediaPlaybackStatus.Playing ? 1.0 : 0.0,
            IsLiveStream = state.IsLive,
            Artwork = ResolveArtwork(state.ArtworkFilePath)
        };

        if (!state.IsLive && state.Duration is { } duration)
        {
            info.PlaybackDuration = duration.TotalSeconds;
        }

        center.NowPlaying = info;
        center.PlaybackState = ToPlaybackState(state.Status);
        UpdateCommandAvailability(state);
    }

    private void UpdateCommandAvailability(MediaSessionState state)
    {
        var center = MPRemoteCommandCenter.Shared;

        center.NextTrackCommand.Enabled = state.CanGoNext;
        center.PreviousTrackCommand.Enabled = state.CanGoPrevious;

        // A live stream has nowhere to scrub to, so the scrubber is withdrawn rather than shown
        // and ignored.
        center.ChangePlaybackPositionCommand.Enabled = state.CanSeek && !state.IsLive;
    }

    /// <summary>
    /// Builds the artwork for a file path, reusing the last one when the path has not changed.
    /// </summary>
    private MPMediaItemArtwork? ResolveArtwork(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            _artwork?.Dispose();
            _artwork = null;
            _artworkPath = null;
            return null;
        }

        if (string.Equals(path, _artworkPath, StringComparison.Ordinal) && _artwork is not null)
        {
            return _artwork;
        }

        var image = new NSImage(path);
        if (!image.IsValid)
        {
            _logger.LogDebug("Artwork at {Path} could not be decoded by AppKit", path);
            image.Dispose();
            return null;
        }

        _artwork?.Dispose();
        _artworkPath = path;
        _artwork = new MPMediaItemArtwork(image.Size, _ => image);
        return _artwork;
    }
}
