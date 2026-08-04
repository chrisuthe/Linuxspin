using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sendspin.Core.MediaSession;
using Windows.Foundation;
using Windows.Media;
using Windows.Storage.Streams;

// Both namespaces declare a MediaPlaybackStatus, so neither name is used unqualified.
using SessionPlaybackStatus = Sendspin.Core.MediaSession.MediaPlaybackStatus;
using SmtcPlaybackStatus = Windows.Media.MediaPlaybackStatus;

namespace Sendspin.Platform.Windows.MediaSession;

/// <summary>
/// Publishes to the Windows System Media Transport Controls — the media flyout above the volume
/// keys, the lock screen, and whatever a Bluetooth headset's buttons are wired to — and turns
/// what comes back into <see cref="MediaSessionIntent"/>s.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No package and no MSIX.</strong> <c>SystemMediaTransportControlsInterop</c> comes
/// from the Windows target framework, and a plain unpackaged desktop process gets a session by
/// handing it a window handle. That is why the handle arrives as a callback rather than being
/// resolved here: the window belongs to the app, and this project deliberately has no UI
/// framework reference.
/// </para>
/// <para>
/// <strong>Things that are load-bearing rather than polish.</strong>
/// <see cref="SystemMediaTransportControlsDisplayUpdater.Type"/> must be
/// <see cref="MediaPlaybackType.Music"/> or the flyout shows nothing.
/// <see cref="SystemMediaTransportControls.IsPlayEnabled"/> and
/// <see cref="SystemMediaTransportControls.IsPauseEnabled"/> must both be true or Windows stops
/// this app's audio when it goes to the background. The timeline needs
/// <see cref="SystemMediaTransportControlsTimelineProperties.MinSeekTime"/> and
/// <see cref="SystemMediaTransportControlsTimelineProperties.MaxSeekTime"/> as well as start,
/// end and position, or <see cref="SystemMediaTransportControls.PlaybackPositionChangeRequested"/>
/// never fires. And updates have to be throttled: pushed faster than roughly one per 750 ms the
/// control ends up drawn but not visible while still accepting button presses.
/// </para>
/// <para>
/// <strong>Threading.</strong> <see cref="SystemMediaTransportControls.ButtonPressed"/> arrives
/// on an MTA pool thread and <see cref="IntentReceived"/> is raised from there unchanged, which
/// is what <see cref="IMediaSession"/> specifies: the handler marshals, because only the app
/// knows what it is marshalling to.
/// </para>
/// </remarks>
public sealed class SmtcMediaSession : IMediaSession
{
    /// <summary>
    /// Minimum spacing between updates actually pushed to the control, in milliseconds.
    /// </summary>
    private const int ThrottleIntervalMs = 750;

    private readonly Func<nint?> _windowHandleProvider;
    private readonly ILogger<SmtcMediaSession> _logger;
    private readonly Lock _gate = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    private SystemMediaTransportControls? _controls;
    private TypedEventHandler<SystemMediaTransportControls, SystemMediaTransportControlsButtonPressedEventArgs>? _buttonPressed;
    private TypedEventHandler<SystemMediaTransportControls, PlaybackPositionChangeRequestedEventArgs>? _positionRequested;
    private TypedEventHandler<SystemMediaTransportControls, ShuffleEnabledChangeRequestedEventArgs>? _shuffleRequested;
    private TypedEventHandler<SystemMediaTransportControls, AutoRepeatModeChangeRequestedEventArgs>? _repeatRequested;
    private Timer? _coalesceTimer;
    private MediaSessionState? _pending;
    private MediaRepeatMode _publishedRepeat = MediaRepeatMode.Off;
    private string? _publishedArtworkPath;
    private long _lastPublishedAtMs = long.MinValue;
    private bool _disposed;

