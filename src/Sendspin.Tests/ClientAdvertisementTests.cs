using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.Core.Audio;
using Sendspin.Core.Configuration;
using Sendspin.Core.Platform;
using Sendspin.Platform.Shared.Client;
using Sendspin.Platform.Shared.Media;
using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests the promises this client makes on the wire, against what it actually does.
/// </summary>
/// <remarks>
/// <para>
/// <c>client/hello</c> and <c>client/state</c> are promises, and a server believes them. When one
/// is wrong the failure is silent on this side: the role-versioning bug was a healthy connection
/// with no audio, and nothing logged an error. So these assert the <em>wire</em> form rather than
/// the <see cref="ClientCapabilities"/> object it is built from — the SDK's translation between
/// the two is private, and it is the JSON a server reads.
/// </para>
/// <para>
/// <see cref="PlayerCapabilitiesTests"/> covers the two promises already pinned — the versioned
/// role names and <c>buffer_capacity</c> — against the capabilities object, and the advertised
/// formats belong to a separate change. Neither is repeated here.
/// </para>
/// </remarks>
public sealed class ClientAdvertisementTests
{
    /// <summary>
    /// Every command this client advertises, paired with the proof that it is really handled.
    /// </summary>
    /// <remarks>
    /// This table is the tie the acceptance criteria ask for. Advertising a command the client
    /// does not honour is the same class of defect as the bare role names: the server sends it,
    /// the client answers with an acknowledgement it did not earn, and the user's control does
    /// nothing. So the advertised list is not asserted against a literal — it is asserted against
    /// the set of commands something here demonstrates end to end, and every entry is run.
    /// </remarks>
    private static readonly Dictionary<string, Func<Session, Task>> HandledCommands = new(StringComparer.Ordinal)
    {
        [Commands.Volume] = async session =>
        {
            await session.SendServerCommandAsync("""{"command":"volume","volume":17}""");

            // Reached the output, not merely the acknowledgement: a client that echoes a volume
            // it never applied looks correct to the server and is silent to the user.
            Assert.Equal(17, session.Pipeline.Volumes[^1]);
            Assert.Equal(17, session.PlayerState.GetProperty("volume").GetInt32());
        },

        [Commands.Mute] = async session =>
        {
            // The session starts unmuted, so this is a change rather than a value that was
            // already there.
            Assert.False(session.Pipeline.Mutes[^1]);

            await session.SendServerCommandAsync("""{"command":"mute","mute":true}""");

            Assert.True(session.Pipeline.Mutes[^1]);
            Assert.True(session.PlayerState.GetProperty("muted").GetBoolean());
        },

        [Commands.SetStaticDelay] = async session =>
        {
            await session.SendServerCommandAsync("""{"command":"set_static_delay","static_delay_ms":275}""");

            // Three separate obligations, and the advertisement claims all three: apply it to
            // timing, persist it so it survives a restart, and report it back.
            Assert.Equal(275.0, session.Clock.StaticDelayMs);
            Assert.Equal(275.0, session.Delays.Load());
            Assert.Equal(275.0, session.PlayerState.GetProperty("static_delay_ms").GetDouble());
        }
    };

    public static TheoryData<string> AdvertisedCommandNames => [.. HandledCommands.Keys];

    /// <summary>
    /// The advertised command list and the commands proven to work must be the same set.
    /// </summary>
    /// <remarks>
    /// Both directions matter. Advertising something unproven fails here; proving something the
    /// client never advertises fails here too, because a server will not send a command it was
    /// not offered and the proof would be of dead code.
    /// </remarks>
    [Fact]
    public async Task AdvertisedCommands_AreExactlyTheOnesProvenToBeHandled()
    {
        await using var session = await Session.OpenAsync();

        Assert.Equal(HandledCommands.Keys.Order(), session.AdvertisedCommands.Order());
    }

    [Theory]
    [MemberData(nameof(AdvertisedCommandNames))]
    public async Task EveryAdvertisedCommand_IsHandledAndTakesEffect(string command)
    {
        await using var session = await Session.OpenAsync();

        await HandledCommands[command](session);
    }

    /// <summary>
    /// Applying a command obliges the client to send a fresh <c>client/state</c>.
    /// </summary>
    /// <remarks>
    /// The spec requires the acknowledgement so the server can recalculate the group average from
    /// what players actually applied. Without it a controller's slider snaps back, because the
    /// server never learns the change landed.
    /// </remarks>
    [Fact]
    public async Task ApplyingACommand_AcknowledgesWithAFreshClientState()
    {
        await using var session = await Session.OpenAsync();
        var before = session.Connection.SentOfType(MessageTypes.ClientState).Count;

        await session.SendServerCommandAsync("""{"command":"volume","volume":31}""");

        Assert.Equal(before + 1, session.Connection.SentOfType(MessageTypes.ClientState).Count);
    }

