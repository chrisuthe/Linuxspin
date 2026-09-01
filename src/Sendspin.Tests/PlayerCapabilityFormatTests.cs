using Sendspin.Core.Audio;
using Sendspin.Core.Configuration;
using Sendspin.Platform.Shared.Client;
using Sendspin.SDK.Models;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for the tiered format advertisement and the order it is offered in.
/// </summary>
/// <remarks>
/// <para>
/// The ordering used to be an emergent property of two nested loops. A tier makes it load-bearing:
/// a server that takes the first format it recognises now gets a hi-res stream or a regular one
/// depending on what this list says first, so the order is pinned here rather than left to
/// whichever way the loops happen to nest.
/// </para>
/// <para>
/// The rule, in one line: <em>preferred codec first; within a codec, high rates before regular;
/// within a tier, the device's mix rate first and the rest descending.</em>
/// </para>
/// </remarks>
public sealed class PlayerCapabilityFormatTests
{
    /// <summary>A device that can only ever run at 48 kHz — the machine this was developed on.</summary>
    private static AudioDeviceInfo PinnedTo48k => new()
    {
        Id = "analog",
        Name = "Ryzen HD Audio Controller Analog Stereo",
        MixSampleRate = 48_000,
        MixChannels = 2,
        SupportedSampleRates = [48_000],
        MaxBitDepth = 24
    };

    /// <summary>The same hardware on a daemon whose clock policy permits hi-res.</summary>
    private static AudioDeviceInfo HiResCapable => new()
    {
        Id = "analog",
        Name = "Ryzen HD Audio Controller Analog Stereo",
        MixSampleRate = 48_000,
        MixChannels = 2,
        SupportedSampleRates = [48_000, 96_000, 192_000],
        MaxBitDepth = 24
    };

    private static List<AudioFormat> Formats(AudioDeviceInfo? device, string codec = AudioCodecs.Flac) =>
        [.. PlayerCapabilities.Build(
            new PlayerSettings { ClientId = "id", PlayerName = "Kitchen", PreferredCodec = codec },
            device,
            softwareVersion: "1.0.0").AudioFormats!];

    private static string Describe(AudioFormat format) =>
        $"{format.Codec}/{format.SampleRate}/{(format.BitDepth is { } d ? d.ToString() : "-")}";

    /// <summary>
    /// A device with no hi-res rate advertises exactly what it did before the tier existed.
    /// </summary>
    /// <remarks>
    /// The no-regression case, and the one this project's own hardware is in: the DAC reaches
    /// 192 kHz but the daemon pins the graph to 48 kHz, so the enumerator reports 48 kHz alone and
    /// no hi-res tier is earned. The only difference from the pre-tier advertisement is that Opus
    /// has lost its 44.1 kHz entry, which was a format the SDK's own decoder refuses to construct.
    /// </remarks>
    [Fact]
    public void DeviceWithNoHiResRate_AdvertisesTheRegularTierOnly()
    {
        Assert.Equal(
            [
                "flac/48000/16", "flac/44100/16",
                "opus/48000/-",
                "pcm/48000/16", "pcm/44100/16"
            ],
            Formats(PinnedTo48k).Select(Describe));
    }

    /// <summary>
    /// The advertisement with no device at all is the same, minus anything device-derived.
    /// </summary>
    [Fact]
    public void NoDevice_FallsBackToTheRegularTier()
    {
        Assert.Equal(
            [
                "flac/48000/16", "flac/44100/16",
                "opus/48000/-",
                "pcm/48000/16", "pcm/44100/16"
            ],
            Formats(device: null).Select(Describe));
    }

    /// <summary>
    /// A device that reports hi-res rates and depth earns the tier, offered before the floor.
    /// </summary>
    /// <remarks>
    /// This pins the whole ordering rule in one assertion: codec-major with the preferred codec
    /// first, hi-res before regular within each codec, and within each tier the mix rate first
    /// (48 000 leads the regular tier) and the rest descending (192 000 before 96 000).
    /// </remarks>
    [Fact]
    public void DeviceWithHiResSupport_OffersTheHiResTierFirst()
    {
        Assert.Equal(
            [
                "flac/192000/24", "flac/96000/24", "flac/48000/16", "flac/44100/16",
                "opus/48000/-",
                "pcm/192000/24", "pcm/96000/24", "pcm/48000/16", "pcm/44100/16"
            ],
            Formats(HiResCapable).Select(Describe));
    }

