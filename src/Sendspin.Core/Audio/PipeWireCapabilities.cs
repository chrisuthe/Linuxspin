using System.Globalization;
using System.Text.Json;

namespace Sendspin.Core.Audio;

/// <summary>
/// What one PipeWire sink node will accept, as read from its <c>EnumFormat</c> and filtered by
/// what the daemon's clock policy will actually grant.
/// </summary>
/// <param name="NodeName">The node's <c>node.name</c>, e.g. <c>alsa_output.pci-….analog-stereo</c>.</param>
/// <param name="Description">
/// The node's <c>node.description</c>. This is the string OpenAL Soft surfaces as its device
/// specifier, so it is what device names are matched on.
/// </param>
/// <param name="SampleRates">
/// Rates the node accepts <em>and</em> the graph will run at, ascending. Empty when the two do not
/// intersect, which cannot normally happen because the graph rate is always one the node took.
/// </param>
/// <param name="Channels">The node's channel count, or 0 when it did not report one.</param>
/// <param name="MaxBitDepth">
/// The deepest sample format the node accepts, capped at 24 — see
/// <see cref="PipeWireCapabilityParser"/> for why the cap is there.
/// </param>
public sealed record PipeWireSinkCapabilities(
    string NodeName,
    string Description,
    IReadOnlyList<int> SampleRates,
    int Channels,
    int MaxBitDepth);

