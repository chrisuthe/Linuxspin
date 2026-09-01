using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.Core.Audio;
using Sendspin.Core.Configuration;
using Sendspin.Platform.Shared.Client;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Proves that every format <see cref="PlayerCapabilities.Build"/> advertises can actually be
/// decoded, by constructing the SDK's real decoder for it.
/// </summary>
/// <remarks>
/// <para>
/// <c>client/hello</c> is a promise. Three separate bugs in this client were the same shape: it
/// advertised a capability it did not have, the server took it at its word, and the failure was
/// silent — no exception here, nothing in the log, just no audio. <c>opus/44100</c> was the
/// clearest: <c>OpusDecoder</c> rejects that rate <em>on construction</em>, so a server that
/// picked it got a decoder that threw before the first sample.
/// </para>
/// <para>
/// <strong>Why this is separate from <see cref="PlayerCapabilityFormatTests"/>.</strong> That file
/// asserts what the list <em>says</em> — membership, tiering, order — against expectations written
/// out by hand. This file asserts the list is <em>keepable</em>, and it is the only place in the
/// suite that puts the SDK's real decoders behind the assertion. The distinction matters: a test
/// that restates the decoder's rule cannot fail when the decoder changes its mind, which is
/// precisely how the <c>opus/44100</c> bug would have survived a green suite.
/// </para>
/// <para>
/// The factory here is <see cref="AudioDecoderFactory"/> — the same one
/// <c>SendspinPlayerService.CreatePipeline</c> hands to <c>AudioPipeline</c>. Nothing is faked,
/// stubbed or restated; if the SDK narrows what it will decode, this fails.
/// </para>
/// </remarks>
public sealed class AdvertisedFormatDecoderTests
{
    /// <summary>
    /// The device shapes the advertisement is driven across, keyed by the name the theory takes.
    /// </summary>
    /// <remarks>
    /// A shape earns its place by reaching formats the others do not. The 44.1 kHz family is not a
    /// duplicate of the 48 kHz one — it is the other hi-res lineage, and it is the only shape that
    /// reaches <c>176400</c>. The low-rate shape is the only one that advertises Opus anywhere but
    /// 48 kHz: <c>PlayerCapabilities</c> permits five Opus rates and hardware reporting alone
    /// decides which are ever offered, so without it four of the five are asserted by nobody.
    /// </remarks>
    /// <summary>
    /// Every shape name <see cref="Shape"/> knows, for the cases that sweep all of them.
    /// </summary>
    /// <remarks>
    /// The <c>[InlineData]</c> lists below have to repeat these as literals, because attribute
    /// arguments must be compile-time constants. That is a drift risk of exactly the kind this file
    /// exists to rule out — a shape added here and missed in a list would narrow the sweep silently
    /// — so <see cref="EveryShape_IsCoveredByEveryTheory"/> holds the two in agreement.
    /// </remarks>
    private static readonly string[] Shapes = ["none", "48k-only", "hi-res", "44k1-family", "low-rate"];

    private static AudioDeviceInfo? Shape(string name) => name switch
    {
        "none" => null,

        "48k-only" => Device("analog", 48_000, [48_000], 24),

        "hi-res" => Device("hi-res", 48_000, [48_000, 96_000, 192_000], 24),

        "44k1-family" => Device("44k1", 44_100, [44_100, 88_200, 176_400], 24),

        "low-rate" => Device("low-rate", 24_000, [24_000, 48_000], 16),

        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown device shape.")
    };

    private static AudioDeviceInfo Device(string id, int mixRate, int[] rates, int depth) => new()
    {
        Id = id,
        Name = id,
        MixSampleRate = mixRate,
        MixChannels = 2,
        SupportedSampleRates = rates,
        MaxBitDepth = depth
    };

    /// <summary>
    /// Everything <see cref="PlayerCapabilities.Build"/> can advertise for one device shape.
    /// </summary>
    /// <remarks>
    /// Swept across every preferred codec, because that is the one setting that changes the
    /// advertisement. It reorders rather than re-populates today — but "today" is an implementation
    /// detail of <c>Build</c>, and this test's claim is about <em>every</em> format it advertises,
    /// so the sweep is what makes the claim literally true rather than true by inspection.
    /// </remarks>
    private static IEnumerable<AudioFormat> Advertised(AudioDeviceInfo? device) =>
        PlayerCapabilities.SupportedCodecs
            .SelectMany(preferred => PlayerCapabilities.Build(
                new PlayerSettings { ClientId = "id", PlayerName = "Kitchen", PreferredCodec = preferred },
                device,
                softwareVersion: "1.0.0").AudioFormats!)
            .DistinctBy(format => (format.Codec, format.SampleRate, format.BitDepth, format.Channels));

    private static string Describe(AudioFormat format) =>
        $"{format.Codec}/{format.SampleRate}/{(format.BitDepth is { } d ? d.ToString() : "-")}";