    /// <summary>
    /// The timing figures this build was built with have to survive the trip to the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted against the capabilities object rather than against the constants, so this is a
    /// claim about the SDK carrying our numbers rather than a constant equalling itself. An SDK
    /// that dropped, renamed or defaulted either field would fail here — and the failure that
    /// prevents is a server scheduling the first chunk too early, which truncates the opening of
    /// every track.
    /// </para>
    /// <para>
    /// <strong>What this does not cover.</strong> Whether 350 ms is the <em>right</em> lead time
    /// is not verifiable from here and is not asserted. The figure is a fixed constant rather than
    /// a measurement of this pipeline, which is a known shortfall recorded in
    /// <c>docs/COMPLIANCE.md</c>: it has to be advertised in <c>client/hello</c>, sent before any
    /// audio has flowed and so before the pipeline can report a latency. There is no real
    /// implementation to compare it against, so nothing weaker is asserted in place of one.
    /// <see cref="MinBuffer_IsTheSameFigureTheDecodedBufferIsAskedToHold"/> is the other figure,
    /// which does have one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Timing_PutsTheLeadTimeAndBufferFloorItWasBuiltWithOnTheWire()
    {
        await using var session = await Session.OpenAsync();
        var player = session.PlayerState;

        Assert.Equal(
            session.Capabilities.RequiredLeadTimeMs,
            player.GetProperty("required_lead_time_ms").GetInt32());

        Assert.Equal(
            session.Capabilities.MinBufferMs,
            player.GetProperty("min_buffer_ms").GetInt32());

        // Serialized unconditionally for a player, so absence is a defect rather than a default.
        Assert.True(player.GetProperty("required_lead_time_ms").GetInt32() > 0);
    }

    /// <summary>
    /// The buffer floor promised to the server and the one the decoded buffer is asked to hold
    /// are one figure, not two that happen to match today.
    /// </summary>
    /// <remarks>
    /// <see cref="PlayerCapabilities.DefaultMinBufferMs"/> has two uses — it is advertised as
    /// <c>min_buffer_ms</c>, and it is the <c>TargetBufferMilliseconds</c> the pipeline configures
    /// — and drift between them is invisible from both ends: the server holds back to a floor the
    /// player is not waiting for, or the player waits for one the server was never told about.
    /// So the figure is taken off the wire and compared against a buffer built by the production
    /// factory, rather than both being restated here.
    /// </remarks>
    [Fact]
    public async Task MinBuffer_IsTheSameFigureTheDecodedBufferIsAskedToHold()
    {
        await using var session = await Session.OpenAsync();
        var advertised = session.PlayerState.GetProperty("min_buffer_ms").GetDouble();

        using var buffer = SendspinPlayerService.CreateDecodedBuffer(
            new AudioFormat { Codec = AudioCodecs.Pcm, SampleRate = 48_000, Channels = 2, BitDepth = 16 },
            new ConvergedClockSynchronizer(),
            new SyncCorrectionPolicy().ToSdkOptions());

        Assert.Equal(advertised, buffer.TargetBufferMilliseconds);
    }

    /// <summary>
    /// A volume outside the range the spec allows must not reach the server.
    /// </summary>
    /// <remarks>
    /// The settings file is editable and survives version changes, so an out-of-range value is
    /// reachable without a bug anywhere in this code. The clamp is in
    /// <see cref="PlayerCapabilities.Build"/>; this is the proof that nothing downstream undoes it.
    /// </remarks>
    [Theory]
    [InlineData(250, 100)]
    [InlineData(-20, 0)]
    public async Task InitialVolume_IsClampedIntoTheRangeTheSpecAllows(int persisted, int expected)
    {
        await using var session = await Session.OpenAsync(Settings(volume: persisted));

        Assert.Equal(expected, session.PlayerState.GetProperty("volume").GetInt32());
    }

