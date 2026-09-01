using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sendspin.Core.Audio;

namespace Sendspin.Platform.Windows.Audio;

/// <summary>
/// Enumerates WASAPI render endpoints, together with the format each one's mixer is actually
/// running at.
/// </summary>
/// <remarks>
/// <para>
/// The mix format is reported rather than assumed because the player advertises real device
/// capabilities to the server: a server told the truth about a 48 kHz endpoint can send 48 kHz
/// and skip a resampling stage, whereas a hardcoded codec list guarantees one.
/// </para>
/// <para>
/// <strong>What <see cref="AudioDeviceInfo.SupportedSampleRates"/> means here.</strong> Shared
/// mode only accepts the engine's own mix rate without conversion, so for most devices this is
/// a single entry. That is the honest answer to "what does this device take as-is"; other rates
/// still play, with the engine's resampler in the path and its latency visible in the audio
/// clock.
/// </para>
/// <para>
/// That satisfies the field's contract — <em>rates that reach the converter unresampled</em> —
/// because the probe below asks <c>IsFormatSupported</c> in <see cref="AudioClientShareMode.Shared"/>,
/// the same mode this player renders in. It would stop satisfying it if the probe were ever
/// widened to exclusive mode, which admits rates the engine cannot take as-is; the share mode is
/// the load-bearing part of the query, not an incidental argument.
/// </para>
/// </remarks>
public sealed class WasapiDeviceEnumerator : IAudioDeviceEnumerator
{
    /// <summary>
    /// Rates worth asking about. The two consumer families plus their multiples, which is every
    /// rate the protocol's codecs are produced at.
    /// </summary>
    private static readonly int[] CandidateSampleRates =
        [44100, 48000, 88200, 96000, 176400, 192000];

    private readonly ILogger<WasapiDeviceEnumerator> _logger;

    public WasapiDeviceEnumerator(ILogger<WasapiDeviceEnumerator> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = TryGetDefaultDeviceId(enumerator);
            var devices = new List<AudioDeviceInfo>();

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(Describe(device, device.ID == defaultId));
                }
            }

            return devices;
        }
        catch (COMException ex)
        {
            // The audio service can be stopped, and an RDP or session-0 context has no endpoints
            // at all. Neither is an error the user can act on, and neither is a reason to fail
            // startup.
            _logger.LogWarning(ex, "Windows audio endpoints could not be enumerated");
            return [];
        }
    }

    /// <inheritdoc/>
    public AudioDeviceInfo? GetDefaultDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                _logger.LogInformation("Windows reports no default audio output device");
                return null;
            }

            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return Describe(device, isDefault: true);
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "The default Windows audio endpoint could not be read");
            return null;
        }
    }

    private static string? TryGetDefaultDeviceId(MMDeviceEnumerator enumerator)
    {
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            return null;
        }

        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.ID;
    }

    private AudioDeviceInfo Describe(MMDevice device, bool isDefault)
    {
        var id = device.ID;
        var name = device.FriendlyName;

        try
        {
            // MMDevice.AudioClient hands out a fresh client each call, so this one is ours to
            // dispose. It is only activated, never initialised: querying the mix format and
            // asking about others needs nothing more.
            using var client = device.AudioClient;
            var mixFormat = client.MixFormat;

            return new AudioDeviceInfo
            {
                Id = id,
                Name = name,
                IsDefault = isDefault,
                MixSampleRate = mixFormat.SampleRate,
                MixChannels = mixFormat.Channels,
                SupportedSampleRates = ProbeSampleRates(client, mixFormat)
            };
        }
        catch (COMException ex)
        {
            // An endpoint can be listed as active and still refuse activation — an exclusive-mode
            // hold by another application, a driver mid-reset. The device is still selectable, so
            // it is reported with the capabilities left unknown rather than dropped.
            _logger.LogDebug(ex, "Audio endpoint {DeviceName} would not report its mix format", name);

            return new AudioDeviceInfo
            {
                Id = id,
                Name = name,
                IsDefault = isDefault
            };
        }
    }

    private IReadOnlyList<int> ProbeSampleRates(AudioClient client, WaveFormat mixFormat)
    {
        var supported = new List<int>();

        foreach (var rate in CandidateSampleRates)
        {
            try
            {
                var candidate = WaveFormat.CreateIeeeFloatWaveFormat(rate, mixFormat.Channels);
                if (client.IsFormatSupported(AudioClientShareMode.Shared, candidate))
                {
                    supported.Add(rate);
                }
            }
            catch (COMException ex)
            {
                _logger.LogDebug(ex, "Audio endpoint refused a {Rate} Hz format query", rate);
            }
        }

        // The rate the engine is running at is supported by definition, even if the query above
        // could not be completed for it.
        if (!supported.Contains(mixFormat.SampleRate))
        {
            supported.Add(mixFormat.SampleRate);
            supported.Sort();
        }

        return supported;
    }
}
