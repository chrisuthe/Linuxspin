using Sendspin.Core.MediaSession;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for the mapping from the server's group state onto what OS media surfaces are told.
/// </summary>
public sealed class MediaSessionMapperTests
{
    [Fact]
    public void FromGroupState_WithNoGroup_IsIdle() =>
        Assert.Equal(MediaSessionState.Idle, MediaSessionMapper.FromGroupState(group: null));

    [Fact]
    public void FromGroupState_ProjectsMetadataAndCapabilities()
    {
        var group = new GroupState
        {
            PlaybackState = PlaybackState.Playing,
            Volume = 64,
            Muted = true,
            Shuffle = true,
            Repeat = "all",
            SupportedCommands = [Commands.Next, Commands.Previous, Commands.Play],
            Metadata = new TrackMetadata
            {
                Title = "Sonata",
                Artist = "Someone",
                Album = "An Album",
                AlbumArtist = "Someone Else",
                // The wire units are MILLISECONDS. TrackMetadata.Duration and .Position are the
                // SDK's convenience properties and are in seconds. Confusing the two produces a
                // duration a thousand times too small, and every media surface then shows a
                // four-minute track as a quarter of a second.
                Progress = new PlaybackProgress { TrackDuration = 245_000.0, TrackProgress = 30_000.0 }
            }
        };

        var state = MediaSessionMapper.FromGroupState(group, "/tmp/art.jpg");

        Assert.Equal(MediaPlaybackStatus.Playing, state.Status);
        Assert.Equal("Sonata", state.Title);
        Assert.Equal("Someone", state.Artist);
        Assert.Equal("An Album", state.Album);
        Assert.Equal("Someone Else", state.AlbumArtist);
        Assert.Equal("/tmp/art.jpg", state.ArtworkFilePath);
        Assert.Equal(TimeSpan.FromSeconds(245), state.Duration);
        Assert.Equal(TimeSpan.FromSeconds(30), state.Position);
        Assert.True(state.CanGoNext);
        Assert.True(state.CanGoPrevious);
        Assert.False(state.IsLive);
        Assert.True(state.Shuffle);
        Assert.Equal(MediaRepeatMode.All, state.Repeat);
        Assert.Equal(64, state.Volume);
        Assert.True(state.Muted);
    }

    /// <summary>
    /// The position is the spec's formula run from the metadata's own timestamp, so a state published
    /// some time after the metadata took effect reports where the track is now.
    /// </summary>
    [Fact]
    public void FromGroupState_ProjectsThePositionForwardFromTheMetadataTimestamp()
    {
        var metadata = new TrackMetadata
        {
            Title = "Sonata",
            Progress = new PlaybackProgress { TrackDuration = 245_000.0, TrackProgress = 30_000.0, PlaybackSpeed = 1000 }
        };
        var group = new GroupState { PlaybackState = PlaybackState.Playing, Metadata = metadata };

        var state = MediaSessionMapper.FromGroupState(group, metadata, "/tmp/art.jpg", elapsedMicrosSinceMetadata: 2_500_000);

        Assert.Equal(TimeSpan.FromSeconds(32.5), state.Position);
        Assert.Equal(TimeSpan.FromSeconds(245), state.Duration);
    }

    /// <summary>
    /// The metadata argument, not the group's own, is what is projected: the group's is whatever the
    /// server sent last, which may be a scheduled update for the next track.
    /// </summary>
    [Fact]
    public void FromGroupState_ProjectsTheMetadataItIsGivenNotTheGroups()
    {
        var current = new TrackMetadata { Title = "One", Progress = new PlaybackProgress { TrackProgress = 1_000.0 } };
        var scheduled = new TrackMetadata { Title = "Two", Progress = new PlaybackProgress { TrackProgress = 0.0 } };
        var group = new GroupState { PlaybackState = PlaybackState.Playing, Metadata = scheduled };

        var state = MediaSessionMapper.FromGroupState(group, current, artworkFilePath: null, elapsedMicrosSinceMetadata: 0);

        Assert.Equal("One", state.Title);
        Assert.Equal(TimeSpan.FromSeconds(1), state.Position);
    }

    [Fact]
    public void ProjectPosition_AdvancesAtNormalSpeed()
    {
        var progress = new PlaybackProgress { TrackProgress = 30_000.0, TrackDuration = 245_000.0, PlaybackSpeed = 1000 };

        Assert.Equal(TimeSpan.FromSeconds(33), MediaSessionMapper.ProjectPosition(progress, elapsedMicros: 3_000_000));
    }