    /// <summary>
    /// The identity the server files this player under is the persisted one.
    /// </summary>
    /// <remarks>
    /// That the id is stable across restarts is pinned by
    /// <see cref="SettingsPersistenceTests.ClientId_IsGeneratedOncePersistedAndPlatformNeutral"/>;
    /// this is the other half — the stable id actually reaching <c>client/hello</c>. A server keys
    /// group membership on it, so a client that sent something else would rejoin as a new device
    /// every time.
    /// </remarks>
    [Fact]
    public async Task Identity_OnTheWireIsThePersistedIdentity()
    {
        await using var session = await Session.OpenAsync();
        var hello = session.Hello;

        Assert.Equal(Session.ClientId, hello.GetProperty("client_id").GetString());
        Assert.Equal(Session.PlayerName, hello.GetProperty("name").GetString());
        Assert.Equal(
            Session.SoftwareVersion,
            hello.GetProperty("device_info").GetProperty("software_version").GetString());
    }

    /// <summary>
    /// Artwork channels are only meaningful alongside the role that carries them.
    /// </summary>
    /// <remarks>
    /// The SDK's documented opt-out is to drop <c>artwork@v1</c> from the roles, which leaves the
    /// channel advertisement behind as a claim about a role the client no longer plays. That is the
    /// same shape as the role-versioning bug — an advertisement the server acts on and the client
    /// cannot honour. Both halves are asserted rather than one being made conditional on the other,
    /// so opting out of artwork has to be a deliberate edit here rather than something that
    /// quietly leaves this passing on nothing.
    /// </remarks>
    [Fact]
    public async Task Artwork_ChannelsAreAdvertisedAlongsideTheArtworkRole()
    {
        await using var session = await Session.OpenAsync();
        var hello = session.Hello;

        Assert.True(hello.TryGetProperty("artwork@v1_support", out var artwork));
        Assert.NotEmpty(artwork.GetProperty("channels").EnumerateArray());

        var roles = hello.GetProperty("supported_roles").EnumerateArray().Select(r => r.GetString());
        Assert.Contains($"{ClientRoles.Artwork}@v1", roles);
    }

