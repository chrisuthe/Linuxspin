using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.Core.Audio;
using Sendspin.Core.Configuration;
using Sendspin.Core.MediaSession;
using Sendspin.Platform.Shared.Client;
using Sendspin.Platform.Shared.Media;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests that the player service forwards the palette and the visualizer frames a session
/// delivers, that the artwork it publishes survives the order a queue delivers it in, and that
/// artwork and metadata stamped for the future are held until their time.
/// </summary>
/// <remarks>
/// <para>
/// <b>The dialled path is driven end to end.</b> The service builds its own SDK client around the
/// transport its <c>ConnectionFactory</c> makes, so a <see cref="RecordingConnection"/> handed in
/// there answers the handshake and then delivers a <c>server/state</c> carrying a colour object
/// and a binary loudness frame, and what comes out of the service's two events is asserted.
/// </para>
/// <para>
/// <b>The advertised path is read, not driven</b>, for the reason <see cref="HostArbitrationTests"/>
/// gives: the host's own sessions only exist once a server has dialled its listener, and its
/// forwarding of <c>ColorChanged</c> and <c>VisualizationReceived</c> is wired inside the SDK for
/// those sessions alone — an adopted client is arbitrated, not forwarded. Reaching that wiring
/// means <c>StartAsync</c>, which binds the listener <em>and</em> advertises on mDNS, and on a LAN
/// with a real server that is an invitation for it to dial in mid-test. So the host subscription
/// is the same pair of lines as the client's, beside the artwork pair it mirrors, and is read
/// from <c>StartAdvertisingAsync</c> and <c>DisposeAsync</c>.
/// </para>
/// </remarks>
public sealed class PlayerServiceEventTests
{
    private const string ServerHello = """
        {"type":"server/hello","payload":{"server_id":"srv","name":"Test","version":1,
        "supported_roles":["player@v1","controller@v1","metadata@v1","artwork@v1","color@v1","visualizer@v1"],
        "active_roles":["player@v1","controller@v1","metadata@v1","artwork@v1","color@v1","visualizer@v1"]}}
        """;

    [Fact]
    public async Task TheDialledSession_ForwardsThePaletteAsTheServerSentIt()
    {
        using var paths = new TemporaryPaths();
        var (service, connection) = await ConnectAsync(paths);
        await using var _ = service;

        var palettes = new List<ColorPalette>();
        service.PaletteChanged += (_, palette) => palettes.Add(palette);

        connection.Receive("""
            {"type":"server/state","payload":{"color":{
              "background_dark":[30,30,46],"primary":[109,40,217],"accent":[6,182,212],"on_dark":[219,39,119]}}}
            """);

        var palette = Assert.Single(palettes);
        Assert.Equal(new RgbColor(30, 30, 46), palette.BackgroundDark);
        Assert.Equal(new RgbColor(109, 40, 217), palette.Primary);
        Assert.Equal(new RgbColor(6, 182, 212), palette.Accent);
        Assert.Equal(new RgbColor(219, 39, 119), palette.OnDark);
        Assert.Null(palette.BackgroundLight);
        Assert.Null(palette.OnLight);
    }

    [Fact]
    public async Task TheDialledSession_ForwardsLoudnessAndBeatFrames()
    {
        using var paths = new TemporaryPaths();
        var (service, connection) = await ConnectAsync(paths);
        await using var _ = service;

        var frames = new List<VisualizerFrame>();
        service.VisualizerFrameReceived += (_, frame) => frames.Add(frame);

        connection.ReceiveBinary(LoudnessFrame(timestamp: 1_000, loudness: 32768));
        connection.ReceiveBinary(BeatFrame(timestamp: 2_000, downbeat: true));

        Assert.Equal(2, frames.Count);
        Assert.Equal(32768, frames[0].Loudness);
        Assert.Null(frames[0].IsDownbeat);
        Assert.Equal(1_000, frames[0].Timestamp);
        Assert.True(frames[1].IsDownbeat);
        Assert.Null(frames[1].Loudness);
    }