    /// <summary>A speed of 0 is the spec's paused: the position holds at the reported one.</summary>
    [Fact]
    public void ProjectPosition_WhenPaused_HoldsStill()
    {
        var progress = new PlaybackProgress { TrackProgress = 30_000.0, TrackDuration = 245_000.0, PlaybackSpeed = 0 };

        Assert.Equal(TimeSpan.FromSeconds(30), MediaSessionMapper.ProjectPosition(progress, elapsedMicros: 3_000_000));
    }

    [Fact]
    public void ProjectPosition_AtOneAndAHalfSpeed_AdvancesOneAndAHalfTimesAsFast()
    {
        var progress = new PlaybackProgress { TrackProgress = 30_000.0, TrackDuration = 245_000.0, PlaybackSpeed = 1500 };

        Assert.Equal(TimeSpan.FromSeconds(34.5), MediaSessionMapper.ProjectPosition(progress, elapsedMicros: 3_000_000));
    }

    [Fact]
    public void ProjectPosition_IsClampedToTheDuration()
    {
        var progress = new PlaybackProgress { TrackProgress = 240_000.0, TrackDuration = 245_000.0, PlaybackSpeed = 1000 };

        Assert.Equal(TimeSpan.FromSeconds(245), MediaSessionMapper.ProjectPosition(progress, elapsedMicros: 60_000_000));
    }

    /// <summary>A zero duration is the protocol's unbounded stream, so there is nothing to clamp to.</summary>
    [Fact]
    public void ProjectPosition_WithNoDuration_IsNotClamped()
    {
        var progress = new PlaybackProgress { TrackProgress = 240_000.0, TrackDuration = 0.0, PlaybackSpeed = 1000 };

        Assert.Equal(TimeSpan.FromSeconds(300), MediaSessionMapper.ProjectPosition(progress, elapsedMicros: 60_000_000));
    }

    [Fact]
    public void ProjectPosition_AssumesNormalSpeedWhenNoneIsGiven()
    {
        var progress = new PlaybackProgress { TrackProgress = 1_000.0 };

        Assert.Equal(TimeSpan.FromSeconds(2), MediaSessionMapper.ProjectPosition(progress, elapsedMicros: 1_000_000));
    }

    [Fact]
    public void ProjectPosition_IsNeverNegative()
    {
        var progress = new PlaybackProgress { TrackProgress = -5_000.0, PlaybackSpeed = 1000 };

        Assert.Equal(TimeSpan.Zero, MediaSessionMapper.ProjectPosition(progress, elapsedMicros: 0));
        Assert.Equal(TimeSpan.FromSeconds(1), MediaSessionMapper.ProjectPosition(new PlaybackProgress { TrackProgress = 1_000.0 }, elapsedMicros: -3_000_000));
    }

    [Fact]
    public void ProjectPosition_WithNoProgress_IsZero() =>
        Assert.Equal(TimeSpan.Zero, MediaSessionMapper.ProjectPosition(progress: null, elapsedMicros: 5_000_000));

    /// <summary>
    /// Seek is never offered, because the player role has no seek command.
    /// </summary>
    /// <remarks>
    /// Asserted for a bounded track specifically: it would be easy to "fix" this by deriving it
    /// from the duration, which is what every OS surface then renders a dead scrubber from.
    /// </remarks>
    [Fact]
    public void FromGroupState_NeverAdvertisesSeek()
    {
        var group = new GroupState
        {
            PlaybackState = PlaybackState.Playing,
            SupportedCommands = [Commands.Next, Commands.Previous],
            Metadata = new TrackMetadata
            {
                Title = "A track with a known length",
                Progress = new PlaybackProgress { TrackDuration = 200_000.0, TrackProgress = 1_000.0 }
            }
        };

        Assert.False(MediaSessionMapper.FromGroupState(group).CanSeek);
    }

    /// <summary>
    /// An absent duration is the protocol's way of saying "unbounded", not missing data.
    /// </summary>
    [Fact]
    public void FromGroupState_WithNoDuration_IsLiveAndNotSeekable()
    {
        var group = new GroupState
        {
            PlaybackState = PlaybackState.Playing,
            Metadata = new TrackMetadata { Title = "Some Radio Station" }
        };

        var state = MediaSessionMapper.FromGroupState(group);

        Assert.True(state.IsLive);
        Assert.Null(state.Duration);
        Assert.False(state.CanSeek);
    }

