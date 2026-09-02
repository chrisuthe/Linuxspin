using Sendspin.Core.Audio;
using Sendspin.Core.Configuration;
using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.Platform.Shared.Client;

/// <summary>
/// Builds the <see cref="ClientCapabilities"/> this installation advertises, from persisted
/// settings and the real capabilities of the chosen output device.
/// </summary>
/// <remarks>
/// The formats offered come from what the device actually reports, not a fixed list. Offering a
/// rate the hardware will not take means the server picks it and the OS mixer silently
/// resamples, adding latency that then has to be discovered rather than declared.
/// </remarks>
public static class PlayerCapabilities
{
    /// <summary>
    /// Startup lead time reported when the pipeline has not measured its own yet.
    /// </summary>
    /// <remarks>
    /// Covers codec init, decoder warm-up and the first fill of the output buffer. The server
    /// schedules the first chunk at least this far after the start trigger, so too small truncates
    /// the opening of a track and too large delays every start.
    /// <para>
    /// It is a fixed figure, and that is a known shortfall against the requirement that it be
    /// derived from the pipeline: the value has to be advertised in <c>client/hello</c>, which is
    /// sent before any audio has flowed and therefore before the pipeline can report a measured
    /// latency. Deriving it properly means re-advertising after the first stream, which the SDK's
    /// capabilities are not shaped for. Recorded in <c>docs/COMPLIANCE.md</c>.
    /// </para>
    /// </remarks>
    public const int DefaultRequiredLeadTimeMs = 350;

    /// <summary>
    /// Ongoing buffer floor requested from the server, absorbing network jitter and decode
    /// variance. The SDK's own default is 150 ms as a conservative LAN figure, and this
    /// deliberately matches it rather than inventing a different number.
    /// </summary>
    public const int DefaultMinBufferMs = 150;

    /// <summary>
    /// The lowest rate belonging to the high-resolution tier.
    /// </summary>
    /// <remarks>
    /// The tier is defined by rate rather than by depth alone, so that a device pinned to 48 kHz
    /// advertises exactly what it always did. 48 kHz at 24 bits is deliberately <em>not</em>
    /// offered: it would be a real gain in depth on hardware that accepts it, but it changes the
    /// advertisement of every device that reports 24-bit support, including ones with no hi-res
    /// rate at all, and that is a wider change than a hi-res tier.
    /// </remarks>
    private const int HighResolutionThresholdHz = 88_200;

    /// <summary>
    /// The depth the high-resolution tier is offered at.
    /// </summary>
    /// <remarks>
    /// 24 rather than 32 because no consumer converter resolves more, and because both the SDK's
    /// PCM and FLAC decoders were verified to handle 24-bit at 96 and 192 kHz.
    /// </remarks>
    private const int HighResolutionBitDepth = 24;

    /// <summary>The depth the regular tier is offered at.</summary>
    private const int RegularBitDepth = 16;

    /// <summary>
    /// Rates Opus can be asked for. Anything else makes the SDK's Opus decoder throw on
    /// construction, so offering one is offering a stream that cannot be played.
    /// </summary>
    /// <remarks>
    /// <c>OpusDecoder</c> rejects a rate outside 8/12/16/24/48 kHz with
    /// <c>"Sample rate is invalid (must be 8/12/16/24/48 Khz)"</c>. This player used to advertise
    /// Opus at 44 100 alongside 48 000 on every platform — a server that picked it got a decoder
    /// that threw before the first sample. 48 kHz is the only member of this set the device
    /// enumeration can produce, but the set is written out rather than reduced to one value so
    /// the constraint is legible.
    /// </remarks>
    private static readonly int[] OpusSampleRates = [8_000, 12_000, 16_000, 24_000, 48_000];