    /// <summary>
    /// Every advertised format constructs its real decoder.
    /// </summary>
    /// <remarks>
    /// Failures are collected rather than thrown on first sight, so a run names every unkeepable
    /// format at once instead of hiding the rest behind whichever came first.
    /// </remarks>
    [Theory]
    [InlineData("none")]
    [InlineData("48k-only")]
    [InlineData("hi-res")]
    [InlineData("44k1-family")]
    [InlineData("low-rate")]
    public void EveryAdvertisedFormat_ConstructsTheRealDecoder(string shape)
    {
        var factory = new AudioDecoderFactory(NullLoggerFactory.Instance);
        var advertised = Advertised(Shape(shape)).ToList();

        Assert.NotEmpty(advertised);

        var unkeepable = new List<string>();

        foreach (var format in advertised)
        {
            try
            {
                using var decoder = factory.Create(format);

                // A constructed decoder that budgets no samples per frame is not a working one.
                // This is the cheapest assertion that the format was really configured for, rather
                // than accepted and left inert.
                if (decoder.MaxSamplesPerFrame <= 0)
                {
                    unkeepable.Add(
                        $"{Describe(format)}: constructed but MaxSamplesPerFrame is "
                        + decoder.MaxSamplesPerFrame.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                unkeepable.Add($"{Describe(format)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(
            unkeepable.Count == 0,
            $"Device shape '{shape}': {unkeepable.Count} of the {advertised.Count} advertised "
            + "format(s) cannot construct a decoder. client/hello would be promising a stream this "
            + "client cannot play:\n  "
            + string.Join("\n  ", unkeepable));
    }

    /// <summary>
    /// Nothing the advertisement offers carries a <c>codec_header</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This asserts a property of <see cref="PlayerCapabilities.Build"/>, not of the SDK, and it
    /// exists to keep the sweep above honest rather than to catch a decoder regression. Its job is
    /// to pin the sweep's <em>input</em>: capabilities go out in <c>client/hello</c> before any
    /// stream exists, so a real <see cref="AudioFormat.CodecHeader"/> is not available to send, and
    /// the formats fed to the decoder above must match that. Were someone later to make a stubborn
    /// codec construct by handing the sweep a synthetic header, the sweep would go green while
    /// proving decodability of a format the client never actually advertises. This fails first.
    /// </para>
    /// <para>
    /// FLAC is the only advertised codec with a header concept — <c>CodecHeader</c> carries its
    /// STREAMINFO block — and it constructs without one because <c>FlacDecoder</c> synthesises the
    /// block from the format's own rate, depth and channels. That is the SDK behaviour the sweep
    /// above relies on, and the sweep is what fails if it ever stops holding.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("none")]
    [InlineData("48k-only")]
    [InlineData("hi-res")]
    [InlineData("44k1-family")]
    [InlineData("low-rate")]
    public void AdvertisedFormats_CarryNoCodecHeader(string shape)
    {
        Assert.All(
            Advertised(Shape(shape)),
            format => Assert.Null(format.CodecHeader));
    }

    /// <summary>
    /// The decoder really does refuse the format this whole file exists because of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A test that only ever asserts success cannot distinguish "every format is decodable" from
    /// "nothing is being checked". This is the negative control: <c>opus/44100</c> is the exact
    /// format the client used to advertise, and it must still throw on construction for the sweep
    /// above to mean anything.
    /// </para>
    /// <para>
    /// It also keeps the proof in the repository. Reintroducing <c>opus/44100</c> into
    /// <c>PlayerCapabilities</c> by hand to watch the sweep go red proves the mechanism once, for
    /// whoever ran it; this proves it on every run.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheFormatThatCausedThisBug_StillFailsToConstruct()
    {
        var factory = new AudioDecoderFactory(NullLoggerFactory.Instance);

        var ex = Assert.Throws<ArgumentException>(() => factory.Create(
            new AudioFormat { Codec = AudioCodecs.Opus, SampleRate = 44_100, Channels = 2 }));

        Assert.Contains("Sample rate", ex.Message, StringComparison.OrdinalIgnoreCase);

        foreach (var shape in Shapes)
        {
            Assert.DoesNotContain(
                Advertised(Shape(shape)),
                format => format.Codec == AudioCodecs.Opus && format.SampleRate == 44_100);
        }
    }

    /// <summary>
    /// Every theory in this file runs over every shape in <see cref="Shapes"/>.
    /// </summary>
    /// <remarks>
    /// The one assertion here that is about the tests rather than the client. <c>[InlineData]</c>
    /// cannot be driven from an array, so each theory repeats the shape names by hand; without this
    /// check, adding a sixth shape and forgetting one list would leave that theory quietly covering
    /// less than it appears to. Reflection over the attributes is the only way to notice, and a
    /// sweep whose breadth is its whole value should not be trusted to a copy-paste.
    /// </remarks>
    [Fact]
    public void EveryShape_IsCoveredByEveryTheory()
    {
        var theories = typeof(AdvertisedFormatDecoderTests)
            .GetMethods()
            .Where(method => method.GetCustomAttributes(typeof(TheoryAttribute), inherit: false).Length > 0)
            .ToList();

        Assert.NotEmpty(theories);

        foreach (var theory in theories)
        {
            var covered = theory
                .GetCustomAttributes(typeof(InlineDataAttribute), inherit: false)
                .Cast<InlineDataAttribute>()
                .Select(data => (string)data.GetData(theory).Single()[0]!)
                .ToList();

            Assert.Equal(Shapes.Order(), covered.Order());
        }
    }
}