    /// <summary>
    /// A command the server did not advertise must not be offered.
    /// </summary>
    [Fact]
    public void FromGroupState_DoesNotAdvertiseUnsupportedCommands()
    {
        var group = new GroupState
        {
            PlaybackState = PlaybackState.Paused,
            SupportedCommands = [Commands.Play, Commands.Pause],
            Metadata = new TrackMetadata { Title = "Track" }
        };

        var state = MediaSessionMapper.FromGroupState(group);

        Assert.False(state.CanGoNext);
        Assert.False(state.CanGoPrevious);
    }

    /// <summary>
    /// No shell has a representation for an errored or idle player, so both read as stopped.
    /// </summary>
    [Theory]
    [InlineData(PlaybackState.Playing, MediaPlaybackStatus.Playing)]
    [InlineData(PlaybackState.Paused, MediaPlaybackStatus.Paused)]
    [InlineData(PlaybackState.Stopped, MediaPlaybackStatus.Stopped)]
    [InlineData(PlaybackState.Idle, MediaPlaybackStatus.Stopped)]
    [InlineData(PlaybackState.Error, MediaPlaybackStatus.Stopped)]
    public void ToPlaybackStatus_MapsEveryProtocolState(PlaybackState input, MediaPlaybackStatus expected) =>
        Assert.Equal(expected, MediaSessionMapper.ToPlaybackStatus(input));

    [Theory]
    [InlineData("off", MediaRepeatMode.Off)]
    [InlineData("one", MediaRepeatMode.One)]
    [InlineData("track", MediaRepeatMode.One)]
    [InlineData("all", MediaRepeatMode.All)]
    [InlineData("playlist", MediaRepeatMode.All)]
    [InlineData(null, MediaRepeatMode.Off)]
    [InlineData("something-a-newer-server-invented", MediaRepeatMode.Off)]
    public void ToRepeatMode_IsToleranceOfUnknownValues(string? repeat, MediaRepeatMode expected) =>
        Assert.Equal(expected, MediaSessionMapper.ToRepeatMode(repeat));

    [Fact]
    public void NextRepeatCommand_CyclesOffToAllToOneAndBack()
    {
        Assert.Equal(Commands.RepeatAll, MediaSessionMapper.NextRepeatCommand(MediaRepeatMode.Off));
        Assert.Equal(Commands.RepeatOne, MediaSessionMapper.NextRepeatCommand(MediaRepeatMode.All));
        Assert.Equal(Commands.RepeatOff, MediaSessionMapper.NextRepeatCommand(MediaRepeatMode.One));
    }

    [Fact]
    public void ToggleShuffleCommand_SendsTheOppositeOfTheCurrentState()
    {
        Assert.Equal(Commands.Shuffle, MediaSessionMapper.ToggleShuffleCommand(currentlyShuffling: false));
        Assert.Equal(Commands.Unshuffle, MediaSessionMapper.ToggleShuffleCommand(currentlyShuffling: true));
    }

    [Theory]
    [InlineData(MediaRepeatMode.Off, "None")]
    [InlineData(MediaRepeatMode.One, "Track")]
    [InlineData(MediaRepeatMode.All, "Playlist")]
    public void MprisLoopStatus_RoundTrips(MediaRepeatMode mode, string expected)
    {
        Assert.Equal(expected, MediaSessionMapper.ToMprisLoopStatus(mode));
        Assert.Equal(mode, MediaSessionMapper.FromMprisLoopStatus(expected));
    }

    [Theory]
    [InlineData(MediaPlaybackStatus.Playing, "Playing")]
    [InlineData(MediaPlaybackStatus.Paused, "Paused")]
    [InlineData(MediaPlaybackStatus.Stopped, "Stopped")]
    public void ToMprisPlaybackStatus_UsesTheSpecStrings(MediaPlaybackStatus status, string expected) =>
        Assert.Equal(expected, MediaSessionMapper.ToMprisPlaybackStatus(status));

    /// <summary>
    /// The track id must be a valid object path outside the reserved namespace.
    /// </summary>
    /// <remarks>
    /// <c>/org/mpris</c> is reserved by the specification, and
    /// <c>/org/mpris/MediaPlayer2/TrackList/NoTrack</c> in particular has a defined meaning that a
    /// real track must not collide with.
    /// </remarks>
    [Fact]
    public void ToMprisTrackId_IsAValidPathOutsideTheReservedNamespace()
    {
        var trackId = MediaSessionMapper.ToMprisTrackId("ArtistAlbumTitle");

        Assert.StartsWith(MediaSessionMapper.TrackIdRoot, trackId, StringComparison.Ordinal);
        Assert.DoesNotContain("/org/mpris", trackId, StringComparison.Ordinal);
        Assert.Matches("^(/[A-Za-z0-9_]+)+$", trackId);
    }

