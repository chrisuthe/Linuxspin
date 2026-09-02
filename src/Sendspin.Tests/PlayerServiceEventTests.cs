using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.Core.Audio;
using Sendspin.Core.Configuration;
using Sendspin.Platform.Shared.Client;
using Sendspin.Platform.Shared.Media;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests that the player service forwards the palette and the visualizer frames a session
/// delivers, as it does the artwork.
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

    private static async Task<(SendspinPlayerService Service, RecordingConnection Connection)> ConnectAsync(TemporaryPaths paths)
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

    private sealed class NoAudioDevices : IAudioDeviceEnumerator
    {
        public IReadOnlyList<AudioDeviceInfo> GetDevices() => [];

        public AudioDeviceInfo? GetDefaultDevice() => null;
    }
}
