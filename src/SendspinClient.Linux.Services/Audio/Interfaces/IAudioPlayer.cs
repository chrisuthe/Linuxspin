// Local audio interfaces for features not provided by Sendspin.SDK

namespace SendspinClient.Linux.Services.Audio.Interfaces;

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
/// Information about an audio output device.
/// </summary>
public sealed class AudioDeviceInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
}

/// <summary>
/// Interface for enumerating available audio output devices.
/// </summary>
public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioDeviceInfo> GetDevices();
    AudioDeviceInfo? GetDefaultDevice();
}