    [Fact]
    public void ToMprisTrackId_WithNoTrack_UsesTheSpecNoTrackPath() =>
        Assert.Equal("/org/mpris/MediaPlayer2/TrackList/NoTrack", MediaSessionMapper.ToMprisTrackId(null));

    /// <summary>
    /// A title containing path or D-Bus metacharacters must still produce a valid identifier.
    /// </summary>
    [Fact]
    public void ToMprisTrackId_SurvivesHostileMetadata()
    {
        var trackId = MediaSessionMapper.ToMprisTrackId("../../etc/passwd:weird:é — ✨");

        Assert.Matches("^(/[A-Za-z0-9_]+)+$", trackId);
    }

    /// <summary>
    /// Artwork filenames must be unique per picture, and stable for one.
    /// </summary>
    /// <remarks>
    /// Every consumer dedupes by path, so a new picture must be a new path; the name comes from the
    /// bytes rather than the track for the reason <see cref="MediaSessionMapper.ArtworkFileName"/>
    /// gives.
    /// </remarks>
    [Fact]
    public void ArtworkFileName_DiffersPerPictureAndIsStableForOne()
    {
        byte[] first = [0xFF, 0xD8, 0xFF, 0x01];
        byte[] second = [0xFF, 0xD8, 0xFF, 0x02];

        Assert.NotEqual(MediaSessionMapper.ArtworkFileName(first), MediaSessionMapper.ArtworkFileName(second));
        Assert.Equal(MediaSessionMapper.ArtworkFileName(first), MediaSessionMapper.ArtworkFileName([.. first]));
        Assert.DoesNotContain(Path.DirectorySeparatorChar, MediaSessionMapper.ArtworkFileName(first));
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, MediaSessionMapper.ArtworkFileName(first));
    }

    /// <summary>
    /// The extension is the caller's, and the name does not depend on it being dotted.
    /// </summary>
    [Fact]
    public void ArtworkFileName_TakesTheExtensionEitherWay()
    {
        byte[] bytes = [1, 2, 3];

        Assert.EndsWith(".png", MediaSessionMapper.ArtworkFileName(bytes, "png"));
        Assert.Equal(
            MediaSessionMapper.ArtworkFileName(bytes, "png"),
            MediaSessionMapper.ArtworkFileName(bytes, ".png"));
    }

    /// <summary>
    /// Identity must not move as a track plays, or artwork would be rewritten and the MPRIS track
    /// id would change on every position report.
    /// </summary>
    [Fact]
    public void BuildTrackIdentity_IgnoresPosition()
    {
        var early = MediaSessionMapper.BuildTrackIdentity(new TrackMetadata
        {
            Title = "Song",
            Artist = "Band",
            Album = "Record",
            Progress = new PlaybackProgress { TrackProgress = 5_000.0, TrackDuration = 200_000.0 }
        });

        var late = MediaSessionMapper.BuildTrackIdentity(new TrackMetadata
        {
            Title = "Song",
            Artist = "Band",
            Album = "Record",
            Progress = new PlaybackProgress { TrackProgress = 190_000.0, TrackDuration = 200_000.0 }
        });

        Assert.Equal(early, late);
    }

    /// <summary>
    /// Fields must not be able to run together into a colliding identity.
    /// </summary>
    [Fact]
    public void BuildTrackIdentity_DoesNotCollideAcrossFieldBoundaries()
    {
        var first = MediaSessionMapper.BuildTrackIdentity(
            new TrackMetadata { Artist = "AB", Album = string.Empty, Title = "C" });
        var second = MediaSessionMapper.BuildTrackIdentity(
            new TrackMetadata { Artist = "A", Album = "B", Title = "C" });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BuildTrackIdentity_WithNoMetadata_IsNull()
    {
        Assert.Null(MediaSessionMapper.BuildTrackIdentity(null));
        Assert.Null(MediaSessionMapper.BuildTrackIdentity(new TrackMetadata()));
        Assert.Null(MediaSessionMapper.BuildTrackIdentity(new TrackMetadata { Title = "   " }));
    }
}
