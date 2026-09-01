using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Sendspin.Core.Audio;
using Silk.NET.OpenAL;

namespace Sendspin.Platform.Linux.Audio;

/// <summary>
/// Enumerates OpenAL output devices through <c>ALC_ENUMERATE_ALL_EXT</c>.
/// </summary>
/// <remarks>
/// <para>
/// The device list is a <em>double</em>-null-terminated string, so it cannot be read through
/// Silk.NET's <c>GetContextProperty</c> overload that returns a <see cref="string"/> — that stops
/// at the first terminator and reports exactly one device, which is how this player used to
/// advertise only whichever output happened to be current. <c>alcGetString</c> is therefore
/// resolved as a function pointer and the list is walked by hand.
/// </para>
/// <para>
/// <c>ALC_ENUMERATE_ALL_EXT</c> is preferred over <c>ALC_ENUMERATION_EXT</c> because the former
/// lists every real output while the latter lists the driver's abstract entries. When neither is
/// present the current device is all OpenAL will admit to, and that single entry is what is
/// returned.
/// </para>
/// <para>
/// <strong>Capability discovery does not go through OpenAL.</strong> OpenAL has no accepted-rate
/// or channel query, and the one rate-shaped property it does expose cannot be used as one:
/// opening a device with <c>ALC_FREQUENCY</c> set and reading it back returns the value that was
/// asked for, whatever the hardware can do. Measured on this project's dev box, both outputs
/// echoed 192 000 — including an HDMI sink whose PipeWire node caps at 48 000. OpenAL Soft runs
/// its own mixer at the requested rate and resamples into the backend, so it can never answer
/// "no". Where PipeWire is present, <see cref="PipeWireCapabilityReader"/> supplies the real
/// answer; playback stays on OpenAL either way. See <c>docs/ARCHITECTURE.md</c>.
/// </para>
/// </remarks>
public sealed unsafe class OpenAlDeviceEnumerator : IAudioDeviceEnumerator, IDisposable
{
    /// <summary>ALC_ALL_DEVICES_SPECIFIER: the full output list, one string per device.</summary>
    private const int AlcAllDevicesSpecifier = 0x1013;

    /// <summary>ALC_DEFAULT_ALL_DEVICES_SPECIFIER.</summary>
    private const int AlcDefaultAllDevicesSpecifier = 0x1012;

    /// <summary>ALC_DEVICE_SPECIFIER, the pre-<c>ENUMERATE_ALL</c> list.</summary>
    private const int AlcDeviceSpecifier = 0x1005;

    /// <summary>ALC_DEFAULT_DEVICE_SPECIFIER.</summary>
    private const int AlcDefaultDeviceSpecifier = 0x1004;

    /// <summary>ALC_FREQUENCY, the mixer rate a device has negotiated with its backend.</summary>
    private const int AlcFrequency = 0x1007;

    private const string EnumerateAllExtension = "ALC_ENUMERATE_ALL_EXT";
    private const string EnumerationExtension = "ALC_ENUMERATION_EXT";

    /// <summary>
    /// Ceiling on entries read from the device list, so a driver that returns an unterminated
    /// buffer cannot turn enumeration into an unbounded walk of process memory.
    /// </summary>
    private const int MaxDevices = 128;

    private readonly ILogger<OpenAlDeviceEnumerator> _logger;
    private readonly PipeWireCapabilityReader _pipeWire;
    private readonly Lock _gate = new();

    private ALContext? _alc;
    private delegate* unmanaged[Cdecl]<Device*, int, byte*> _alcGetString;
    private bool _unavailable;
    private bool _disposed;

    public OpenAlDeviceEnumerator(ILogger<OpenAlDeviceEnumerator> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _pipeWire = new PipeWireCapabilityReader(logger);
    }