/// <summary>
/// A snapshot of the PipeWire graph: the rate it is running at, and what each sink will take.
/// </summary>
/// <param name="GraphSampleRate">
/// The rate the graph is clocked at right now. Every node runs at this rate, which is why it can
/// be reported as the mix rate of <em>every</em> device without opening any of them.
/// </param>
/// <param name="PermittedRates">
/// The rates the daemon's clock policy will let the graph switch to, ascending. This is the gate
/// that stops a hi-res tier being advertised on hardware whose daemon will never run it.
/// </param>
/// <param name="Sinks">Every <c>Audio/Sink</c> node found.</param>
public sealed record PipeWireGraph(
    int GraphSampleRate,
    IReadOnlyList<int> PermittedRates,
    IReadOnlyList<PipeWireSinkCapabilities> Sinks)
{
    /// <summary>
    /// Finds the sink matching an OpenAL device specifier, or null when none does.
    /// </summary>
    /// <remarks>
    /// OpenAL Soft's PipeWire and PulseAudio backends both name a device by the node's
    /// <c>node.description</c>, so that is tried first; <c>node.name</c> is tried second because a
    /// backend that reports the raw node name is a plausible variation and the fallback costs
    /// nothing. Matching is ordinal and exact — a fuzzy match that paired the wrong sink would
    /// advertise one device's capabilities for another, which is worse than not matching at all.
    /// </remarks>
    public PipeWireSinkCapabilities? Match(string openAlDeviceName)
    {
        if (string.IsNullOrEmpty(openAlDeviceName))
        {
            return null;
        }

        return Sinks.FirstOrDefault(sink =>
                   string.Equals(sink.Description, openAlDeviceName, StringComparison.Ordinal))
               ?? Sinks.FirstOrDefault(sink =>
                   string.Equals(sink.NodeName, openAlDeviceName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Describes one device's capabilities, or returns null when this graph knows nothing about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><c>MixSampleRate</c> is answered for every device, not just the default.</strong>
    /// The reason a platform enumerator would restrict it to the default is the cost and side
    /// effects of opening a device to ask. PipeWire removes that reason outright: it clocks the
    /// whole graph at one rate, so a single dump gives the mix rate of every node without opening
    /// any of them.
    /// </para>
    /// <para>
    /// Null means "not one of my sinks" — an OpenAL null or loopback entry, or a machine where
    /// PipeWire is running but not driving this output. The caller falls back rather than treating
    /// the absence as a report of no capability.
    /// </para>
    /// <para>
    /// <strong>The graph rate is only this sink's mix rate if this sink accepts it.</strong> The
    /// graph runs at one rate but not every node can meet it: force the graph to 96 kHz on the dev
    /// machine and the analog sink follows while the HDMI sink, whose <c>EnumFormat</c> stops at
    /// 48 kHz, cannot. Reporting the graph rate unconditionally there put 96 kHz at the front of
    /// the HDMI device's advertisement — a rate that device will never accept, which is the exact
    /// failure this whole path exists to prevent. So the rate is reported only when it survived the
    /// intersection, and 0 (unknown) otherwise.
    /// </para>
    /// </remarks>
    public AudioDeviceCapabilities? Describe(string openAlDeviceName)
    {
        var sink = Match(openAlDeviceName);

        if (sink is null)
        {
            return null;
        }

        var mixRate = sink.SampleRates.Contains(GraphSampleRate) ? GraphSampleRate : 0;

        return new AudioDeviceCapabilities(mixRate, sink.Channels, sink.SampleRates, sink.MaxBitDepth);
    }

    /// <summary>
    /// Whether this dump told us what rate the graph runs at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is what decides whether a caller may substitute its own rate.</strong> A zero
    /// <see cref="AudioDeviceCapabilities.MixSampleRate"/> out of <see cref="Describe"/> has two
    /// entirely different meanings, and conflating them reintroduces the bug this class exists to
    /// prevent:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <strong>Policy known, rate withheld</strong> — the graph runs at a rate this sink cannot
    /// meet, so <see cref="Describe"/> deliberately reports none. That is a <em>finding</em>, and
    /// substituting a rate from anywhere else overrides it with the very number that was rejected:
    /// the OpenAL mixer runs at the graph rate, so probing <c>ALC_FREQUENCY</c> hands back exactly
    /// the rate PipeWire just said this sink will not take.
    /// </description></item>
    /// <item><description>
    /// <strong>Policy unknown</strong> — the dump carried no <c>settings</c> metadata, so nothing
    /// is known about the graph clock. That is an <em>absence</em>, and falling back to the mixer
    /// rate is strictly better than reporting nothing.
    /// </description></item>
    /// </list>
    /// <para>
    /// So a caller may fall back only when this is false. It is true whenever the dump carried a
    /// clock policy, whether or not any particular sink could meet it.
    /// </para>
    /// </remarks>
    public bool HasRatePolicy => GraphSampleRate > 0;
}

/// <summary>
/// The device fields a capability source can fill in.
/// </summary>
/// <param name="MixSampleRate">The rate the device's mixer runs at, or 0 when unknown.</param>
/// <param name="Channels">The device's channel count, or 0 when unknown.</param>
/// <param name="SampleRates">Rates the device takes as-is; empty when unknown.</param>
/// <param name="MaxBitDepth">The deepest format the device takes, or 0 when unknown.</param>
public sealed record AudioDeviceCapabilities(
    int MixSampleRate,
    int Channels,
    IReadOnlyList<int> SampleRates,
    int MaxBitDepth);

/// <summary>
/// Turns <c>pw-dump</c> JSON into a <see cref="PipeWireGraph"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why PipeWire answers this and OpenAL does not.</strong> OpenAL exposes no accepted-rate
/// or channel query at all, and its one rate-shaped property lies: opening a device with
/// <c>ALC_FREQUENCY</c> set to 192 000 and reading it back returns 192 000 on hardware whose sink
/// caps at 48 000, because OpenAL Soft runs its own mixer at whatever rate it was asked for and
/// resamples into the backend. It can never answer "no", so it cannot be used to gate an
/// advertisement. The sink node's <c>EnumFormat</c> is the real list.
/// </para>
/// <para>
/// <strong>Why the graph's clock policy is part of the answer.</strong> A node's
/// <c>EnumFormat</c> says what the <em>hardware</em> will take; it does not say what the daemon
/// will ask it for. PipeWire runs the whole graph at a single rate, chosen from
/// <c>clock.allowed-rates</c>, and a stream at any other rate is resampled on the way in. A DAC
/// whose <c>EnumFormat</c> reaches 192 kHz on a daemon configured
/// <c>clock.allowed-rates = [ 48000 ]</c> will never once run above 48 kHz — measured, not
/// assumed. So the rates reported here are the intersection of the two, which is the only figure
/// that means "this plays without a resampler in the path".
/// </para>
/// <para>
/// <strong>Why 24 is the depth ceiling.</strong> Sinks commonly advertise <c>S32LE</c>, and some
/// advertise <c>F32LE</c>. Neither means the converter has 32 real bits — 24 is the deepest any
/// consumer DAC resolves, it is the depth the hi-res tier is defined in, and claiming 32 would be
/// advertising precision that does not exist on the other end of the cable.
/// </para>
/// </remarks>
public static class PipeWireCapabilityParser
{
    /// <summary>
    /// Rates worth asking about — the two consumer families and their multiples, matching the
    /// candidate list the Windows enumerator probes so the platforms advertise from one vocabulary.
    /// </summary>
    public static readonly IReadOnlyList<int> CandidateSampleRates =
        [44_100, 48_000, 88_200, 96_000, 176_400, 192_000];

    /// <summary>
    /// The deepest depth this player will advertise. See the type remarks.
    /// </summary>
    public const int MaxAdvertisedBitDepth = 24;

    /// <summary>
    /// SPA sample-format names mapped to the depth this player will claim for them.
    /// </summary>
    /// <remarks>
    /// Only the linear PCM formats are listed. A sink that offers nothing but IEC958 or DSD
    /// subtypes contributes no depth here, which is correct: this player renders raw PCM.
    /// </remarks>
    private static readonly Dictionary<string, int> FormatDepths = new(StringComparer.Ordinal)
    {
        ["U8"] = 8,
        ["S8"] = 8,
        ["S16LE"] = 16,
        ["S16BE"] = 16,
        ["S24LE"] = 24,
        ["S24BE"] = 24,
        ["S24_32LE"] = 24,
        ["S24_32BE"] = 24,
        ["S32LE"] = 24,
        ["S32BE"] = 24,
        ["F32LE"] = 24,
        ["F32BE"] = 24,
        ["F64LE"] = 24,
        ["F64BE"] = 24
    };

    /// <summary>
    /// Parses a <c>pw-dump</c> document.
    /// </summary>
    /// <returns>
    /// The graph, or null when the document is not a PipeWire dump at all. A dump that parses but
    /// contains no sinks yields a graph with an empty <see cref="PipeWireGraph.Sinks"/> rather than
    /// null — "PipeWire is here and has no sinks" is a different answer from "PipeWire is not here".
    /// </returns>
    public static PipeWireGraph? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var (graphRate, permitted) = ReadClockPolicy(document.RootElement);
            var sinks = new List<PipeWireSinkCapabilities>();

            foreach (var node in document.RootElement.EnumerateArray())
            {
                var sink = TryReadSink(node, permitted);
                if (sink is not null)
                {
                    sinks.Add(sink);
                }
            }

            return new PipeWireGraph(graphRate, permitted, sinks);
        }
    }

    /// <summary>
    /// Reads the daemon's clock policy: the rate the graph runs at, and the rates it may switch to.
    /// </summary>
    /// <remarks>
    /// <c>clock.force-rate</c> wins outright when set — it pins the graph and nothing renegotiates
    /// it. Otherwise <c>clock.allowed-rates</c> is the switchable set. When that is absent or
    /// empty the graph cannot switch at all and <c>clock.rate</c> is the only rate there will ever
    /// be, which is a restriction rather than a licence to guess.
    /// </remarks>
    private static (int GraphRate, IReadOnlyList<int> Permitted) ReadClockPolicy(JsonElement root)
    {
        var clockRate = 0;
        var forceRate = 0;
        List<int>? allowed = null;

        foreach (var entry in root.EnumerateArray())
        {
            if (!IsType(entry, "PipeWire:Interface:Metadata") ||
                !TryGetProperty(entry, "props", out var props) ||
                ReadString(props, "metadata.name") != "settings" ||
                !TryGetProperty(entry, "metadata", out var metadata) ||
                metadata.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in metadata.EnumerateArray())
            {
                if (!TryGetProperty(item, "key", out var key) || key.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (!TryGetProperty(item, "value", out var value))
                {
                    continue;
                }

                switch (key.GetString())
                {
                    case "clock.rate":
                        clockRate = ReadInt(value);
                        break;
                    case "clock.force-rate":
                        forceRate = ReadInt(value);
                        break;
                    case "clock.allowed-rates":
                        allowed = ReadRateArray(value);
                        break;
                    default:
                        break;
                }
            }
        }

        var graphRate = forceRate > 0 ? forceRate : clockRate;

        if (forceRate > 0)
        {
            return (graphRate, [forceRate]);
        }

        if (allowed is { Count: > 0 })
        {
            allowed.Sort();
            return (graphRate, allowed);
        }

        return (graphRate, graphRate > 0 ? [graphRate] : []);
    }

    /// <summary>
    /// Reads <c>clock.allowed-rates</c>, which arrives as a SPA-JSON array rendered into a string
    /// (<c>"[ 48000 ]"</c>) rather than as a JSON array.
    /// </summary>
    private static List<int> ReadRateArray(JsonElement value)
    {
        var rates = new List<int>();

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var rate = ReadInt(item);
                if (rate > 0)
                {
                    rates.Add(rate);
                }
            }

            return rates;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return rates;
        }

        foreach (var token in value.GetString()!.Split(
                     ['[', ']', ',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate) && rate > 0)
            {
                rates.Add(rate);
            }
        }

        return rates;
    }

    /// <summary>
    /// Reads one dump entry as a sink, or returns null when it is not an <c>Audio/Sink</c> node.
    /// </summary>
    private static PipeWireSinkCapabilities? TryReadSink(JsonElement node, IReadOnlyList<int> permitted)
    {
        if (!IsType(node, "PipeWire:Interface:Node") ||
            !TryGetProperty(node, "info", out var info) ||
            !TryGetProperty(info, "props", out var props) ||
            ReadString(props, "media.class") != "Audio/Sink")
        {
            return null;
        }

        var nodeName = ReadString(props, "node.name") ?? string.Empty;
        var description = ReadString(props, "node.description") ?? nodeName;

        if (!TryGetProperty(info, "params", out var parameters) ||
            !TryGetProperty(parameters, "EnumFormat", out var enumFormat) ||
            enumFormat.ValueKind != JsonValueKind.Array)
        {
            // The node is real but did not enumerate its formats — a suspended device that has not
            // been opened yet can do this. Reported with nothing filled in, so the caller falls
            // back rather than treating "unknown" as "unsupported".
            return new PipeWireSinkCapabilities(nodeName, description, [], 0, 0);
        }

        var rates = new SortedSet<int>();
        var channels = 0;
        var depth = 0;

        foreach (var format in enumFormat.EnumerateArray())
        {
            // Only raw PCM. An iec958 or dsd entry describes a passthrough this player does not
            // produce, and its rates are not rates we could render at.
            if (ReadString(format, "mediaType") != "audio" || ReadString(format, "mediaSubtype") != "raw")
            {
                continue;
            }

            if (TryGetProperty(format, "rate", out var rate))
            {
                foreach (var candidate in CandidateSampleRates)
                {
                    if (ChoiceAllows(rate, candidate) && permitted.Contains(candidate))
                    {
                        rates.Add(candidate);
                    }
                }
            }

            if (TryGetProperty(format, "channels", out var channelChoice))
            {
                channels = Math.Max(channels, ReadPreferredInt(channelChoice));
            }

            if (TryGetProperty(format, "format", out var formatChoice))
            {
                depth = Math.Max(depth, ReadMaxDepth(formatChoice));
            }
        }

        return new PipeWireSinkCapabilities(
            nodeName, description, [.. rates], channels, Math.Min(depth, MaxAdvertisedBitDepth));
    }

    /// <summary>
    /// Whether a SPA choice admits <paramref name="candidate"/>.
    /// </summary>
    /// <remarks>
    /// A choice is rendered by <c>pw-dump</c> three ways: a bare value when it is fixed, an object
    /// with <c>min</c>/<c>max</c> for a range, and an object of <c>default</c>/<c>alt1</c>/… for an
    /// enumeration. A rate is very often a <em>range</em> — the analog sink measured here reports
    /// <c>{default 48000, min 48000, max 192000}</c> — so treating the object as an enumeration and
    /// reading only its listed members would miss every rate between the bounds.
    /// </remarks>
    private static bool ChoiceAllows(JsonElement choice, int candidate)
    {
        if (choice.ValueKind == JsonValueKind.Number)
        {
            return ReadInt(choice) == candidate;
        }

        if (choice.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryGetProperty(choice, "min", out var min) && TryGetProperty(choice, "max", out var max))
        {
            return candidate >= ReadInt(min) && candidate <= ReadInt(max);
        }

        foreach (var member in choice.EnumerateObject())
        {
            if (ReadInt(member.Value) == candidate)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the value a choice would settle on: its <c>default</c> if it has one, else the bare
    /// value, else a range's <c>max</c>.
    /// </summary>
    private static int ReadPreferredInt(JsonElement choice)
    {
        if (choice.ValueKind == JsonValueKind.Number)
        {
            return ReadInt(choice);
        }

        if (choice.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (TryGetProperty(choice, "default", out var preferred))
        {
            return ReadInt(preferred);
        }

        return TryGetProperty(choice, "max", out var max) ? ReadInt(max) : 0;
    }

    /// <summary>
    /// Reads the deepest depth a format choice offers.
    /// </summary>
    private static int ReadMaxDepth(JsonElement choice)
    {
        if (choice.ValueKind == JsonValueKind.String)
        {
            return FormatDepths.GetValueOrDefault(choice.GetString()!, 0);
        }

        if (choice.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var depth = 0;
        foreach (var member in choice.EnumerateObject())
        {
            if (member.Value.ValueKind == JsonValueKind.String)
            {
                depth = Math.Max(depth, FormatDepths.GetValueOrDefault(member.Value.GetString()!, 0));
            }
        }

        return depth;
    }

    private static bool IsType(JsonElement element, string type) =>
        element.ValueKind == JsonValueKind.Object && ReadString(element, "type") == type;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return element.TryGetProperty(name, out value);
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads an integer that PipeWire may have rendered as a number or as a string.
    /// </summary>
    private static int ReadInt(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetInt32(out var number) ? number : 0,
        JsonValueKind.String => int.TryParse(
            value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0,
        _ => 0
    };
}
