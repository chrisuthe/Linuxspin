using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.Core.MediaSession;

/// <summary>
/// Turns the SDK's <see cref="GroupState"/> into a <see cref="MediaSessionState"/>, and maps
/// media-surface identifiers back and forth.
/// </summary>
/// <remarks>
/// This is deliberately platform-neutral and free of I/O so it can be tested directly. The
/// three platform sessions hold marshalling only; every decision about what a shell should be
/// told lives here, once.
/// </remarks>
public static class MediaSessionMapper
{
    /// <summary>
    /// Root for MPRIS track ids.
    /// </summary>
    /// <remarks>
    /// Not under <c>/org/mpris</c>: that namespace is reserved by the specification, and
    /// <c>/org/mpris/MediaPlayer2/TrackList/NoTrack</c> in particular has a defined meaning
    /// that a real track must not collide with.
    /// </remarks>
    public const string TrackIdRoot = "/io/sendspin/client/track/";

    /// <summary>
    /// Projects the server's authoritative group state into a media-session snapshot.
    /// </summary>
    /// <param name="group">The group state, or null when disconnected.</param>
    /// <param name="artworkFilePath">
    /// Path to the artwork file for the current track, or null when there is none.
    /// </param>
    public static MediaSessionState FromGroupState(GroupState? group, string? artworkFilePath = null)
    {
        if (group is null)
        {
            return MediaSessionState.Idle;
        }

        var metadata = group.Metadata;
        var duration = ToTimeSpan(metadata?.Duration);
        var identity = BuildTrackIdentity(metadata);
        var commands = group.SupportedCommands ?? [];

        return new MediaSessionState
        {
            Status = ToPlaybackStatus(group.PlaybackState),
            Title = NullIfBlank(metadata?.Title),
            Artist = NullIfBlank(metadata?.Artist),
            Album = NullIfBlank(metadata?.Album),
            AlbumArtist = NullIfBlank(metadata?.AlbumArtist),
            ArtworkFilePath = artworkFilePath,
            Duration = duration,
            Position = ToTimeSpan(metadata?.Position) ?? TimeSpan.Zero,
            CanGoNext = Supports(commands, Commands.Next),
            CanGoPrevious = Supports(commands, Commands.Previous),
            // Always false. The player role has no seek command — position belongs to the server,
            // and a player asking to seek would be asking the whole group to move. Every OS surface
            // renders a draggable scrubber from this flag and PlayerCommandRouter then declines the
            // resulting intent, so advertising it would give all three shells a scrubber that snaps
            // back. The inbound handlers stay as defensive no-ops.
            CanSeek = false,
            Shuffle = group.Shuffle,
            Repeat = ToRepeatMode(group.Repeat),
            Volume = Math.Clamp(group.Volume, 0, 100),
            Muted = group.Muted,
            TrackIdentity = identity
        };
    }

    /// <summary>
    /// Maps the SDK's playback state onto what a shell can display.
    /// </summary>
    /// <remarks>
    /// <see cref="PlaybackState.Error"/> and <see cref="PlaybackState.Idle"/> both become
    /// <see cref="MediaPlaybackStatus.Stopped"/>: no OS surface has a representation for
    /// either, and reporting anything else leaves a shell showing a player as active when it
    /// is not.
    /// </remarks>
    public static MediaPlaybackStatus ToPlaybackStatus(PlaybackState state) => state switch
    {
        PlaybackState.Playing => MediaPlaybackStatus.Playing,
        PlaybackState.Paused => MediaPlaybackStatus.Paused,
        _ => MediaPlaybackStatus.Stopped
    };

    /// <summary>
    /// Parses the protocol's repeat string. Unknown values are
    /// <see cref="MediaRepeatMode.Off"/> rather than an exception: a newer server adding a
    /// mode must not break an older player.
    /// </summary>
    public static MediaRepeatMode ToRepeatMode(string? repeat) => repeat?.ToLowerInvariant() switch
    {
        "one" or "track" => MediaRepeatMode.One,
        "all" or "playlist" or "context" => MediaRepeatMode.All,
        _ => MediaRepeatMode.Off
    };

    /// <summary>
    /// Returns the protocol command that advances repeat one step from
    /// <paramref name="current"/>, cycling off to all to one and back.
    /// </summary>
    public static string NextRepeatCommand(MediaRepeatMode current) => current switch
    {
        MediaRepeatMode.Off => Commands.RepeatAll,
        MediaRepeatMode.All => Commands.RepeatOne,
        _ => Commands.RepeatOff
    };

    /// <summary>
    /// Returns the protocol command that toggles shuffle from its current state.
    /// </summary>
    public static string ToggleShuffleCommand(bool currentlyShuffling) =>
        currentlyShuffling ? Commands.Unshuffle : Commands.Shuffle;

