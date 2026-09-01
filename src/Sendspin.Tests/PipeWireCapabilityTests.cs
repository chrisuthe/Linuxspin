using Sendspin.Core.Audio;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for reading real device capability out of a <c>pw-dump</c> document.
/// </summary>
/// <remarks>
/// <para>
/// Every fixture here is the shape <c>pw-dump</c> actually emits, taken from a live PipeWire
/// 1.6.8 daemon rather than from the documentation — including the two details that are easy to
/// get wrong and that the parser exists to handle: a node's <c>rate</c> is normally a
/// <em>range</em> ({default, min, max}) rather than a list of discrete rates, and
/// <c>clock.allowed-rates</c> arrives as a SPA-JSON array rendered into a <em>string</em>
/// (<c>"[ 48000 ]"</c>) rather than as a JSON array.
/// </para>
/// <para>
/// The measurement these tests encode: on the machine this was developed on, the analog sink's
/// <c>EnumFormat</c> reaches 192 kHz while the daemon's <c>clock.allowed-rates</c> is
/// <c>[ 48000 ]</c>, and playing a 96 kHz file left the node at 48 kHz throughout. The hardware
/// can; the daemon will not. That is why capability is the intersection of the two and not just
/// the node's own list.
/// </para>
/// </remarks>
public sealed class PipeWireCapabilityTests
{
    /// <summary>
    /// A dump with one sink whose rate is a range, plus a settings metadata block.
    /// </summary>
    private static string Dump(
        string allowedRates = "\"[ 48000 ]\"",
        int clockRate = 48_000,
        int forceRate = 0,
        int rateMin = 48_000,
        int rateMax = 192_000,
        string formats = """{ "default": "S32LE", "alt1": "S32LE", "alt2": "S16LE" }""") =>
        $$"""
        [
          {
            "id": 32,
            "type": "PipeWire:Interface:Metadata",
            "props": { "metadata.name": "settings" },
            "metadata": [
              { "subject": 0, "key": "clock.rate", "value": {{clockRate}} },
              { "subject": 0, "key": "clock.allowed-rates", "value": {{allowedRates}} },
              { "subject": 0, "key": "clock.force-rate", "value": {{forceRate}} }
            ]
          },
          {
            "id": 63,
            "type": "PipeWire:Interface:Node",
            "info": {
              "props": {
                "media.class": "Audio/Sink",
                "node.name": "alsa_output.pci-0000_65_00.6.analog-stereo",
                "node.description": "Ryzen HD Audio Controller Analog Stereo"
              },
              "params": {
                "EnumFormat": [
                  {
                    "mediaType": "audio",
                    "mediaSubtype": "raw",
                    "format": {{formats}},
                    "rate": { "default": 48000, "min": {{rateMin}}, "max": {{rateMax}} },
                    "channels": 2,
                    "position": [ "FL", "FR" ]
                  }
                ]
              }
            }
          }
        ]
        """;

    /// <summary>
    /// The daemon's clock policy, not just the node's <c>EnumFormat</c>, decides what is offered.
    /// </summary>
    /// <remarks>
    /// This is the finding the whole task turns on. The node says it reaches 192 kHz and it is
    /// telling the truth about the converter — but PipeWire clocks the entire graph at one rate
    /// chosen from <c>clock.allowed-rates</c>, and with that pinned to 48 000 the sink never once
    /// runs higher. Reporting 96 or 192 kHz here would advertise a tier whose only effect is to
    /// put a resampler in the path.
    /// </remarks>
    [Fact]
    public void AllowedRates_GateWhatTheNodeAdvertises()
    {
        var graph = PipeWireCapabilityParser.Parse(Dump());

        Assert.NotNull(graph);
        var sink = Assert.Single(graph.Sinks);

        Assert.Equal([48_000], sink.SampleRates);
        Assert.Equal(48_000, graph.GraphSampleRate);
    }