    /// <summary>
    /// The roles advertised in <c>client/hello</c>, as <c>&lt;role&gt;@v&lt;version&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The version suffix is load-bearing.</strong> <c>ClientRoles.Player</c> and friends
    /// are the pre-versioning spellings — bare <c>player</c>, <c>controller</c> — and a current
    /// server matches <c>supported_roles</c> against versioned identifiers. Advertising the bare
    /// names makes a server activate nothing:
    /// </para>
    /// <code>
    /// non-compliant client: client/hello sent support objects for unlisted roles: player@v1, artwork@v1
    /// Client offered roles/versions this server does not implement: ['player', 'controller', 'metadata', 'artwork']
    /// </code>
    /// <para>
    /// The client then connects, clock-syncs and registers — as a <em>protocol</em> entry rather
    /// than a player — and the server never sends <c>stream/start</c>, so nothing is ever
    /// rendered. That is silence with a healthy connection and no error anywhere on this side,
    /// which is exactly how it presented.
    /// </para>
    /// <para>
    /// SDK 9.3.2 is half-migrated rather than simply old: it already keys the support objects it
    /// emits as <c>player@v1_support</c> and <c>artwork@v1_support</c>, and only the role list
    /// kept the bare spellings. So these are written out here rather than taken from
    /// <c>ClientRoles</c>, which has no versioned members to take.
    /// </para>
    /// <para>
    /// <c>color@v1</c> and <c>visualizer@v1</c> feed the living backdrop: the palette the server
    /// extracts from the artwork, and the loudness and beat frames it derives from the audio. The
    /// visualizer role goes with <see cref="VisualizerSupport"/> and must not be listed without
    /// it, nor the support set without the role: the SDK emits <c>visualizer@v1_support</c>
    /// whenever the support object is set, and a support object for an unlisted role is the
    /// non-compliant hello quoted above.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> AdvertisedRoles { get; } =
        [
            $"{ClientRoles.Player}@v1",
            $"{ClientRoles.Controller}@v1",
            $"{ClientRoles.Metadata}@v1",
            $"{ClientRoles.Artwork}@v1",
            "color@v1",
            $"{ClientRoles.Visualizer}@v1"
        ];

    /// <summary>
    /// The visualizer features the backdrop consumes, and the rate it wants them at.
    /// </summary>
    /// <remarks>
    /// Loudness and beat only: the glow eases toward loudness and pulses on beats, and nothing
    /// here draws a spectrum. 30 frames a second is plenty for values that are eased over
    /// hundreds of milliseconds anyway, and 4 096 bytes of buffered frames is the WPF player's
    /// figure, which a server has been seen to honour. Spectrum is not listed, so no spectrum
    /// configuration is needed.
    /// </remarks>
    public static VisualizerSupport VisualizerSupport { get; } = new()
    {
        Types = [VisualizerTypes.Loudness, VisualizerTypes.Beat],
        RateMax = 30,
        BufferCapacity = 4096
    };

    /// <summary>
    /// Codecs this build can decode, best first.
    /// </summary>
    /// <remarks>
    /// FLAC before Opus: both are supported by the SDK's decoders, and for a LAN endpoint
    /// lossless is the better default when bandwidth is not the constraint. PCM is offered last
    /// as the always-works fallback.
    /// </remarks>
    public static IReadOnlyList<string> SupportedCodecs { get; } =
        [AudioCodecs.Flac, AudioCodecs.Opus, AudioCodecs.Pcm];

    /// <summary>
    /// Builds the capabilities to advertise.
    /// </summary>
    /// <param name="settings">Persisted settings, supplying identity, volume and mute.</param>
    /// <param name="device">
    /// The output device that will be used, or null when none could be enumerated.
    /// </param>
    /// <param name="softwareVersion">This build's version string.</param>
    public static ClientCapabilities Build(
        PlayerSettings settings,
        AudioDeviceInfo? device,
        string softwareVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(softwareVersion);

        return new ClientCapabilities
        {
            ClientId = settings.ClientId,
            ClientName = settings.PlayerName,
            ProductName = "Sendspin Player",
            Manufacturer = "Sendspin Contributors",
            SoftwareVersion = softwareVersion,
            Roles = [.. AdvertisedRoles],
            AudioFormats = BuildFormats(settings.PreferredCodec, device),

            // Alongside visualizer@v1 in the roles above; the two go together or not at all.
            VisualizerSupport = VisualizerSupport,

            // BufferCapacity is deliberately unset. It is compressed *bytes*, not milliseconds,
            // and the 8 000 that used to be set here — read as 8 s — advertised about one second
            // of Opus. Left alone, the SDK derives it from the decoded buffer's 30 s default and
            // the formats above, using whichever of them packs the most audio into a byte. That
            // is a bitrate-less Opus entry, which the SDK values at its conservative 64 kbps
            // fallback; declaring AudioFormat.Bitrate would tighten it, and is deliberately not
            // done here.
            RequiredLeadTimeMs = DefaultRequiredLeadTimeMs,
            MinBufferMs = DefaultMinBufferMs,

            // The player exposes a static-delay control, so it must accept the server's
            // set_static_delay command as well as reporting its own value.
            SupportsSetStaticDelay = true,

            InitialVolume = Math.Clamp(settings.Volume, 0, 100),
            InitialMuted = settings.Muted,
            ArtworkChannels =
            [
                new ArtworkChannelSpec
                {
                    Source = ArtworkSources.Album,
                    Format = "jpeg",
                    MediaWidth = 512,
                    MediaHeight = 512
                }
            ]
        };
    }

