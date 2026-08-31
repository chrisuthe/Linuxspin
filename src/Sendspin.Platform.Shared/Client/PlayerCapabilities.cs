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
    /// </remarks>
    public static IReadOnlyList<string> AdvertisedRoles { get; } =
        [
            $"{ClientRoles.Player}@v1",
            $"{ClientRoles.Controller}@v1",
            $"{ClientRoles.Metadata}@v1",
            $"{ClientRoles.Artwork}@v1"
        ];

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
    /// The device's current mix rate comes first: matching it means the OS mixer does no
    /// resampling, which is both the lowest latency and the only configuration whose latency
    /// the device reports honestly. 48 kHz and 44.1 kHz follow because a server that has
    /// neither will pick something, and those two cover essentially all music.
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

    private static List<AudioFormat> BuildFormats(string preferredCodec, AudioDeviceInfo? device)
    {
        var channels = device is { MixChannels: > 0 } ? Math.Min(device.MixChannels, 2) : 2;
        var rates = ResolveSampleRates(device);

        // The user's preferred codec is offered first; the rest still follow, because a server
        // that cannot do FLAC should get Opus rather than nothing.
        var codecs = SupportedCodecs
            .OrderByDescending(codec => string.Equals(codec, preferredCodec, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var formats = new List<AudioFormat>();

        foreach (var codec in codecs)
        {
            foreach (var rate in rates)
            {
                formats.Add(new AudioFormat
                {
                    Codec = codec,
                    SampleRate = rate,
                    Channels = channels,
                    BitDepth = codec == AudioCodecs.Opus ? null : 16
                });
            }
        }

        return formats;
    }
}