    /// <inheritdoc/>
    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        lock (_gate)
        {
            var alc = TryGetContextApi();
            if (alc is null)
            {
                return [];
            }

            try
            {
                return ReadDevices(alc);
            }
            catch (Exception ex) when (IsAudioStackFailure(ex))
            {
                _logger.LogWarning(ex, "OpenAL device enumeration failed");
                return [];
            }
        }
    }

    /// <inheritdoc/>
    public AudioDeviceInfo? GetDefaultDevice()
    {
        var devices = GetDevices();
        return devices.FirstOrDefault(device => device.IsDefault) ?? devices.FirstOrDefault();
    }

    /// <summary>
    /// Releases the OpenAL loader handle.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _alc?.Dispose();
            _alc = null;
            _alcGetString = null;
        }
    }

    /// <summary>
    /// Whether an exception means "there is no usable audio stack here", which on a headless or
    /// containerised machine is normal rather than a fault.
    /// </summary>
    private static bool IsAudioStackFailure(Exception exception) => exception is
        DllNotFoundException or
        FileNotFoundException or
        EntryPointNotFoundException or
        PlatformNotSupportedException or
        TypeInitializationException or
        InvalidOperationException;

    /// <summary>
    /// Splits a double-null-terminated ALC device list.
    /// </summary>
    private static List<string> ReadDeviceList(byte* list)
    {
        var names = new List<string>();

        if (list is null)
        {
            return names;
        }

        var cursor = list;
        while (*cursor != 0 && names.Count < MaxDevices)
        {
            var span = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(cursor);
            names.Add(Encoding.UTF8.GetString(span));
            cursor += span.Length + 1;
        }

        return names;
    }

    private static string? ReadString(byte* value) =>
        value is null ? null : Marshal.PtrToStringUTF8((nint)value);

    private ALContext? TryGetContextApi()
    {
        if (_disposed || _unavailable)
        {
            return null;
        }

        if (_alc is not null)
        {
            return _alc;
        }

        try
        {
            var alc = ALContext.GetApi();
            var getString = (delegate* unmanaged[Cdecl]<Device*, int, byte*>)alc.GetProcAddress(null, "alcGetString");

            if (getString is null)
            {
                alc.Dispose();
                _unavailable = true;
                _logger.LogWarning("OpenAL is present but does not export alcGetString; no devices can be listed");
                return null;
            }

            _alc = alc;
            _alcGetString = getString;
            return alc;
        }
        catch (Exception ex) when (IsAudioStackFailure(ex))
        {
            // No OpenAL at all. The player will fail later with its own message; enumeration's
            // contract is to report nothing rather than to throw.
            _unavailable = true;
            _logger.LogWarning(ex, "OpenAL could not be loaded; no audio devices will be listed");
            return null;
        }
    }

    private List<AudioDeviceInfo> ReadDevices(ALContext alc)
    {
        var enumerateAll = alc.IsExtensionPresent(null, EnumerateAllExtension);
        var enumeration = enumerateAll || alc.IsExtensionPresent(null, EnumerationExtension);

        var listToken = enumerateAll ? AlcAllDevicesSpecifier : AlcDeviceSpecifier;
        var defaultToken = enumerateAll ? AlcDefaultAllDevicesSpecifier : AlcDefaultDeviceSpecifier;

        var defaultName = ReadString(_alcGetString(null, defaultToken));
        var names = enumeration
            ? ReadDeviceList(_alcGetString(null, listToken))
            : [];

        if (names.Count == 0 && !string.IsNullOrEmpty(defaultName))
        {
            names.Add(defaultName);
        }

        if (names.Count == 0)
        {
            _logger.LogInformation("OpenAL reported no output devices");
            return [];
        }

        // One dump for the whole list, not one per device: it needs no device to be opened, which
        // is the entire reason capability discovery moved here.
        var graph = _pipeWire.Read();

        var devices = new List<AudioDeviceInfo>(names.Count);

        foreach (var name in names)
        {
            var isDefault = string.Equals(name, defaultName, StringComparison.Ordinal);

            var capabilities = graph?.Describe(name)
                ?? new AudioDeviceCapabilities(MixSampleRate: 0, Channels: 0, SampleRates: [], MaxBitDepth: 0);

            // The mix rate falls back per-device rather than only when PipeWire is missing
            // entirely. PipeWire can answer about a sink and still not supply a rate: a dump that
            // carries no settings metadata leaves the clock policy unknown, and a sink that cannot
            // meet the graph rate has none to report. Both used to skip this probe merely because
            // Describe returned non-null, which threw away a figure the pre-PipeWire code had.
            //
            // The old restraint still holds where it applies: reading ALC_FREQUENCY means opening
            // the device, which asks the sound server for a stream, so it is done for the default
            // device only.
            var mixSampleRate = capabilities.MixSampleRate > 0
                ? capabilities.MixSampleRate
                : isDefault ? ReadMixSampleRate(alc, name) : 0;

            devices.Add(new AudioDeviceInfo
            {
                // OpenAL identifies a device by its specifier string and offers nothing more
                // stable, so the id and the display name are necessarily the same value.
                Id = name,
                Name = name,
                IsDefault = isDefault,
                MixSampleRate = mixSampleRate,
                MixChannels = capabilities.Channels,
                SupportedSampleRates = capabilities.SampleRates,
                MaxBitDepth = capabilities.MaxBitDepth
            });
        }

        _logger.LogInformation(
            "OpenAL reported {Count} output device(s) via {Extension}; capabilities from {Source}",
            devices.Count,
            enumerateAll ? EnumerateAllExtension : EnumerationExtension,
            graph is null ? "OpenAL only (no PipeWire)" : "PipeWire EnumFormat");

        return devices;
    }

    private int ReadMixSampleRate(ALContext alc, string name)
    {
        var device = alc.OpenDevice(name);
        if (device is null)
        {
            return 0;
        }

        try
        {
            var frequency = 0;
            alc.GetContextProperty(device, (GetContextInteger)AlcFrequency, 1, &frequency);

            var error = alc.GetError(device);
            if (error != ContextError.NoError)
            {
                _logger.LogDebug("ALC_FREQUENCY query on {Device} returned {Error}", name, error);
                return 0;
            }

            return frequency > 0 ? frequency : 0;
        }
        finally
        {
            alc.CloseDevice(device);
        }
    }
}