    /// <summary>
    /// The 24-bit depth is gated on the device's reported depth, independently of its rates.
    /// </summary>
    /// <remarks>
    /// Rate and depth are separately reported and a device can have one without the other. A sink
    /// that runs 96 kHz but takes only <c>S16LE</c> keeps its 96 kHz — at 16 bits, which is what it
    /// will accept. Gating the tier as a whole on depth would silently drop the rate too.
    /// </remarks>
    [Fact]
    public void HighRatesWithoutDepth_AreOfferedAt16Bit()
    {
        var device = new AudioDeviceInfo
        {
            Id = "16bit",
            Name = "16-bit only",
            MixSampleRate = 48_000,
            MixChannels = 2,
            SupportedSampleRates = [48_000, 96_000],
            MaxBitDepth = 16
        };

        Assert.Equal(
            ["flac/96000/16", "flac/48000/16", "flac/44100/16"],
            Formats(device).Where(f => f.Codec == AudioCodecs.Flac).Select(Describe));
    }

    /// <summary>
    /// A device that reported no depth at all gets no 24-bit tier.
    /// </summary>
    /// <remarks>
    /// 0 means "not reported", which is read as 16-bit. That is what keeps Windows and macOS — which
    /// do not fill this field — advertising exactly what they do today while still reporting their
    /// rates.
    /// </remarks>
    [Fact]
    public void UnreportedDepth_IsTreatedAs16Bit()
    {
        var device = new AudioDeviceInfo
        {
            Id = "unknown-depth",
            Name = "Reports rates but not depth",
            MixSampleRate = 48_000,
            MixChannels = 2,
            SupportedSampleRates = [48_000, 96_000]
        };

        Assert.All(
            Formats(device).Where(f => f.Codec != AudioCodecs.Opus),
            format => Assert.Equal(16, format.BitDepth));
    }

    /// <summary>
    /// Nothing above the device's own reported rates is ever advertised.
    /// </summary>
    /// <remarks>
    /// The two hardcoded fallbacks (48 kHz and 44.1 kHz) are deliberately still added, and both sit
    /// below the hi-res threshold — so a rate can only reach the hi-res tier by having been
    /// reported by the device. This is the assertion that stops a future change from reintroducing
    /// a hardcoded 96 000.
    /// </remarks>
    [Fact]
    public void HiResRates_ComeOnlyFromTheDeviceReport()
    {
        foreach (var device in new[] { PinnedTo48k, HiResCapable })
        {
            var advertisedHiRes = Formats(device)
                .Where(format => format.SampleRate >= 88_200)
                .Select(format => format.SampleRate)
                .Distinct();

            Assert.All(advertisedHiRes, rate => Assert.Contains(rate, device.SupportedSampleRates));
        }
    }

    /// <summary>
    /// A device listing rates it will not natively run gets no hi-res tier from them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven from the macOS semantics, which is where this went wrong in the shipped version.
    /// <c>CoreAudioDeviceEnumerator</c> filled <see cref="AudioDeviceInfo.SupportedSampleRates"/>
    /// from <c>DeviceAvailableNominalSampleRates</c> — rates the device could be <em>switched</em>
    /// to — while <c>AuhalRenderPlayer</c> never sets the nominal rate. So a Mac running at 48 kHz
    /// whose output also lists 96 kHz advertised <c>flac/96000/16</c> <em>ahead</em> of
    /// <c>flac/48000/16</c>, and CoreAudio resampled it straight back down: more bandwidth to
    /// reach a resampler, at no gain in depth.
    /// </para>
    /// <para>
    /// The enumerator is fixed to report only the nominal rate, and this pins the shared side of
    /// it: given a device shaped the way the old macOS one was, the hi-res tier must not appear.
    /// The two halves are independent, which is why both exist — this test still passes if a
    /// future enumerator reintroduces a wide list, because the tier gate no longer trusts the
    /// rate threshold alone.
    /// </para>
    /// </remarks>
    [Fact]
    public void RatesTheDeviceWillNotRunNatively_EarnNoHiResTier()
    {
        // A Mac running at 48 kHz whose output can be switched up to 192 kHz. This is the exact
        // input that used to produce a wide SupportedSampleRates; CoreAudioSampleRates now reduces
        // it to the one rate that needs no conversion.
        var native = CoreAudioSampleRates.ResolveNative(
            nominalRate: 48_000,
            [new SampleRateRange(44_100, 44_100), new SampleRateRange(48_000, 192_000)]);

        Assert.Equal([48_000], native);

        var device = new AudioDeviceInfo
        {
            Id = "mac",
            Name = "Mac running at 48 kHz, switchable to 96 kHz",
            MixSampleRate = 48_000,
            MixChannels = 2,
            SupportedSampleRates = native
        };

        // No hi-res tier, and the advertisement leads with the rate the device actually runs.
        Assert.Equal(
            ["flac/48000/16", "flac/44100/16"],
            Formats(device).Where(f => f.Codec == AudioCodecs.Flac).Select(Describe));
    }