    /// <summary>
    /// A picture that arrives before its track's metadata must still be published as a change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order a queue produces: track 1's metadata, its artwork A, then artwork B <em>before</em>
    /// track 2's metadata. Every consumer of the published path dedupes by it, so if B were written
    /// under the name the current metadata implies it would overwrite A's file in place and nothing
    /// would notice. The path has to change the moment B arrives, and the two paths have to differ.
    /// </para>
    /// <para>
    /// The same picture re-sent must land at the same path: that is the dedupe working as intended,
    /// not a regression.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ArtworkArrivingBeforeItsMetadata_IsPublishedUnderANewPath()
    {
        using var paths = new TemporaryPaths();
        var (service, connection) = await ConnectAsync(paths);
        await using var _ = service;

        var published = new List<string?>();
        service.StateChanged += (_, state) => published.Add(state.ArtworkFilePath);

        connection.Receive("""
            {"type":"server/state","payload":{"metadata":{"title":"One","artist":"A","album":"X"}}}
            """);
        connection.ReceiveBinary(ArtworkFrame(timestamp: 1_000, Jpeg(marker: 0xA1)));
        var pathA = published[^1];

        connection.ReceiveBinary(ArtworkFrame(timestamp: 2_000, Jpeg(marker: 0xB2)));
        var pathB = published[^1];

        connection.Receive("""
            {"type":"server/state","payload":{"metadata":{"title":"Two","artist":"A","album":"X"}}}
            """);

        Assert.NotNull(pathA);
        Assert.NotNull(pathB);
        Assert.NotEqual(pathA, pathB);
        Assert.Equal(pathB, published[^1]);
        Assert.True(File.Exists(pathA));
        Assert.True(File.Exists(pathB));

        connection.ReceiveBinary(ArtworkFrame(timestamp: 3_000, Jpeg(marker: 0xB2)));

        Assert.Equal(pathB, published[^1]);
    }

    /// <summary>
    /// The first track after connect is the same race with no metadata at all: two pictures before
    /// any <c>server/state</c> must be two files, with the newer one published once there is a
    /// state to publish it in.
    /// </summary>
    /// <remarks>
    /// Nothing is published until the first group state arrives — the session is idle — so the
    /// evidence is the cache directory, not the event stream.
    /// </remarks>
    [Fact]
    public async Task TwoPicturesBeforeAnyMetadata_AreTwoFiles()
    {
        using var paths = new TemporaryPaths();
        var (service, connection) = await ConnectAsync(paths);
        await using var _ = service;

        var published = new List<string?>();
        service.StateChanged += (_, state) => published.Add(state.ArtworkFilePath);

        connection.ReceiveBinary(ArtworkFrame(timestamp: 1_000, Jpeg(marker: 0xA1)));
        connection.ReceiveBinary(ArtworkFrame(timestamp: 2_000, Jpeg(marker: 0xB2)));
        connection.Receive("""
            {"type":"server/state","payload":{"metadata":{"title":"One","artist":"A","album":"X"}}}
            """);

        var path = published[^1];
        Assert.NotNull(path);
        Assert.Equal(Jpeg(marker: 0xB2), await File.ReadAllBytesAsync(path));
        Assert.Equal(2, Directory.GetFiles(paths.AlbumArtCacheDirectory).Length);
    }

    /// <remarks>
    /// The unsubscribe on the client path: a palette that arrives on a transport the service has
    /// let go must not reach anyone, or a stale session could recolour the window.
    /// </remarks>
    [Fact]
    public async Task AfterDisconnect_NothingIsForwarded()
    {
        using var paths = new TemporaryPaths();
        var (service, connection) = await ConnectAsync(paths);
        await using var _ = service;

        var palettes = 0;
        var frames = 0;
        service.PaletteChanged += (_, _) => palettes++;
        service.VisualizerFrameReceived += (_, _) => frames++;

        await service.DisconnectAsync();

        connection.Receive("""{"type":"server/state","payload":{"color":{"primary":[1,2,3]}}}""");
        connection.ReceiveBinary(LoudnessFrame(timestamp: 5, loudness: 100));

        Assert.Equal(0, palettes);
        Assert.Equal(0, frames);
    }