    /// <param name="windowHandleProvider">
    /// Returns the main window's handle, or null while there is no window yet. Called on
    /// <see cref="InitializeAsync"/> only.
    /// </param>
    /// <param name="logger">Logger for diagnostics.</param>
    public SmtcMediaSession(Func<nint?> windowHandleProvider, ILogger<SmtcMediaSession> logger)
    {
        ArgumentNullException.ThrowIfNull(windowHandleProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _windowHandleProvider = windowHandleProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public event EventHandler<MediaSessionIntentEventArgs>? IntentReceived;

    /// <inheritdoc/>
    public bool IsActive
    {
        get { lock (_gate) return _controls is not null; }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Call from the thread that owns the window: the control is bound to that window's
    /// apartment. Absence of a session is reported through <see cref="IsActive"/>, never thrown,
    /// and calling again is safe — a second call once the window exists attaches the session that
    /// the first could not.
    /// </remarks>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_disposed || _controls is not null)
            {
                return Task.CompletedTask;
            }

            var handle = ResolveWindowHandle();
            if (handle is null)
            {
                return Task.CompletedTask;
            }

            try
            {
                var controls = SystemMediaTransportControlsInterop.GetForWindow(handle.Value);

                controls.IsEnabled = true;

                // Not optional: Windows suspends a backgrounded app's audio unless it declares
                // that it handles play and pause itself.
                controls.IsPlayEnabled = true;
                controls.IsPauseEnabled = true;
                controls.IsStopEnabled = true;

                controls.DisplayUpdater.Type = MediaPlaybackType.Music;
                controls.DisplayUpdater.Update();

                Subscribe(controls);
                _controls = controls;

                _logger.LogInformation("System Media Transport Controls attached to window {Handle:X}", handle.Value);
            }
            catch (COMException ex)
            {
                _logger.LogWarning(
                    ex,
                    "System Media Transport Controls are unavailable; this player will not appear in the " +
                    "Windows media flyout");
            }

            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public void Publish(MediaSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_gate)
        {
            if (_disposed || _controls is null)
            {
                return;
            }

            _pending = state;

            var now = _elapsed.ElapsedMilliseconds;
            var dueAt = _lastPublishedAtMs + ThrottleIntervalMs;

            if (_lastPublishedAtMs == long.MinValue || now >= dueAt)
            {
                Flush(now);
                return;
            }

            // Coalesce: the newest state overwrites whatever was waiting, and one timer fires
            // when the window opens, so the control always ends up showing the latest state
            // without being updated more often than it tolerates.
            ScheduleFlush((int)(dueAt - now));
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;

            _coalesceTimer?.Dispose();
            _coalesceTimer = null;
            _pending = null;

            var controls = _controls;
            _controls = null;

            if (controls is not null)
            {
                Unsubscribe(controls);

                try
                {
                    controls.DisplayUpdater.ClearAll();
                    controls.DisplayUpdater.Update();
                    controls.PlaybackStatus = SmtcPlaybackStatus.Closed;
                    controls.IsEnabled = false;
                }
                catch (COMException ex)
                {
                    // The window may already be gone, which takes the session with it.
                    _logger.LogDebug(ex, "System Media Transport Controls could not be cleared during shutdown");
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    private static MediaPlaybackAutoRepeatMode ToAutoRepeatMode(MediaRepeatMode mode) => mode switch
    {
        MediaRepeatMode.One => MediaPlaybackAutoRepeatMode.Track,
        MediaRepeatMode.All => MediaPlaybackAutoRepeatMode.List,
        _ => MediaPlaybackAutoRepeatMode.None
    };

    private static MediaRepeatMode FromAutoRepeatMode(MediaPlaybackAutoRepeatMode mode) => mode switch
    {
        MediaPlaybackAutoRepeatMode.Track => MediaRepeatMode.One,
        MediaPlaybackAutoRepeatMode.List => MediaRepeatMode.All,
        _ => MediaRepeatMode.Off
    };

    private nint? ResolveWindowHandle()
    {
        nint? handle;

        try
        {
            handle = _windowHandleProvider();
        }
        catch (InvalidOperationException ex)
        {
            // The app's accessor throws when asked off the UI thread or before the window is
            // shown; either way there is no session to attach and no reason to fail startup.
            _logger.LogWarning(ex, "The main window handle could not be read; SMTC will stay inactive");
            return null;
        }

        if (handle is null || handle.Value == 0)
        {
            _logger.LogWarning("No main window handle is available yet; SMTC will stay inactive");
            return null;
        }

        return handle;
    }

    private void Subscribe(SystemMediaTransportControls controls)
    {
        _buttonPressed = (_, args) => OnButtonPressed(args.Button);
        _positionRequested = (_, args) =>
            Raise(new MediaSessionIntentEventArgs(MediaSessionIntent.Seek, args.RequestedPlaybackPosition));
        _shuffleRequested = (_, _) => Raise(new MediaSessionIntentEventArgs(MediaSessionIntent.ToggleShuffle));
        _repeatRequested = (_, args) => OnRepeatRequested(args.RequestedAutoRepeatMode);

        controls.ButtonPressed += _buttonPressed;
        controls.PlaybackPositionChangeRequested += _positionRequested;
        controls.ShuffleEnabledChangeRequested += _shuffleRequested;
        controls.AutoRepeatModeChangeRequested += _repeatRequested;
    }

    private void Unsubscribe(SystemMediaTransportControls controls)
    {
        if (_buttonPressed is not null)
        {
            controls.ButtonPressed -= _buttonPressed;
            _buttonPressed = null;
        }

        if (_positionRequested is not null)
        {
            controls.PlaybackPositionChangeRequested -= _positionRequested;
            _positionRequested = null;
        }

        if (_shuffleRequested is not null)
        {
            controls.ShuffleEnabledChangeRequested -= _shuffleRequested;
            _shuffleRequested = null;
        }

        if (_repeatRequested is not null)
        {
            controls.AutoRepeatModeChangeRequested -= _repeatRequested;
            _repeatRequested = null;
        }
    }

    private void OnButtonPressed(SystemMediaTransportControlsButton button)
    {
        var intent = button switch
        {
            SystemMediaTransportControlsButton.Play => MediaSessionIntent.Play,
            SystemMediaTransportControlsButton.Pause => MediaSessionIntent.Pause,
            SystemMediaTransportControlsButton.Stop => MediaSessionIntent.Stop,
            SystemMediaTransportControlsButton.Next => MediaSessionIntent.Next,
            SystemMediaTransportControlsButton.Previous => MediaSessionIntent.Previous,
            _ => (MediaSessionIntent?)null
        };

        if (intent is null)
        {
            _logger.LogDebug("Ignoring unsupported SMTC button {Button}", button);
            return;
        }

        Raise(new MediaSessionIntentEventArgs(intent.Value));
    }

    /// <summary>
    /// Turns the shell's requested repeat mode into the cycle intent the rest of the app speaks.
    /// </summary>
    /// <remarks>
    /// The shell asks for a specific mode, but the protocol's repeat commands are reached by
    /// cycling, so a request that does not differ from what was last published is dropped rather
    /// than turned into a cycle that lands somewhere else.
    /// </remarks>
    private void OnRepeatRequested(MediaPlaybackAutoRepeatMode requested)
    {
        if (FromAutoRepeatMode(requested) == _publishedRepeat)
        {
            return;
        }

        Raise(new MediaSessionIntentEventArgs(MediaSessionIntent.CycleRepeat));
    }

    private void Raise(MediaSessionIntentEventArgs args) => IntentReceived?.Invoke(this, args);

    /// <summary>
    /// Arms the coalescing timer. Caller holds <see cref="_gate"/>.
    /// </summary>
    private void ScheduleFlush(int delayMs)
    {
        _coalesceTimer ??= new Timer(OnCoalesceTimer, state: null, Timeout.Infinite, Timeout.Infinite);
        _coalesceTimer.Change(Math.Max(0, delayMs), Timeout.Infinite);
    }

    private void OnCoalesceTimer(object? state)
    {
        lock (_gate)
        {
            if (_disposed || _pending is null)
            {
                return;
            }

            Flush(_elapsed.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Pushes the pending state to the control. Caller holds <see cref="_gate"/>.
    /// </summary>
    private void Flush(long nowMs)
    {
        var controls = _controls;
        var state = _pending;

        if (controls is null || state is null)
        {
            return;
        }

        _pending = null;
        _lastPublishedAtMs = nowMs;

        try
        {
            ApplyTransportFlags(controls, state);
            ApplyMetadata(controls, state);
            ApplyTimeline(controls, state);
        }
        catch (COMException ex)
        {
            // The session dies with its window. Dropping the reference stops every later publish
            // from retrying against a dead object, and IsActive tells the UI the truth.
            _logger.LogWarning(ex, "System Media Transport Controls stopped responding; the session is now inactive");
            _controls = null;
        }
    }

    private void ApplyTransportFlags(SystemMediaTransportControls controls, MediaSessionState state)
    {
        controls.PlaybackStatus = state.Status switch
        {
            SessionPlaybackStatus.Playing => SmtcPlaybackStatus.Playing,
            SessionPlaybackStatus.Paused => SmtcPlaybackStatus.Paused,
            _ => SmtcPlaybackStatus.Stopped
        };

        controls.IsNextEnabled = state.CanGoNext;
        controls.IsPreviousEnabled = state.CanGoPrevious;
        controls.ShuffleEnabled = state.Shuffle;
        controls.AutoRepeatMode = ToAutoRepeatMode(state.Repeat);
        _publishedRepeat = state.Repeat;
    }

    private void ApplyMetadata(SystemMediaTransportControls controls, MediaSessionState state)
    {
        var updater = controls.DisplayUpdater;

        if (state.Title is null)
        {
            updater.ClearAll();

            // ClearAll resets the playback type too, and a session without Music shows nothing.
            updater.Type = MediaPlaybackType.Music;
            updater.Update();
            _publishedArtworkPath = null;
            return;
        }

        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = state.Title;
        updater.MusicProperties.Artist = state.Artist ?? string.Empty;
        updater.MusicProperties.AlbumTitle = state.Album ?? string.Empty;
        updater.MusicProperties.AlbumArtist = state.AlbumArtist ?? string.Empty;

        ApplyArtwork(updater, state.ArtworkFilePath);

        updater.Update();
    }

    /// <summary>
    /// Points the control at the artwork file, if it changed.
    /// </summary>
    /// <remarks>
    /// A <c>file://</c> stream reference rather than bytes: the shell reads the file itself, and
    /// creating a reference per publish would have it re-read the same image every 750 ms.
    /// </remarks>
    private void ApplyArtwork(SystemMediaTransportControlsDisplayUpdater updater, string? artworkFilePath)
    {
        if (artworkFilePath == _publishedArtworkPath)
        {
            return;
        }

        _publishedArtworkPath = artworkFilePath;

        if (artworkFilePath is null)
        {
            updater.Thumbnail = null;
            return;
        }

        try
        {
            updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(artworkFilePath));
        }
        catch (UriFormatException ex)
        {
            _logger.LogDebug(ex, "Artwork path {Path} is not a usable file URI", artworkFilePath);
            updater.Thumbnail = null;
            _publishedArtworkPath = null;
        }
    }

    private static void ApplyTimeline(SystemMediaTransportControls controls, MediaSessionState state)
    {
        var timeline = new SystemMediaTransportControlsTimelineProperties();

        // A live stream is left entirely at zero: an unbounded stream has no end to scrub to,
        // and a scrubber that cannot land anywhere is worse than none.
        if (state.Duration is { } duration && duration > TimeSpan.Zero)
        {
            timeline.StartTime = TimeSpan.Zero;
            timeline.EndTime = duration;
            timeline.Position = state.Position < TimeSpan.Zero
                ? TimeSpan.Zero
                : state.Position > duration ? duration : state.Position;

            if (state.CanSeek)
            {
                // Only now does PlaybackPositionChangeRequested fire. Leaving them at zero when
                // the server will not accept a seek is what keeps the scrubber inert.
                timeline.MinSeekTime = TimeSpan.Zero;
                timeline.MaxSeekTime = duration;
            }
        }

        controls.UpdateTimelineProperties(timeline);
    }
}