    /// <summary>
    /// The device's current mix rate counts as native on its own.
    /// </summary>
    /// <remarks>
    /// The complement of the test above, and the reason the gate is not simply "member of
    /// SupportedSampleRates". A device running at 96 kHz is running there whether or not its
    /// enumerator also listed that rate, so the tier must not be withheld from it. Every current
    /// enumerator does include its mix rate in that list; this pins the behaviour for one that
    /// does not, rather than resting on the coincidence.
    /// </remarks>
    [Fact]
    public void TheMixRateCountsAsNativeEvenIfTheListOmitsIt()
    {
        var device = new AudioDeviceInfo
        {
            Id = "96k",
            Name = "Running at 96 kHz, reports no list",
            MixSampleRate = 96_000,
            MixChannels = 2,
            SupportedSampleRates = [],
            MaxBitDepth = 24
        };

        Assert.Equal(
            ["flac/96000/24", "flac/48000/16", "flac/44100/16"],
            Formats(device).Where(f => f.Codec == AudioCodecs.Flac).Select(Describe));
    }

    /// <summary>
    /// Opus is never offered at a rate its decoder rejects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SDK's <c>OpusDecoder</c> throws <c>ArgumentException("Sample rate is invalid (must be
    /// 8/12/16/24/48 Khz)")</c> on construction for anything else. This player used to advertise
    /// <c>opus/44100</c> on every platform: a server that picked it got a decoder that threw before
    /// the first sample, which is a dead stream rather than a degraded one.
    /// </para>
    /// <para>
    /// Asserted against the hi-res device too, because that is where a naive tiering would offer
    /// <c>opus/96000</c> and <c>opus/192000</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Opus_IsOnlyOfferedAtRatesItsDecoderAccepts()
    {
        int[] decodable = [8_000, 12_000, 16_000, 24_000, 48_000];

        foreach (var device in new[] { PinnedTo48k, HiResCapable, null })
        {
            var opus = Formats(device).Where(format => format.Codec == AudioCodecs.Opus).ToList();

            Assert.NotEmpty(opus);
            Assert.All(opus, format => Assert.Contains(format.SampleRate, decodable));

            // And it carries no depth: it is a lossy transform codec, not a PCM container.
            Assert.All(opus, format => Assert.Null(format.BitDepth));
        }
    }

    /// <summary>
    /// The preferred codec leads, and the tier order inside it is unaffected by which one it is.
    /// </summary>
    [Fact]
    public void PreferredCodec_LeadsWithoutDisturbingTheTierOrder()
    {
        Assert.Equal(
            [
                "pcm/192000/24", "pcm/96000/24", "pcm/48000/16", "pcm/44100/16",
                "flac/192000/24", "flac/96000/24", "flac/48000/16", "flac/44100/16",
                "opus/48000/-"
            ],
            Formats(HiResCapable, AudioCodecs.Pcm).Select(Describe));
    }

    /// <summary>
    /// The device's mix rate leads its own tier even when it is not the highest rate there.
    /// </summary>
    /// <remarks>
    /// The mix rate is the one rate that needs no renegotiation — on PipeWire the whole graph is
    /// clocked at it. So within a tier it is promoted ahead of higher rates, which is the one place
    /// the ordering is not simply descending.
    /// </remarks>
    [Fact]
    public void MixRate_LeadsItsOwnTier()
    {
        var device = new AudioDeviceInfo
        {
            Id = "96k-default",
            Name = "Running at 96 kHz",
            MixSampleRate = 96_000,
            MixChannels = 2,
            SupportedSampleRates = [48_000, 96_000, 192_000],
            MaxBitDepth = 24
        };

        Assert.Equal(
            ["flac/96000/24", "flac/192000/24", "flac/48000/16", "flac/44100/16"],
            Formats(device).Where(f => f.Codec == AudioCodecs.Flac).Select(Describe));
    }

    /// <summary>
    /// A mono device is advertised as mono, and anything wider is clamped to stereo.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(6, 2)]
    [InlineData(0, 2)]
    public void Channels_FollowTheDeviceClampedToStereo(int reported, int expected)
    {
        var device = new AudioDeviceInfo
        {
            Id = "channels",
            Name = "Channels",
            MixSampleRate = 48_000,
            MixChannels = reported,
            SupportedSampleRates = [48_000]
        };

        Assert.All(Formats(device), format => Assert.Equal(expected, format.Channels));
    }
}
