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
    /// Sample rates the device accepts. Used to advertise real capabilities to the
    /// server instead of a hardcoded list; empty when the platform cannot report them.
    /// </summary>
    public IReadOnlyList<int> SupportedSampleRates { get; init; } = [];
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