    /// <summary>
    /// The living backdrop's two roles, and the support object the visualizer one needs.
    /// </summary>
    /// <remarks>
    /// The same shape as the artwork pin above, in the other direction: the SDK emits
    /// <c>visualizer@v1_support</c> whenever the support object is set, and a support object
    /// for a role that is not listed is the non-compliant hello the role-versioning bug produced.
    /// So the wire form is asserted to carry both halves, and to ask for exactly the features
    /// the backdrop renders.
    /// </remarks>
    [Fact]
    public async Task Visualizer_SupportIsAdvertisedAlongsideTheVisualizerAndColorRoles()
    {
        await using var session = await Session.OpenAsync();
        var hello = session.Hello;

        var roles = hello.GetProperty("supported_roles").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("color@v1", roles);
        Assert.Contains("visualizer@v1", roles);

        Assert.True(hello.TryGetProperty("visualizer@v1_support", out var support));
        Assert.Equal(["loudness", "beat"], support.GetProperty("types").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(30, support.GetProperty("rate_max").GetInt32());
        Assert.Equal(4096, support.GetProperty("buffer_capacity").GetInt32());
        Assert.False(support.TryGetProperty("spectrum", out _));
    }

    /// <summary>
    /// The channel source advertised has to be one the protocol defines.
    /// </summary>
    /// <remarks>
    /// Asserted against the SDK's own constant rather than the string, so a spec rename surfaces
    /// here rather than as a server quietly streaming nothing.
    /// </remarks>
    [Fact]
    public async Task Artwork_AdvertisesASourceTheProtocolDefines()
    {
        await using var session = await Session.OpenAsync();

        Assert.Equal(ArtworkSources.Album, session.ArtworkChannel.GetProperty("source").GetString());
    }

    /// <summary>
    /// Artwork arriving in the advertised format must be stored as that format.
    /// </summary>
    /// <remarks>
    /// The advertised <c>format</c> is what the server encodes to, and the extension
    /// <see cref="ArtworkCache"/> chooses is what a shell uses to decode it. The two disagreeing
    /// produces a picture that renders on one desktop and not another — so the extension is
    /// derived from the advertisement here rather than hard-coded, and the cache is given real
    /// bytes of the advertised format.
    /// </remarks>
    [Fact]
    public async Task Artwork_InTheAdvertisedFormatIsStoredUnderThatFormatsExtension()
    {
        await using var session = await Session.OpenAsync();
        var format = session.ArtworkChannel.GetProperty("format").GetString();

        using var paths = new TemporaryPaths();
        var cache = new ArtworkCache(paths, NullLogger<ArtworkCache>.Instance);

        var path = cache.Write(JpegBytes(width: 512, height: 512));

        Assert.NotNull(path);
        Assert.Equal(ExtensionFor(format), Path.GetExtension(path));
    }

    /// <summary>
    /// Nothing enforces the advertised dimensions, and this records that rather than hiding it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>media_width</c> and <c>media_height</c> are advertised as 512x512, but the artwork path
    /// never decodes an image: <see cref="ArtworkCache"/> sniffs magic bytes for an extension and
    /// writes the payload through untouched, and <c>SendspinPlayerService</c> hands it whatever
    /// arrived. So the figures are a request to the server, not a limit this client applies.
    /// </para>
    /// <para>
    /// That is a real gap and it is asserted in the honest direction — an oversized image is
    /// stored whole — rather than asserted away with something weaker. It is tolerable because
    /// the consequence is a larger file in a cache that already prunes itself, and because
    /// enforcement would mean decoding and re-encoding every image to save nothing the server was
    /// not already willing to send. If enforcement is ever added, this test fails and whoever
    /// adds it has to decide what the advertisement should then say.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Artwork_LargerThanTheAdvertisedDimensionsIsStoredUnchanged()
    {
        await using var session = await Session.OpenAsync();
        var channel = session.ArtworkChannel;
        var advertisedWidth = channel.GetProperty("media_width").GetInt32();
        var advertisedHeight = channel.GetProperty("media_height").GetInt32();

        var oversized = JpegBytes(width: advertisedWidth * 2, height: advertisedHeight * 2);

        using var paths = new TemporaryPaths();
        var cache = new ArtworkCache(paths, NullLogger<ArtworkCache>.Instance);

        var path = cache.Write(oversized);

        Assert.NotNull(path);
        Assert.Equal(oversized, await File.ReadAllBytesAsync(path));
    }

    /// <summary>
    /// The file extension a shell needs for a protocol format name.
    /// </summary>
    private static string ExtensionFor(string? format) => format switch
    {
        "jpeg" or "jpg" => ".jpg",
        "png" => ".png",
        "webp" => ".webp",
        _ => throw new InvalidOperationException($"Advertised artwork format '{format}' has no known extension")
    };

    /// <summary>
    /// JPEG headers declaring the given dimensions, followed by filler standing in for scan data.
    /// </summary>
    /// <remarks>
    /// The SOI, APP0 and SOF0 segments are well formed, so the dimensions really are declared where
    /// a decoder would read them. The scan data is not, because nothing in the artwork path decodes
    /// an image — which is the point
    /// <see cref="Artwork_LargerThanTheAdvertisedDimensionsIsStoredUnchanged"/> makes.
    /// </remarks>
    private static byte[] JpegBytes(int width, int height)
    {
        byte[] headers =
        [
            0xFF, 0xD8,                                             // SOI
            0xFF, 0xE0, 0x00, 0x10,                                 // APP0, 16 bytes
            0x4A, 0x46, 0x49, 0x46, 0x00,                           // "JFIF\0"
            0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,   // version, density, no thumbnail
            0xFF, 0xC0, 0x00, 0x0B, 0x08,                           // SOF0, 11 bytes, 8-bit samples
            (byte)(height >> 8), (byte)height,
            (byte)(width >> 8), (byte)width,
            0x01, 0x01, 0x11, 0x00                                  // one component, 1x1 sampling
        ];

        return [.. headers, .. new byte[256]];
    }

    private static PlayerSettings Settings(int volume = 42) => new()
    {
        ClientId = Session.ClientId,
        PlayerName = Session.PlayerName,
        Volume = volume,
        Muted = false
    };

    /// <summary>
    /// A real <see cref="SendspinClientService"/> talking to a recording transport, taken through
    /// the handshake so that both advertisements have actually been sent.
    /// </summary>
    /// <remarks>
    /// The SDK's <c>ConnectAsync</c> does not return until a <c>server/hello</c> arrives, so the
    /// answer has to come from inside the send rather than after it — hence
    /// <see cref="RecordingConnection.OnSent"/>.
    /// </remarks>
    private sealed class Session : IAsyncDisposable
    {
        public const string ClientId = "client-1";
        public const string PlayerName = "Kitchen";
        public const string SoftwareVersion = "1.0.0";

        private const string ServerHello = """
            {"type":"server/hello","payload":{"server_id":"srv","name":"Test","version":1,
            "supported_roles":["player@v1","controller@v1","metadata@v1","artwork@v1","color@v1","visualizer@v1"],
            "active_roles":["player@v1","controller@v1","metadata@v1","artwork@v1","color@v1","visualizer@v1"]}}
            """;

        private readonly List<JsonDocument> _parsed = [];
        private readonly SendspinClientService _client;

        private Session(
            RecordingConnection connection,
            SendspinClientService client,
            ClientCapabilities capabilities,
            InertAudioPipeline pipeline,
            ConvergedClockSynchronizer clock,
            InMemoryStaticDelayStore delays)
        {
            Connection = connection;
            _client = client;
            Capabilities = capabilities;
            Pipeline = pipeline;
            Clock = clock;
            Delays = delays;
        }

        public RecordingConnection Connection { get; }

        public ClientCapabilities Capabilities { get; }

        public InertAudioPipeline Pipeline { get; }

        public ConvergedClockSynchronizer Clock { get; }

        public InMemoryStaticDelayStore Delays { get; }

        /// <summary>The <c>client/hello</c> payload, as the server would parse it.</summary>
        public JsonElement Hello => Payload(Connection.SentOfType(MessageTypes.ClientHello)[0]);

        /// <summary>The most recent <c>client/state</c> player object.</summary>
        public JsonElement PlayerState =>
            Payload(Connection.SentOfType(MessageTypes.ClientState)[^1]).GetProperty("player");

        /// <summary>The single artwork channel advertised in <c>client/hello</c>.</summary>
        public JsonElement ArtworkChannel =>
            Hello.GetProperty("artwork@v1_support").GetProperty("channels").EnumerateArray().First();

        /// <summary>
        /// Every command the client advertises, across both messages it advertises them in.
        /// </summary>
        /// <remarks>
        /// The two lists are disjoint and neither is the whole promise: <c>client/hello</c> carries
        /// the always-available <c>volume</c> and <c>mute</c>, and <c>client/state</c> carries the
        /// optional extras the SDK derives from <c>SupportsSetStaticDelay</c>. A server reads both,
        /// so the union is what was actually offered.
        /// </remarks>
        public IEnumerable<string> AdvertisedCommands =>
            Names(Hello.GetProperty("player@v1_support"))
                .Concat(Names(PlayerState))
                .Distinct(StringComparer.Ordinal);

        public static async Task<Session> OpenAsync(PlayerSettings? settings = null)
        {
            var pipeline = new InertAudioPipeline();
            var clock = new ConvergedClockSynchronizer();
            var delays = new InMemoryStaticDelayStore();

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

            var capabilities = PlayerCapabilities.Build(
                settings ?? Settings(),
                device: null,
                softwareVersion: SoftwareVersion);

            var client = new SendspinClientService(
                NullLogger<SendspinClientService>.Instance,
                connection,
                clock,
                capabilities,
                pipeline,
                delays);

            var session = new Session(connection, client, capabilities, pipeline, clock, delays);

            await client.ConnectAsync(new Uri("ws://test.invalid/sendspin"));
            await session.WaitForClientStatesAsync(1);

            return session;
        }

        /// <summary>
        /// Delivers a <c>server/command</c> carrying the given player object, and waits for the
        /// acknowledgement the spec requires so the assertion runs against a settled client.
        /// </summary>
        public async Task SendServerCommandAsync(string playerJson)
        {
            var before = Connection.SentOfType(MessageTypes.ClientState).Count;

            Connection.Receive(
                """{"type":"server/command","payload":{"player":""" + playerJson + "}}");

            await WaitForClientStatesAsync(before + 1);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var document in _parsed)
            {
                document.Dispose();
            }

            await _client.DisposeAsync();
        }

        private static IEnumerable<string> Names(JsonElement owner) =>
            owner.TryGetProperty("supported_commands", out var commands)
                ? commands.EnumerateArray().Select(c => c.GetString()!)
                : [];

        /// <summary>
        /// Waits for the client to have sent <paramref name="count"/> <c>client/state</c> messages.
        /// </summary>
        /// <remarks>
        /// The SDK answers an inbound frame on its own receive path, so there is nothing to await.
        /// Polling the acknowledgement is what makes that observable without a fixed sleep.
        /// </remarks>
        private async Task WaitForClientStatesAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (Connection.SentOfType(MessageTypes.ClientState).Count < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"No client/state number {count} was sent");
                await Task.Delay(10);
            }
        }

        private JsonElement Payload(string json)
        {
            var document = JsonDocument.Parse(json);
            _parsed.Add(document);

            return document.RootElement.GetProperty("payload");
        }
    }
}
