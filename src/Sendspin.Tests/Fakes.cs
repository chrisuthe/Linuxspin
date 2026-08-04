using Sendspin.Core.Control;
using Sendspin.Core.MediaSession;
using Sendspin.Core.Platform;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;

namespace Sendspin.Tests;

/// <summary>
/// An <see cref="IAudioPlayer"/> that records what it was told, for asserting on values the SDK
/// hands to a platform backend.
/// </summary>
/// <remarks>
/// Hand-written rather than produced by a mocking library. These fakes are small, and the
/// assertions read better against a plain object than against a configured mock; it also keeps
/// the test project free of a mocking dependency.
/// </remarks>
internal sealed class RecordingAudioPlayer : IAudioPlayer
{
    public AudioPlayerState State { get; private set; } = AudioPlayerState.Uninitialized;

    public float Volume { get; set; } = 1.0f;

    public bool IsMuted { get; set; }

    public int OutputLatencyMs => 20;

    public int CalibratedStartupLatencyMs => 0;

    public AudioFormat? OutputFormat { get; private set; }

    public List<string> Calls { get; } = [];

    public event EventHandler<AudioPlayerState>? StateChanged;

    public event EventHandler<AudioPlayerError>? ErrorOccurred;

    public long? GetAudioClockMicroseconds() => null;

    public void NotifyReconnect() => Calls.Add(nameof(NotifyReconnect));

    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken = default)
    {
        OutputFormat = format;
        State = AudioPlayerState.Stopped;
        StateChanged?.Invoke(this, State);
        _ = ErrorOccurred;
        return Task.CompletedTask;
    }

    public void SetSampleSource(IAudioSampleSource source) => Calls.Add(nameof(SetSampleSource));

    public void Play()
    {
        State = AudioPlayerState.Playing;
        Calls.Add(nameof(Play));
    }

    public void Pause()
    {
        State = AudioPlayerState.Paused;
        Calls.Add(nameof(Pause));
    }

    public void Stop()
    {
        State = AudioPlayerState.Stopped;
        Calls.Add(nameof(Stop));
    }

    public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"{nameof(SwitchDeviceAsync)}:{deviceId}");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// A clock synchroniser that reports itself already converged, so a pipeline under test starts
/// without waiting.
/// </summary>
internal sealed class ConvergedClockSynchronizer : IClockSynchronizer
{
    public bool IsConverged => true;

    public bool HasMinimalSync => true;

    public double StaticDelayMs { get; set; }

    public void ProcessMeasurement(long t1, long t2, long t3, long t4)
    {
    }

    public long ClientToServerTime(long clientTime) => clientTime;

    public long ServerToClientTime(long serverTime) => serverTime;

    public void Reset()
    {
    }

    public ClockSyncStatus GetStatus() => new() { IsConverged = true, MeasurementCount = 100 };
}

/// <summary>
/// The minimal sample source a pipeline needs in order to start.
/// </summary>
internal sealed class BufferSampleSource(ITimedAudioBuffer buffer, Func<long> now) : IAudioSampleSource
{
    public AudioFormat Format => buffer.Format;

    public int Read(float[] target, int offset, int count) =>
        buffer.ReadRaw(target.AsSpan(offset, count), now());
}

/// <summary>
/// Records the commands a <see cref="PlayerCommandRouter"/> produces.
/// </summary>
internal sealed class RecordingCommandSink : IPlayerCommandSink
{
    public bool CanSend { get; set; } = true;

    public MediaSessionState CurrentState { get; set; } = MediaSessionState.Idle;

    public List<string> Commands { get; } = [];

    public List<int> Volumes { get; } = [];

    public List<bool> Mutes { get; } = [];

    public Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        Commands.Add(command);
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
    {
        Volumes.Add(volume);
        return Task.CompletedTask;
    }

    public Task SetMuteAsync(bool muted, CancellationToken cancellationToken = default)
    {
        Mutes.Add(muted);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Platform paths rooted in a temporary directory, so a config round-trip test writes real files
/// without touching the developer's own settings.
/// </summary>
internal sealed class TemporaryPaths : PlatformPathsBase, IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sendspin-tests", Guid.NewGuid().ToString("n"));

    public override string ConfigDirectory => Path.Combine(_root, "config");

    public override string DataDirectory => Path.Combine(_root, "data");

    public override string CacheDirectory => Path.Combine(_root, "cache");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