    /// <summary>
    /// Widen the daemon's allowed rates and the same hardware earns a hi-res tier.
    /// </summary>
    /// <remarks>
    /// The other half of the gate: nothing about the node changed between this test and the one
    /// above, only the daemon's policy. Verified against the real daemon by setting
    /// <c>clock.force-rate 96000</c>, at which point the node renegotiated to 96 kHz.
    /// </remarks>
    [Fact]
    public void AllowedRates_AdmitHiResWhenTheDaemonPermitsIt()
    {
        var graph = PipeWireCapabilityParser.Parse(Dump(allowedRates: "\"[ 44100, 48000, 96000, 192000 ]\""));

        var sink = Assert.Single(graph!.Sinks);

        // 44 100 is absent because the node's range starts at 48 000: both sides have to agree.
        Assert.Equal([48_000, 96_000, 192_000], sink.SampleRates);
    }

    /// <summary>
    /// A rate range admits every candidate between its bounds, not just its listed members.
    /// </summary>
    /// <remarks>
    /// Reading the choice object as an enumeration would see only <c>default</c>, <c>min</c> and
    /// <c>max</c> and would silently miss 96 000 — a rate the device genuinely takes.
    /// </remarks>
    [Fact]
    public void RateRange_AdmitsCandidatesBetweenItsBounds()
    {
        var graph = PipeWireCapabilityParser.Parse(
            Dump(allowedRates: "\"[ 44100, 48000, 88200, 96000, 176400, 192000 ]\"", rateMin: 44_100));

        Assert.Equal(
            [44_100, 48_000, 88_200, 96_000, 176_400, 192_000],
            Assert.Single(graph!.Sinks).SampleRates);
    }

    /// <summary>
    /// <c>clock.force-rate</c> overrides the allowed set outright.
    /// </summary>
    [Fact]
    public void ForceRate_PinsTheGraphRegardlessOfAllowedRates()
    {
        var graph = PipeWireCapabilityParser.Parse(
            Dump(allowedRates: "\"[ 44100, 48000, 96000 ]\"", forceRate: 96_000, rateMin: 44_100));

        Assert.Equal(96_000, graph!.GraphSampleRate);
        Assert.Equal([96_000], Assert.Single(graph.Sinks).SampleRates);
    }

    /// <summary>
    /// With no allowed set, the graph cannot switch and its current rate is the only one.
    /// </summary>
    /// <remarks>
    /// An empty <c>clock.allowed-rates</c> means PipeWire never renegotiates the graph, so
    /// <c>clock.rate</c> is not merely the current rate but the only rate there will be. Read as
    /// "unrestricted" it would license advertising anything the node lists.
    /// </remarks>
    [Fact]
    public void AbsentAllowedRates_LeaveTheCurrentRateAsTheOnlyOne()
    {
        var graph = PipeWireCapabilityParser.Parse(Dump(allowedRates: "\"[ ]\"", rateMin: 44_100));

        Assert.Equal([48_000], Assert.Single(graph!.Sinks).SampleRates);
    }

    /// <summary>
    /// The deepest linear format the sink lists becomes the reported depth, capped at 24.
    /// </summary>
    [Theory]
    [InlineData("""{ "default": "S16LE" }""", 16)]
    [InlineData("""{ "default": "S32LE", "alt1": "S16LE" }""", 24)]
    [InlineData("""{ "default": "S24LE", "alt1": "S16LE" }""", 24)]
    [InlineData("\"F32LE\"", 24)]
    [InlineData("\"S16LE\"", 16)]
    public void MaxBitDepth_IsTheDeepestLinearFormatCappedAt24(string formats, int expected) =>
        Assert.Equal(expected, Assert.Single(PipeWireCapabilityParser.Parse(Dump(formats: formats))!.Sinks).MaxBitDepth);