    /// <summary>
    /// A queue's next-track race, as the spec models it: track 1's metadata is in effect, then the
    /// next picture and the next metadata arrive together stamped three seconds ahead. Nothing
    /// visible changes until that moment, and then both change at once.
    /// </summary>
    /// <remarks>
    /// The clock synchroniser has a known offset and the local clock is moved by hand, so "three
    /// seconds ahead" is exact and the promotion is cranked rather than waited for. The position
    /// published for track 1 also pins the spec's formula end to end: the metadata is stamped one
    /// local second before "now", so the track is one second past its reported progress.
    /// </remarks>
    [Fact]
    public async Task ScheduledArtworkAndMetadata_AreHeldUntilTheirTimestampAndChangeTogether()
    {
        using var paths = new TemporaryPaths();
        var clock = new ManualClock(Second(10));
        var (service, connection) = await ConnectAsync(paths, clock, new OffsetClockSynchronizer(Second(5)));
        await using var _ = service;

        var published = new List<(string? Title, string? Artwork, TimeSpan Position)>();
        service.StateChanged += (_, state) => published.Add((state.Title, state.ArtworkFilePath, state.Position));

        // Server 4 s is local 9 s: one second in the past.
        connection.Receive(Metadata("One", serverMicros: Second(4), progressMs: 30_000));
        connection.ReceiveBinary(ArtworkFrame(timestamp: Second(4), Jpeg(marker: 0xA1)));
        var pathA = published[^1].Artwork;
        Assert.NotNull(pathA);
        Assert.Equal("One", published[^1].Title);
        Assert.Equal(TimeSpan.FromSeconds(31), published[^1].Position);

        // Server 8 s is local 13 s: three seconds ahead.
        var before = published.Count;
        connection.ReceiveBinary(ArtworkFrame(timestamp: Second(8), Jpeg(marker: 0xB2)));
        connection.Receive(Metadata("Two", serverMicros: Second(8), progressMs: 0));

        // The metadata carried a volume too, so a state was published — showing track 1 still.
        Assert.All(published.Skip(before), state =>
        {
            Assert.Equal("One", state.Title);
            Assert.Equal(pathA, state.Artwork);
        });

        clock.Advance(TimeSpan.FromSeconds(2.999));
        service.PromoteDue();
        Assert.Equal("One", published[^1].Title);
        Assert.Equal(pathA, published[^1].Artwork);

        clock.Advance(TimeSpan.FromSeconds(0.001));
        var beforePromotion = published.Count;
        service.PromoteDue();

        var promoted = Assert.Single(published.Skip(beforePromotion));
        Assert.Equal("Two", promoted.Title);
        Assert.NotNull(promoted.Artwork);
        Assert.NotEqual(pathA, promoted.Artwork);
        Assert.Equal(TimeSpan.Zero, promoted.Position);
    }

    /// <summary>
    /// The spec's cancel for metadata: re-sending the current state with a past or present timestamp
    /// applies it and discards the pending update.
    /// </summary>
    [Fact]
    public async Task APastMetadataAfterAScheduledOne_CancelsThePendingUpdate()
    {
        using var paths = new TemporaryPaths();
        var clock = new ManualClock(Second(10));
        var (service, connection) = await ConnectAsync(paths, clock, new OffsetClockSynchronizer(Second(5)));
        await using var _ = service;

        var titles = new List<string?>();
        service.StateChanged += (_, state) => titles.Add(state.Title);

        connection.Receive(Metadata("One", serverMicros: Second(4), progressMs: 0));
        connection.Receive(Metadata("Two", serverMicros: Second(8), progressMs: 0));
        connection.Receive(Metadata("One", serverMicros: Second(5), progressMs: 1_000));

        clock.Advance(TimeSpan.FromSeconds(10));
        service.PromoteDue();

        Assert.Equal("One", titles[^1]);
        Assert.DoesNotContain("Two", titles);
    }

    [Fact]
    public async Task Disconnecting_DiscardsPendingUpdates()
    {
        using var paths = new TemporaryPaths();
        var clock = new ManualClock(Second(10));
        var (service, connection) = await ConnectAsync(paths, clock, new OffsetClockSynchronizer(Second(5)));
        await using var _ = service;

        var published = new List<MediaSessionState>();
        service.StateChanged += (_, state) => published.Add(state);

        connection.Receive(Metadata("One", serverMicros: Second(4), progressMs: 0));
        connection.ReceiveBinary(ArtworkFrame(timestamp: Second(8), Jpeg(marker: 0xB2)));
        connection.Receive(Metadata("Two", serverMicros: Second(8), progressMs: 0));

        await service.DisconnectAsync();
        var afterDisconnect = published.Count;

        clock.Advance(TimeSpan.FromSeconds(10));
        service.PromoteDue();

        Assert.Equal(afterDisconnect, published.Count);
        Assert.Equal(MediaSessionState.Idle, published[^1]);
        Assert.Equal(MediaSessionState.Idle, service.CurrentState);
    }

