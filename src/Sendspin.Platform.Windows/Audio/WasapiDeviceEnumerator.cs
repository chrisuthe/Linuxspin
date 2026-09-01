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
/// mode renders at the engine's mix format and converts everything else, so the rates that reach
/// the converter unresampled are exactly one: the mix rate. Other rates still play, with the
/// engine's resampler in the path and its latency visible in the audio clock — they are simply not
/// what that field promises. See <see cref="ProbeSampleRates"/>.
/// </para>
/// </remarks>
public sealed class WasapiDeviceEnumerator : IAudioDeviceEnumerator
{
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
                SupportedSampleRates = ProbeSampleRates(mixFormat)
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

    /// <summary>
    /// Returns the rates this endpoint renders without conversion, per the
    /// <see cref="AudioDeviceInfo.SupportedSampleRates"/> contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>In shared mode that is the engine's mix rate, and nothing else.</strong> The engine
    /// renders at its mix format and converts everything else, so a rate is either the mix rate or
    /// it is resampled — there is no third case for this player to report. Probing a candidate list
    /// with <c>IsFormatSupported</c> was answering a different question: what the endpoint will
    /// <em>accept</em>, which in shared mode includes whatever the engine is willing to convert.
    /// </para>
    /// <para>
    /// Nothing real is lost by narrowing it. An endpoint genuinely running at 96 kHz reports 96 kHz
    /// as its mix rate, so it still earns its high-resolution tier; what disappears is only the
    /// claim to rates the engine would have resampled.
    /// </para>
    /// <para>
    /// This was tightened after the equivalent bug was found on macOS. Windows had been safe by
    /// accident rather than by construction — the probe happening to admit little beyond the mix
    /// rate — and "safe by accident" stopped being good enough once the advertisement began
    /// <em>leading</em> with the high-resolution tier rather than appending it.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<int> ProbeSampleRates(WaveFormat mixFormat) =>
        mixFormat.SampleRate > 0 ? [mixFormat.SampleRate] : [];
}
