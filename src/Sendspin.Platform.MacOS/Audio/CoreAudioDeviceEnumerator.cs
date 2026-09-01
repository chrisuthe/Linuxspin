using Microsoft.Extensions.Logging;
using Sendspin.Core.Audio;

namespace Sendspin.Platform.MacOS.Audio;

/// <summary>
/// Enumerates macOS audio output devices through the CoreAudio HAL.
/// </summary>
/// <remarks>
/// <para>
/// Device identity is the device UID, not the <c>AudioObjectID</c>: object ids are handed out per
/// boot and reused as devices appear and disappear, so a persisted id would silently start
/// pointing at a different device. <see cref="CoreAudioInterop.TranslateDeviceUid"/> converts back
/// when a device is actually opened.
/// </para>
/// <para>
/// Sample rates come from the hardware rather than a hardcoded codec list, because they are what
/// the client advertises to the server. A device that reports nothing yields an empty list, which
/// is honest, rather than a plausible-looking guess.
/// </para>
/// </remarks>
public sealed class CoreAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    private readonly ILogger<CoreAudioDeviceEnumerator> _logger;

    public CoreAudioDeviceEnumerator(ILogger<CoreAudioDeviceEnumerator> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        var deviceIds = CoreAudioInterop.GetPropertyArray<uint>(
            CoreAudioInterop.SystemObject, CoreAudioInterop.HardwareDevices, CoreAudioInterop.ScopeGlobal);

        if (deviceIds.Length == 0)
        {
            _logger.LogDebug("The CoreAudio HAL reported no audio devices");
            return [];
        }

        var defaultId = ReadDefaultOutputDeviceId();
        var devices = new List<AudioDeviceInfo>(deviceIds.Length);

        foreach (var deviceId in deviceIds)
        {
            var device = Describe(deviceId, deviceId == defaultId);
            if (device is not null)
            {
                devices.Add(device);
            }
        }

        return devices;
    }

    /// <inheritdoc/>
    public AudioDeviceInfo? GetDefaultDevice()
    {
        var defaultId = ReadDefaultOutputDeviceId();
        return defaultId == 0 ? null : Describe(defaultId, isDefault: true);
    }

    private static uint ReadDefaultOutputDeviceId() =>
        CoreAudioInterop.TryGetProperty<uint>(
            CoreAudioInterop.SystemObject,
            CoreAudioInterop.HardwareDefaultOutputDevice,
            CoreAudioInterop.ScopeGlobal,
            out var deviceId)
            ? deviceId
            : 0;

    /// <summary>
    /// Counts the channels a device offers on its output scope.
    /// </summary>
    /// <remarks>
    /// This is the test for "is this an output device". A microphone still appears in
    /// <c>kAudioHardwarePropertyDevices</c>; what it does not have is an output stream
    /// configuration with channels in it. Only the buffer count and channel counts are read, so
    /// the variable-length <c>AudioBufferList</c> is walked as raw words rather than mapped.
    /// </remarks>
    private static int CountOutputChannels(uint deviceId)
    {
        // AudioBufferList is { UInt32 mNumberBuffers; AudioBuffer mBuffers[]; } and AudioBuffer is
        // { UInt32 mNumberChannels; UInt32 mDataByteSize; void* mData; }, so on a 64-bit target the
        // list is a 4-byte count, 4 bytes of padding, then 16 bytes per buffer.
        const int HeaderWords = 2;
        const int WordsPerBuffer = 4;

        var words = CoreAudioInterop.GetPropertyArray<uint>(
            deviceId, CoreAudioInterop.DeviceStreamConfiguration, CoreAudioInterop.ScopeOutput);

        if (words.Length < HeaderWords)
        {
            return 0;
        }

        var bufferCount = (int)words[0];
        var channels = 0;

        for (var buffer = 0; buffer < bufferCount; buffer++)
        {
            var channelWord = HeaderWords + (buffer * WordsPerBuffer);
            if (channelWord >= words.Length)
            {
                break;
            }

            channels += (int)words[channelWord];
        }

        return channels;
    }

    /// <summary>
    /// Returns the rates this device runs natively as configured, per the
    /// <see cref="AudioDeviceInfo.SupportedSampleRates"/> contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>That is the nominal rate, and only the nominal rate.</strong>
    /// <c>DeviceAvailableNominalSampleRates</c> is tempting and wrong here: it lists what the
    /// device could be <em>switched</em> to, and this player never switches it —
    /// <c>AuhalRenderPlayer</c> reads <c>DeviceNominalSampleRate</c> for its latency arithmetic
    /// and never sets it. So a stream at any other rate in that list goes through AUHAL's
    /// converter, which is precisely what this field must not promise.
    /// </para>
    /// <para>
    /// This mattered concretely: with the wider list, a Mac running at 48 kHz whose output also
    /// lists 96 kHz advertised a 96 kHz format <em>ahead</em> of 48 kHz, and CoreAudio resampled
    /// it straight back down — more bytes over the network to reach a resampler, at no gain in
    /// depth. The available-rates query is still worth having if this player ever sets the nominal
    /// rate; until it does, the honest answer is the one rate that needs no conversion.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<int> ReadSupportedSampleRates(uint deviceId, int nominalRate)
    {
        var ranges = CoreAudioInterop.GetPropertyArray<AudioValueRange>(
            deviceId, CoreAudioInterop.DeviceAvailableNominalSampleRates, CoreAudioInterop.ScopeOutput);

        // The decision itself is in Core, where it can be tested; this method is the property read.
        return CoreAudioSampleRates.ResolveNative(
            nominalRate,
            [.. ranges.Select(range => new SampleRateRange(range.Minimum, range.Maximum))]);
    }

    private AudioDeviceInfo? Describe(uint deviceId, bool isDefault)
    {
        var channels = CountOutputChannels(deviceId);
        if (channels <= 0)
        {
            return null;
        }

        var uid = CoreAudioInterop.GetPropertyString(
            deviceId, CoreAudioInterop.DeviceUid, CoreAudioInterop.ScopeGlobal);

        if (string.IsNullOrEmpty(uid))
        {
            // Without a UID the device cannot be reopened later, so offering it would produce a
            // selection that silently fails to apply.
            _logger.LogDebug("Skipping audio device {DeviceId}: it reports no UID", deviceId);
            return null;
        }

        var name = CoreAudioInterop.GetPropertyString(
            deviceId, CoreAudioInterop.ObjectName, CoreAudioInterop.ScopeGlobal);

        CoreAudioInterop.TryGetProperty<double>(
            deviceId, CoreAudioInterop.DeviceNominalSampleRate, CoreAudioInterop.ScopeGlobal, out var nominalRate);

        var mixRate = nominalRate > 0 ? (int)Math.Round(nominalRate) : 0;

        return new AudioDeviceInfo
        {
            Id = uid,
            Name = string.IsNullOrWhiteSpace(name) ? uid : name,
            IsDefault = isDefault,
            MixSampleRate = mixRate,
            MixChannels = channels,
            SupportedSampleRates = ReadSupportedSampleRates(deviceId, mixRate)

            // MaxBitDepth is deliberately left unset. AUHAL renders float32 and this enumerator
            // does not query the stream's physical format, so there is no measured depth to report
            // — and 0 means "not reported", which advertises 16-bit and changes nothing here.
        };
    }
}