    /// <summary>
    /// Only the track metadata is scheduled. Playback state, volume, mute and the command set have no
    /// audible moment of their own and apply the instant they arrive, even while metadata is held.
    /// </summary>
    [Fact]
    public async Task PlaybackStateVolumeAndCommands_ApplyImmediatelyWhileMetadataIsHeld()
    {
        using var paths = new TemporaryPaths();
        var clock = new ManualClock(Second(10));
        var (service, connection) = await ConnectAsync(paths, clock, new OffsetClockSynchronizer(Second(5)));
        await using var _ = service;

        connection.Receive(Metadata("One", serverMicros: Second(4), progressMs: 0));
        connection.Receive(Metadata("Two", serverMicros: Second(8), progressMs: 0));

        connection.Receive("""
            {"type":"server/state","payload":{"controller":{"volume":37,"muted":true,"supported_commands":["next"]}}}
            """);
        connection.Receive("""{"type":"group/update","payload":{"group_id":"g","playback_state":"paused"}}""");

        var state = service.CurrentState;
        Assert.Equal("One", state.Title);
        Assert.Equal(37, state.Volume);
        Assert.True(state.Muted);
        Assert.True(state.CanGoNext);
        Assert.False(state.CanGoPrevious);
        Assert.Equal(MediaPlaybackStatus.Paused, state.Status);
    }

    private static long Second(double seconds) => (long)(seconds * 1_000_000);

    /// <summary>
    /// A <c>server/state</c> carrying one track's metadata, stamped in server microseconds, alongside
    /// a volume so that every metadata message also publishes a state.
    /// </summary>
    private static string Metadata(string title, long serverMicros, double progressMs) =>
        JsonSerializer.Serialize(new
        {
            type = "server/state",
            payload = new
            {
                metadata = new
                {
                    timestamp = serverMicros,
                    title,
                    artist = "A",
                    album = "X",
                    progress = new { track_progress = progressMs, track_duration = 200_000, playback_speed = 1000 }
                },
                controller = new { volume = 50 }
            }
        });

    private static async Task<(SendspinPlayerService Service, RecordingConnection Connection)> ConnectAsync(
        TemporaryPaths paths,
        ManualClock? clock = null,
        IClockSynchronizer? clockSynchronizer = null)
    {
        var connection = new RecordingConnection
        {
            OnSent = (transport, json) =>
            {
                if (MessageSerializer.GetMessageType(json) == MessageTypes.ClientHello)
                {
                    transport.Receive(ServerHello);
                }
            }
        };

        var settings = new SettingsService(
            new JsonSettingsStore(paths, NullLogger<JsonSettingsStore>.Instance),
            NullLogger<SettingsService>.Instance);
        settings.Update(s =>
        {
            s.ClientId = "client-1";
            s.PlayerName = "Kitchen";
        });

        var service = new SendspinPlayerService(
            NullLoggerFactory.Instance,
            settings,
            new InMemoryStaticDelayStore(),
            new NoAudioDevices(),
            () => new RecordingAudioPlayer(),
            new ArtworkCache(paths, NullLogger<ArtworkCache>.Instance),
            new SyncCorrectionPolicy(),
            "1.0.0")
        {
            ConnectionFactory = () => connection
        };

        if (clock is not null)
        {
            service.LocalClock = () => clock.Now;
        }

        if (clockSynchronizer is not null)
        {
            service.ClockSynchronizerFactory = () => clockSynchronizer;
        }

        await service.ConnectAsync("ws://test.invalid/sendspin");

        return (service, connection);
    }

    /// <summary>A binary visualizer message: the type byte, a big-endian timestamp, the payload.</summary>
    private static byte[] Frame(byte type, long timestamp, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[9 + payload.Length];
        frame[0] = type;
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(1, 8), timestamp);
        payload.CopyTo(frame.AsSpan(9));
        return frame;
    }

    private static byte[] LoudnessFrame(long timestamp, ushort loudness)
    {
        Span<byte> payload = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, loudness);
        return Frame(BinaryMessageTypes.VisualizerLoudness, timestamp, payload);
    }

    private static byte[] BeatFrame(long timestamp, bool downbeat) =>
        Frame(BinaryMessageTypes.VisualizerBeat, timestamp, [(byte)(downbeat ? 1 : 0)]);

    private static byte[] ArtworkFrame(long timestamp, byte[] image) =>
        Frame(BinaryMessageTypes.Artwork0, timestamp, image);

    /// <summary>A JPEG signature followed by one byte that makes this picture distinct.</summary>
    private static byte[] Jpeg(byte marker) => [0xFF, 0xD8, 0xFF, marker];

    private sealed class NoAudioDevices : IAudioDeviceEnumerator
    {
        public IReadOnlyList<AudioDeviceInfo> GetDevices() => [];

        public AudioDeviceInfo? GetDefaultDevice() => null;
    }
}
