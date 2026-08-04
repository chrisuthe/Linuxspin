using Sendspin.Core.MediaSession;
using Tmds.DBus.Protocol;

namespace Sendspin.Platform.Linux.MediaSession;

/// <summary>
/// Marshals a <see cref="MediaSessionState"/> into the MPRIS <c>Metadata</c> map.
/// </summary>
/// <remarks>
/// Marshalling only. Every decision about what a shell should be told — status mapping, track
/// identity, the artwork filename, the track-id path — belongs to
/// <see cref="MediaSessionMapper"/> and is shared with the other two platforms.
/// </remarks>
internal static class MprisMetadata
{
    /// <summary>
    /// Builds the <c>a{sv}</c> metadata map for a state snapshot.
    /// </summary>
    /// <remarks>
    /// The D-Bus types here are exact requirements, not preferences. <c>xesam:artist</c> is
    /// <c>as</c>, an array, and a bare string in its place is silently dropped by both shells.
    /// <c>mpris:trackid</c> is an object path, unique per track and outside the reserved
    /// <c>/org/mpris</c> namespace. <c>mpris:length</c> is <c>x</c> in <em>microseconds</em>.
    /// <c>mpris:artUrl</c> must be a <c>file://</c> URL: KDE's lock screen installs a deny-all
    /// network factory that refuses <c>http</c> and <c>data:</c>, and GNOME has no <c>data:</c>
    /// backend at all.
    /// </remarks>
    public static VariantValue Build(MediaSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var metadata = new Dict<string, VariantValue>
        {
            ["mpris:trackid"] = VariantValue.ObjectPath(new ObjectPath(MediaSessionMapper.ToMprisTrackId(state.TrackIdentity)))
        };

        // A live stream has no length. Reporting one — even zero — gives the shell a seek bar
        // that can never land anywhere.
        if (!state.IsLive && state.Duration is { } duration)
        {
            metadata["mpris:length"] = VariantValue.Int64((long)duration.TotalMicroseconds);
        }

        if (FileUrl.TryCreate(state.ArtworkFilePath, out var artUrl))
        {
            metadata["mpris:artUrl"] = VariantValue.String(artUrl);
        }

        if (state.Title is { } title)
        {
            metadata["xesam:title"] = VariantValue.String(title);
        }

        if (state.Artist is { } artist)
        {
            metadata["xesam:artist"] = VariantValue.Array(new[] { artist });
        }

        if (state.AlbumArtist is { } albumArtist)
        {
            metadata["xesam:albumArtist"] = VariantValue.Array(new[] { albumArtist });
        }

        if (state.Album is { } album)
        {
            metadata["xesam:album"] = VariantValue.String(album);
        }

        return metadata.AsVariantValue();
    }

    /// <summary>
    /// Whether two states differ in anything the metadata map carries.
    /// </summary>
    /// <remarks>
    /// Drives whether <c>PropertiesChanged</c> needs to carry <c>Metadata</c> again. Compared
    /// field by field rather than by rebuilding and comparing the maps, because a
    /// <see cref="VariantValue"/> holding a dictionary has no cheap structural equality.
    /// </remarks>
    public static bool Differs(MediaSessionState previous, MediaSessionState current) =>
        previous.TrackIdentity != current.TrackIdentity ||
        previous.Title != current.Title ||
        previous.Artist != current.Artist ||
        previous.Album != current.Album ||
        previous.AlbumArtist != current.AlbumArtist ||
        previous.ArtworkFilePath != current.ArtworkFilePath ||
        previous.Duration != current.Duration;
}