    /// <summary>
    /// Returns the rates this device will accept, in the order they should be offered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The device's current mix rate comes first: matching it means the OS mixer does no
    /// resampling, which is both the lowest latency and the only configuration whose latency
    /// the device reports honestly. 48 kHz and 44.1 kHz follow because a server that has
    /// neither will pick something, and those two cover essentially all music.
    /// </para>
    /// <para>
    /// <strong>The two fallbacks stay even when the device did report its rates</strong>, and that
    /// is deliberate. They can mean offering a rate the device does not take as-is — a sink pinned
    /// to 48 kHz by its daemon still gets 44.1 kHz offered, and the sound server resamples it. The
    /// contract on <see cref="AudioDeviceInfo.SupportedSampleRates"/> is what a device runs
    /// natively; this list is a superset of that, ordered by preference, and the resampler's delay
    /// is included in the latency the device reports rather than hidden. Refusing 44.1 kHz would
    /// force a resample for the majority of all music anyway, only on the server's side and with no
    /// better result.
    /// </para>
    /// <para>
    /// What the device's report <em>does</em> gate is the high-resolution tier, where the
    /// trade-off is the other way round: a 96/24 stream sent to a device that will only ever run
    /// 48 kHz is bandwidth and fidelity spent to reach a resampler. See
    /// <see cref="BuildFormats"/>.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<int> ResolveSampleRates(AudioDeviceInfo? device)
    {
        var rates = new List<int>();

        if (device is not null && device.MixSampleRate > 0)
        {
            rates.Add(device.MixSampleRate);
        }

        if (device is not null)
        {
            foreach (var rate in device.SupportedSampleRates)
            {
                if (!rates.Contains(rate))
                {
                    rates.Add(rate);
                }
            }
        }

        foreach (var fallback in new[] { 48_000, 44_100 })
        {
            if (!rates.Contains(fallback))
            {
                rates.Add(fallback);
            }
        }

        return rates;
    }

