namespace Sendspin.Core.MediaSession;

/// <summary>
/// Playback status as the OS media surfaces model it.
/// </summary>
/// <remarks>
/// Deliberately not the SDK's <see cref="Sendspin.SDK.Models.PlaybackState"/>: that has an
/// <c>Error</c> member which no media surface has a representation for, and the mapping from
/// it is a decision (an errored player is <see cref="Stopped"/> to the shell) that belongs in
/// one tested place rather than repeated in three platform adapters.
/// </remarks>
public enum MediaPlaybackStatus
{
    Stopped,
    Playing,
    Paused
}

/// <summary>
/// Repeat mode, in the three states every OS surface and the protocol agree on.
/// </summary>
public enum MediaRepeatMode
{
    Off,
    One,
    All
}

/// <summary>
/// Everything the OS media surfaces need to render this player, in one immutable snapshot.
/// </summary>
/// <remarks>
/// <para>
/// One snapshot rather than a property bag with individual setters, because every platform
/// surface wants a coherent set: SMTC needs the whole timeline at once or
/// <c>PlaybackPositionChangeRequested</c> never fires, and MPRIS must emit the complete
/// <c>Metadata</c> map in <c>changed_properties</c> or KDE discards the entry.
/// </para>
/// <para>
/// Artwork travels as a file path, not bytes. Both Linux shells require it: KDE's lock screen
/// installs a deny-all network factory, so <c>http</c> and <c>data:</c> are both blocked, and
/// GNOME has no <c>data:</c> GVfs backend at all. Since one platform's hard requirement is
/// the strictest, all three use it.
/// </para>
/// </remarks>
public sealed record MediaSessionState
{
    /// <summary>Gets the playback status.</summary>
    public MediaPlaybackStatus Status { get; init; } = MediaPlaybackStatus.Stopped;

    /// <summary>Gets the track title, or null when nothing is playing.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the track artist.</summary>
    public string? Artist { get; init; }

    /// <summary>Gets the album name.</summary>
    public string? Album { get; init; }

    /// <summary>Gets the album artist.</summary>
    public string? AlbumArtist { get; init; }

    /// <summary>
    /// Gets an absolute path to the artwork file for this track, or null when there is none.
    /// </summary>
    public string? ArtworkFilePath { get; init; }

    /// <summary>
    /// Gets the track duration, or null for an unbounded stream.
    /// </summary>
    /// <remarks>
    /// Null is the live-radio case and is meaningful, not missing data: a surface must show
    /// elapsed time only and offer no seek bar.
    /// </remarks>
    public TimeSpan? Duration { get; init; }

    /// <summary>Gets the elapsed position.</summary>
    public TimeSpan Position { get; init; }

    /// <summary>Gets whether the stream is unbounded, i.e. live.</summary>
    public bool IsLive => Duration is null || Duration.Value <= TimeSpan.Zero;

    /// <summary>Gets whether the server currently accepts a next-track command.</summary>
    public bool CanGoNext { get; init; }

    /// <summary>Gets whether the server currently accepts a previous-track command.</summary>
    public bool CanGoPrevious { get; init; }

    /// <summary>Gets whether seeking is offered. Never true for a live stream.</summary>
    public bool CanSeek { get; init; }

    /// <summary>Gets whether shuffle is on.</summary>
    public bool Shuffle { get; init; }

    /// <summary>Gets the repeat mode.</summary>
    public MediaRepeatMode Repeat { get; init; } = MediaRepeatMode.Off;

    /// <summary>Gets the protocol volume, 0-100.</summary>
    public int Volume { get; init; } = 100;

    /// <summary>Gets whether output is muted.</summary>
    public bool Muted { get; init; }

    /// <summary>
    /// Gets a stable identity for the current track, used to key artwork files and the MPRIS
    /// track id. Null when nothing is playing.
    /// </summary>
    public string? TrackIdentity { get; init; }

    /// <summary>An empty state, published when disconnected.</summary>
    public static MediaSessionState Idle { get; } = new();
}

/// <summary>
/// A command arriving from an OS media surface.
/// </summary>
public enum MediaSessionIntent
{
    Play,
    Pause,

    /// <summary>
    /// Toggle. A distinct intent because wired headphones send a toggle while Bluetooth sends
    /// discrete play and pause, so a session that only handles the discrete pair loses the
    /// wired case.
    /// </summary>
    TogglePlayPause,

    Stop,
    Next,
    Previous,
    Seek,
    SetVolume,
    SetMute,
    ToggleShuffle,
    CycleRepeat,

    /// <summary>Bring the window to the front.</summary>
    Raise,

    /// <summary>Quit the application.</summary>
    Quit
}

/// <summary>
/// An intent from a media surface, with its argument where it has one.
/// </summary>
/// <param name="Intent">What was asked for.</param>
/// <param name="Position">Target position, for <see cref="MediaSessionIntent.Seek"/>.</param>
/// <param name="Volume">Target protocol volume 0-100, for <see cref="MediaSessionIntent.SetVolume"/>.</param>
/// <param name="Muted">Target mute state, for <see cref="MediaSessionIntent.SetMute"/>.</param>
public sealed record MediaSessionIntentEventArgs(
    MediaSessionIntent Intent,
    TimeSpan? Position = null,
    int? Volume = null,
    bool? Muted = null);

/// <summary>
/// An OS media surface: publish state to it, receive intents from it.
/// </summary>
/// <remarks>
/// <para>
/// Shaped as publish/receive rather than as the union of SMTC, MPRIS and
/// <c>MPNowPlayingInfoCenter</c>, because that union is three times the surface and every
/// member of it leaks a platform concept. There is no cross-platform .NET library for this;
/// Rust's <c>souvlaki</c> is the structural precedent.
/// </para>
/// <para>
/// Implementations must raise <see cref="IntentReceived"/> and nothing else. In particular an
/// implementation must never touch the audio pipeline: a platform callback that pokes audio
/// directly bypasses the server, which owns transport state, and produces a player whose UI
/// and whose actual state disagree. Intents go through the same controller path as a click
/// in the app.
/// </para>
/// </remarks>
public interface IMediaSession : IAsyncDisposable
{
    /// <summary>
    /// Gets whether the session is live. False when the platform surface is unavailable —
    /// which is normal, not an error, and must not be fatal.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Raised when a media surface asks for something. May arrive on any thread; the handler
    /// is responsible for marshalling.
    /// </summary>
    event EventHandler<MediaSessionIntentEventArgs>? IntentReceived;

    /// <summary>
    /// Connects to the platform surface. Must not throw when the surface is absent; report
    /// via <see cref="IsActive"/> and log instead.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes the current state. Called often; implementations throttle as their platform
    /// requires.
    /// </summary>
    void Publish(MediaSessionState state);
}

/// <summary>
/// The media session used where a platform has no surface, or where one failed to start.
/// </summary>
/// <remarks>
/// A null object rather than a nullable dependency: it removes the null checks that were
/// scattered through the old view model, and it keeps "no media session" from being a state
/// every caller has to remember to handle.
/// </remarks>
public sealed class NullMediaSession : IMediaSession
{
    /// <inheritdoc/>
    public bool IsActive => false;

    /// <inheritdoc/>
    public event EventHandler<MediaSessionIntentEventArgs>? IntentReceived;

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public void Publish(MediaSessionState state)
    {
        // Nothing to publish to. IntentReceived is never raised; referencing it here keeps
        // the compiler from warning about an event that is part of the interface contract.
        _ = IntentReceived;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