    /// <summary>
    /// Maps the MPRIS <c>LoopStatus</c> property value onto a repeat mode.
    /// </summary>
    public static MediaRepeatMode FromMprisLoopStatus(string? loopStatus) => loopStatus switch
    {
        "Track" => MediaRepeatMode.One,
        "Playlist" => MediaRepeatMode.All,
        _ => MediaRepeatMode.Off
    };

    /// <summary>
    /// Renders a repeat mode as an MPRIS <c>LoopStatus</c> value.
    /// </summary>
    public static string ToMprisLoopStatus(MediaRepeatMode mode) => mode switch
    {
        MediaRepeatMode.One => "Track",
        MediaRepeatMode.All => "Playlist",
        _ => "None"
    };

    /// <summary>
    /// Renders a status as an MPRIS <c>PlaybackStatus</c> value.
    /// </summary>
    public static string ToMprisPlaybackStatus(MediaPlaybackStatus status) => status switch
    {
        MediaPlaybackStatus.Playing => "Playing",
        MediaPlaybackStatus.Paused => "Paused",
        _ => "Stopped"
    };

    /// <summary>
    /// Builds the MPRIS <c>mpris:trackid</c> object path for a track identity.
    /// </summary>
    /// <remarks>
    /// The result must be a syntactically valid D-Bus object path, so the identity is reduced
    /// to hex. A null or empty identity yields the specification's <c>NoTrack</c> path, which
    /// is how "nothing is playing" is expressed.
    /// </remarks>
    public static string ToMprisTrackId(string? trackIdentity)
    {
        if (string.IsNullOrEmpty(trackIdentity))
        {
            return "/org/mpris/MediaPlayer2/TrackList/NoTrack";
        }

        return TrackIdRoot + ToHexToken(trackIdentity);
    }

    /// <summary>
    /// Builds a filename for a piece of artwork from the image bytes themselves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unique per picture, and that is a requirement rather than tidiness. Every consumer of
    /// the path dedupes by it: the window reloads only when the path changes, and GNOME's
    /// texture cache and Plasma's applet cache cover art by URL for the life of the shell. So a
    /// new picture must always land at a new path, and the same picture re-sent may land at the
    /// same one.
    /// </para>
    /// <para>
    /// Hashing the bytes rather than the track's metadata is what makes that hold: with a queue
    /// the server can deliver the next track's picture before the next track's metadata, and a
    /// name taken from the metadata would then overwrite the current track's file in place,
    /// under a path nobody rereads.
    /// </para>
    /// </remarks>
    public static string ArtworkFileName(ReadOnlySpan<byte> imageData, string extension = "jpg") =>
        $"artwork-{ToHexToken(imageData)}.{extension.TrimStart('.')}";

    /// <summary>
    /// Derives a stable identity for a track from its metadata.
    /// </summary>
    /// <remarks>
    /// Hashed from artist, album and title rather than taken from a server-supplied id,
    /// because the protocol's metadata carries no track identifier. Position is deliberately
    /// excluded so that the identity, and therefore the MPRIS track id, stays put as a track
    /// plays.
    /// </remarks>
    public static string? BuildTrackIdentity(TrackMetadata? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        var title = NullIfBlank(metadata.Title);
        var artist = NullIfBlank(metadata.Artist);
        var album = NullIfBlank(metadata.Album);

        if (title is null && artist is null && album is null)
        {
            return null;
        }

        // Unit separator: a character no metadata field will contain, so ("A", "B") and
        // ("AB", "") cannot collapse into the same identity.
        const char FieldSeparator = '\u001f';
        return string.Join(FieldSeparator, artist ?? string.Empty, album ?? string.Empty, title ?? string.Empty);
    }

    private static bool Supports(IEnumerable<string> commands, string command) =>
        commands.Contains(command, StringComparer.OrdinalIgnoreCase);

    private static TimeSpan? ToTimeSpan(double? seconds)
    {
        if (seconds is null || double.IsNaN(seconds.Value) || seconds.Value <= 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds.Value);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Reduces arbitrary text to a short hex token safe in both a D-Bus object path and a
    /// filename.
    /// </summary>
    /// <remarks>
    /// Not a security boundary — it exists so that a title containing a slash, a colon or a
    /// non-ASCII character cannot produce an invalid path.
    /// </remarks>
    private static string ToHexToken(string value) => ToHexToken(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Reduces arbitrary bytes to a short hex token: SHA-256 truncated to 16 bytes.
    /// </summary>
    private static string ToHexToken(ReadOnlySpan<byte> value)
    {
        var hash = SHA256.HashData(value);
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLower(CultureInfo.InvariantCulture);
    }
}