    /// <summary>
    /// Builds the advertised format list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The ordering rule.</strong> Most-preferred first, on three keys, and it is a
    /// decision rather than a by-product of how the loops nest — which is what it used to be:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <strong>Codec-major.</strong> The user's preferred codec first, then the rest in
    /// <see cref="SupportedCodecs"/> order, because a server that cannot do FLAC should get Opus
    /// rather than nothing.
    /// </description></item>
    /// <item><description>
    /// <strong>Tier next, high-resolution before regular.</strong> A rate reaches the hi-res tier
    /// only by being above the threshold <em>and</em> passing <see cref="IsNative"/> — a rate the
    /// device runs with no resampler in the path. That second condition is what makes leading with
    /// this tier safe: offering it first is only right if the device really renders it, and a
    /// platform whose native list is wider than that would otherwise be led with a rate its own
    /// stack resamples straight back down. The regular tier always follows as the floor.
    /// </description></item>
    /// <item><description>
    /// <strong>Rate last: the device's current mix rate first, then descending.</strong> The mix
    /// rate leads its tier because it is the one rate that needs no renegotiation — on PipeWire the
    /// whole graph is clocked at it, and asking for anything else means the daemon either switches
    /// every node or resamples. The remainder descend because, among rates the device has already
    /// said it accepts, higher is better.
    /// </description></item>
    /// </list>
    /// <para>
    /// The rule is pinned by tests. It is load-bearing now in a way it was not before: a server
    /// that takes the first format it recognises gets the hi-res tier only when one was earned,
    /// and gets the mix rate rather than a resampled one within whichever tier it lands in.
    /// </para>
    /// </remarks>
    private static List<AudioFormat> BuildFormats(string preferredCodec, AudioDeviceInfo? device)
    {
        var channels = device is { MixChannels: > 0 } ? Math.Min(device.MixChannels, 2) : 2;
        var rates = ResolveSampleRates(device);
        var mixRate = device?.MixSampleRate ?? 0;

        var regularRates = OrderTier(rates.Where(rate => rate < HighResolutionThresholdHz), mixRate);
        var highResolutionRates = OrderTier(
            rates.Where(rate => rate >= HighResolutionThresholdHz && IsNative(device, rate)), mixRate);

        // Depth is gated separately from rate, because they are separately reported and a device
        // can have one without the other. A sink that runs 96 kHz but accepts only S16LE still
        // gets its 96 kHz offered — at 16 bits, which is what it will take. Gating the whole tier
        // on depth would drop the rate as well and lose real capability.
        var bitDepthAtHighRates = device is not null && device.MaxBitDepth >= HighResolutionBitDepth
            ? HighResolutionBitDepth
            : RegularBitDepth;

        var codecs = SupportedCodecs
            .OrderByDescending(codec => string.Equals(codec, preferredCodec, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var formats = new List<AudioFormat>();

        foreach (var codec in codecs)
        {
            // Opus carries no bit depth — it is a lossy transform codec, not a PCM container — so
            // it has no place in a tier distinguished by depth, and its rate ceiling of 48 kHz
            // puts it below the hi-res threshold regardless.
            if (codec == AudioCodecs.Opus)
            {
                foreach (var rate in regularRates.Where(OpusSampleRates.Contains))
                {
                    formats.Add(new AudioFormat
                    {
                        Codec = codec,
                        SampleRate = rate,
                        Channels = channels,
                        BitDepth = null
                    });
                }

                continue;
            }

            AddTier(formats, codec, highResolutionRates, channels, bitDepthAtHighRates);
            AddTier(formats, codec, regularRates, channels, RegularBitDepth);
        }

        return formats;
    }

    /// <summary>
    /// Whether the device will run this rate without a resampler in the path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two signals, and both are needed. <see cref="AudioDeviceInfo.SupportedSampleRates"/> is the
    /// enumerator's list of native rates, per the contract written on that field. The device's
    /// current <see cref="AudioDeviceInfo.MixSampleRate"/> counts too, and independently: it is the
    /// rate the device is running at <em>right now</em>, which is native by definition whether or
    /// not the enumerator also happened to list it.
    /// </para>
    /// <para>
    /// This is the gate that keeps the hi-res tier honest across platforms, and it is checked here
    /// rather than left to the rate threshold. The threshold alone is not a gate:
    /// <see cref="ResolveSampleRates"/> promotes the mix rate and copies the device's list before
    /// appending two hardcoded fallbacks, so "above 88.2 kHz" only excludes the fallbacks. Relying
    /// on that meant relying on every enumerator happening to include its own mix rate in its
    /// native list — true today, unenforced, and exactly the kind of coincidence that broke macOS
    /// when its list turned out to mean something wider.
    /// </para>
    /// </remarks>
    private static bool IsNative(AudioDeviceInfo? device, int rate) =>
        device is not null &&
        (device.SupportedSampleRates.Contains(rate) || (device.MixSampleRate > 0 && device.MixSampleRate == rate));

    /// <summary>
    /// Orders one tier's rates: the device's mix rate first when it falls in this tier, then the
    /// rest descending.
    /// </summary>
    private static List<int> OrderTier(IEnumerable<int> rates, int mixRate)
    {
        var ordered = rates.Distinct().OrderByDescending(rate => rate).ToList();

        var mixIndex = ordered.IndexOf(mixRate);
        if (mixIndex > 0)
        {
            ordered.RemoveAt(mixIndex);
            ordered.Insert(0, mixRate);
        }

        return ordered;
    }

    private static void AddTier(
        List<AudioFormat> formats, string codec, IReadOnlyList<int> rates, int channels, int bitDepth)
    {
        foreach (var rate in rates)
        {
            formats.Add(new AudioFormat
            {
                Codec = codec,
                SampleRate = rate,
                Channels = channels,
                BitDepth = bitDepth
            });
        }
    }
}
