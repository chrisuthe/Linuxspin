namespace Sendspin.Core.Audio;

/// <summary>
/// Error codes for audio player errors.
/// </summary>
public enum AudioPlayerErrorCode
{
    Unknown,
    DeviceInitializationFailed,
    DeviceNotFound,
    FormatNotSupported,
    BufferUnderrun,
    BufferOverflow,
    DeviceLost
}

/// <summary>
/// An audio output device, and what its mixer will accept without resampling.
/// </summary>
public sealed class AudioDeviceInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public bool IsDefault { get; init; }

    /// <summary>
    /// Mixer sample rate the device is currently running at, or 0 when unknown.
    /// </summary>
    public int MixSampleRate { get; init; }

    /// <summary>
    /// Mixer channel count, or 0 when unknown.
    /// </summary>
    public int MixChannels { get; init; }

    /// <summary>
    /// Sample rates this device will run <strong>natively as it is currently configured</strong>,
    /// meaning the platform's audio stack puts no resampler in the path. Empty when the platform
    /// cannot report them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is a narrow contract, and it is narrow on purpose.</strong> It is not "rates
    /// the hardware is capable of", and it is not "rates that will play". Almost any rate will
    /// play — the question this field answers is which ones arrive at the converter unaltered. It
    /// is what gates the high-resolution tier in <c>PlayerCapabilities</c>, and a rate listed here
    /// that the stack would in fact resample turns that tier into bandwidth spent to reach a
    /// resampler at no gain in fidelity.
    /// </para>
    /// <para>
    /// The test is: <em>if the server sent audio at this rate right now, would anything resample
    /// it before the converter?</em> If the platform would have to be reconfigured first — a
    /// device rate switched, a daemon setting changed, an exclusive-mode stream taken — then the
    /// rate does <strong>not</strong> belong here, however capable the hardware is.
    /// </para>
    /// <para>
    /// How each platform satisfies it:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <strong>Linux</strong> — the sink node's <c>EnumFormat</c> intersected with the rates
    /// PipeWire's clock policy will actually run the graph at. The node's own list alone is the
    /// hardware's capability, not the daemon's willingness, and the two differ routinely.
    /// </description></item>
    /// <item><description>
    /// <strong>Windows</strong> — what shared-mode <c>IsFormatSupported</c> admits, which is
    /// essentially the engine's mix rate. Shared mode is the mode this player uses, and it accepts
    /// nothing else without the engine's resampler.
    /// </description></item>
    /// <item><description>
    /// <strong>macOS</strong> — the current nominal rate only.
    /// <c>DeviceAvailableNominalSampleRates</c> lists rates the device could be <em>switched</em>
    /// to, and this player never sets the nominal rate, so every other entry in it would go
    /// through AUHAL's converter.
    /// </description></item>
    /// </list>
    /// <para>
    /// An enumerator that widens this to "could support" reintroduces exactly the silent resample
    /// the advertisement exists to avoid, and it does so in shared code that cannot see which
    /// platform it is running on.
    /// </para>
    /// </remarks>
    public IReadOnlyList<int> SupportedSampleRates { get; init; } = [];

    /// <summary>
    /// The deepest sample format the device accepts, or 0 when the platform cannot report it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gates the high-resolution tier in the advertisement: a 24 here is what allows 24-bit
    /// formats to be offered at all. 0 means "not reported", which is treated as "16-bit only" —
    /// the conservative reading, and the one that keeps a platform that has not implemented this
    /// advertising exactly what it does today.
    /// </para>
    /// <para>
    /// Reported by the Linux enumerator from PipeWire. Windows and macOS leave it unset for now
    /// and so advertise no 24-bit tier; their existing rate reporting is unaffected.
    /// </para>
    /// </remarks>
    public int MaxBitDepth { get; init; }
}

/// <summary>
/// Enumerates the audio output devices the platform can render to.
/// </summary>
public interface IAudioDeviceEnumerator
{
    /// <summary>
    /// Returns every active output device, or an empty list when the platform audio
    /// stack is unavailable. Does not throw for an absent or broken audio stack.
    /// </summary>
    IReadOnlyList<AudioDeviceInfo> GetDevices();

    /// <summary>
    /// Returns the system default output device, or null when there is none.
    /// </summary>
    AudioDeviceInfo? GetDefaultDevice();
}