    /// <summary>
    /// Non-raw subtypes contribute nothing.
    /// </summary>
    /// <remarks>
    /// An <c>iec958</c> entry describes an S/PDIF passthrough this player does not produce, and
    /// its rates are not rates we could render at. The HDMI sink on the dev machine lists one
    /// alongside its raw entry, so this is the real layout rather than a hypothetical.
    /// </remarks>
    [Fact]
    public void Iec958Entries_AreIgnored()
    {
        const string json = """
        [
          {
            "id": 32,
            "type": "PipeWire:Interface:Metadata",
            "props": { "metadata.name": "settings" },
            "metadata": [
              { "subject": 0, "key": "clock.rate", "value": 48000 },
              { "subject": 0, "key": "clock.allowed-rates", "value": "[ 44100, 48000 ]" }
            ]
          },
          {
            "id": 94,
            "type": "PipeWire:Interface:Node",
            "info": {
              "props": {
                "media.class": "Audio/Sink",
                "node.name": "alsa_output.hdmi-stereo",
                "node.description": "HDMI 3"
              },
              "params": {
                "EnumFormat": [
                  {
                    "mediaType": "audio",
                    "mediaSubtype": "iec958",
                    "iec958Codec": { "default": "PCM" },
                    "rate": { "default": 48000, "min": 32000, "max": 48000 }
                  },
                  {
                    "mediaType": "audio",
                    "mediaSubtype": "raw",
                    "format": { "default": "S16LE" },
                    "rate": { "default": 48000, "min": 48000, "max": 48000 },
                    "channels": 2
                  }
                ]
              }
            }
          }
        ]
        """;

        var sink = Assert.Single(PipeWireCapabilityParser.Parse(json)!.Sinks);

        // 44 100 is in the allowed set and in the iec958 entry's range, but not in the raw one.
        Assert.Equal([48_000], sink.SampleRates);
        Assert.Equal(16, sink.MaxBitDepth);
    }

    /// <summary>
    /// A sink that did not enumerate its formats reports nothing rather than a guess.
    /// </summary>
    [Fact]
    public void SinkWithoutEnumFormat_ReportsNothing()
    {
        const string json = """
        [
          {
            "id": 70,
            "type": "PipeWire:Interface:Node",
            "info": {
              "props": {
                "media.class": "Audio/Sink",
                "node.name": "suspended",
                "node.description": "Suspended Sink"
              },
              "params": {}
            }
          }
        ]
        """;

        var sink = Assert.Single(PipeWireCapabilityParser.Parse(json)!.Sinks);

        Assert.Empty(sink.SampleRates);
        Assert.Equal(0, sink.MaxBitDepth);
        Assert.Equal(0, sink.Channels);
    }

    /// <summary>
    /// Output that is not a PipeWire dump yields null, which the caller reads as "no PipeWire".
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"an\": \"object\" }")]
    public void UnusableOutput_YieldsNull(string json) =>
        Assert.Null(PipeWireCapabilityParser.Parse(json));

    /// <summary>
    /// A dump with no sinks is still a dump — "PipeWire is here and has no sinks" is a different
    /// answer from "PipeWire is not here", and only the latter licenses the OpenAL fallback.
    /// </summary>
    [Fact]
    public void DumpWithNoSinks_IsAGraphRatherThanNull()
    {
        var graph = PipeWireCapabilityParser.Parse("[]");

        Assert.NotNull(graph);
        Assert.Empty(graph.Sinks);
    }

    /// <summary>
    /// Devices are matched on the node description OpenAL surfaces, then on the raw node name.
    /// </summary>
    [Fact]
    public void Match_FindsSinksByDescriptionThenNodeName()
    {
        var graph = PipeWireCapabilityParser.Parse(Dump())!;

        Assert.NotNull(graph.Match("Ryzen HD Audio Controller Analog Stereo"));
        Assert.NotNull(graph.Match("alsa_output.pci-0000_65_00.6.analog-stereo"));
        Assert.Null(graph.Match("Some Other Device"));
        Assert.Null(graph.Match(""));
    }

    /// <summary>
    /// A sink that cannot meet the graph rate does not report it as its mix rate.
    /// </summary>
    /// <remarks>
    /// Caught on real hardware rather than reasoned about: forcing the daemon to 96 kHz left the
    /// HDMI sink — whose <c>EnumFormat</c> stops at 48 kHz — unable to run at the graph rate, and
    /// reporting the graph rate anyway put <c>96000</c> at the head of that device's advertised
    /// formats. The mix rate has to survive the same intersection everything else does.
    /// </remarks>
    [Fact]
    public void Describe_WithholdsAGraphRateThisSinkCannotMeet()
    {
        // Node stops at 48 kHz; the daemon is forced to 96 kHz. They do not intersect.
        var graph = PipeWireCapabilityParser.Parse(
            Dump(forceRate: 96_000, rateMin: 48_000, rateMax: 48_000))!;

        var described = graph.Describe("Ryzen HD Audio Controller Analog Stereo");

        Assert.NotNull(described);
        Assert.Empty(described.SampleRates);
        Assert.Equal(0, described.MixSampleRate);
    }

    /// <summary>
    /// A withheld rate and an unknown rate are told apart, so only the second licenses a fallback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both come out of <c>Describe</c> as <c>MixSampleRate == 0</c>, and treating them alike
    /// reintroduces the defect this class exists to prevent. When the policy <em>is</em> known and
    /// the rate was withheld, the caller must not substitute one: the OpenAL mixer runs at the
    /// graph rate, so probing it hands back exactly the rate PipeWire rejected for that sink, and
    /// <c>PlayerCapabilities</c> treats a device's own mix rate as native by definition — so the
    /// advertisement would lead with a hi-res tier the sink resamples.
    /// </para>
    /// <para>
    /// That is the macOS bug in Linux clothing, which is why the distinction is a property on the
    /// graph rather than an inference in the enumerator.
    /// </para>
    /// </remarks>
    [Fact]
    public void AWithheldRateIsDistinguishedFromAnUnknownOne()
    {
        // Policy known (forced to 96 kHz), sink capped at 48 kHz: cannot meet it.
        var withheld = PipeWireCapabilityParser.Parse(
            Dump(forceRate: 96_000, rateMin: 48_000, rateMax: 48_000))!;

        Assert.True(withheld.HasRatePolicy);
        Assert.Equal(0, withheld.Describe("Ryzen HD Audio Controller Analog Stereo")!.MixSampleRate);

        // No settings metadata at all: nothing is known about the graph clock.
        const string noPolicy = """
        [
          {
            "id": 63,
            "type": "PipeWire:Interface:Node",
            "info": {
              "props": {
                "media.class": "Audio/Sink",
                "node.name": "sink",
                "node.description": "Sink"
              },
              "params": {
                "EnumFormat": [
                  {
                    "mediaType": "audio",
                    "mediaSubtype": "raw",
                    "format": { "default": "S16LE" },
                    "rate": { "default": 48000, "min": 44100, "max": 192000 },
                    "channels": 2
                  }
                ]
              }
            }
          }
        ]
        """;

        var unknown = PipeWireCapabilityParser.Parse(noPolicy)!;

        Assert.False(unknown.HasRatePolicy);
        Assert.Equal(0, unknown.Describe("Sink")!.MixSampleRate);
    }

    /// <summary>
    /// Describing a device the graph does not own returns null, so the caller falls back rather
    /// than reading the absence as a report of no capability.
    /// </summary>
    [Fact]
    public void Describe_ReturnsNullForADeviceTheGraphDoesNotOwn() =>
        Assert.Null(PipeWireCapabilityParser.Parse(Dump())!.Describe("Some Other Device"));

    /// <summary>
    /// The mix rate is answered for every sink, because the graph clock is shared.
    /// </summary>
    /// <remarks>
    /// This is what retires the old "only probe the default device" restriction: the reason for it
    /// was the cost of opening a device to read <c>ALC_FREQUENCY</c>, and reading the graph clock
    /// opens nothing.
    /// </remarks>
    [Fact]
    public void Describe_AnswersTheMixRateForEverySink()
    {
        var described = PipeWireCapabilityParser.Parse(Dump())!
            .Describe("alsa_output.pci-0000_65_00.6.analog-stereo");

        Assert.NotNull(described);
        Assert.Equal(48_000, described.MixSampleRate);
        Assert.Equal(2, described.Channels);
        Assert.Equal(24, described.MaxBitDepth);
        Assert.Equal([48_000], described.SampleRates);
    }
}
