using Sendspin.Core.Control;
using Sendspin.Core.MediaSession;
using Sendspin.Core.Platform;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
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

/// <summary>
/// An <see cref="IAudioPipeline"/> that does nothing, for tests that need a service constructed
/// rather than a stream played.
/// </summary>
internal sealed class InertAudioPipeline : IAudioPipeline
{
    public AudioPipelineState State => AudioPipelineState.Idle;

    public bool IsReady => false;

    public AudioBufferStats? BufferStats => null;

    public AudioFormat? CurrentFormat => null;

    public AudioFormat? OutputFormat => null;

    public int DetectedOutputLatencyMs => 0;

    /// <summary>Volumes the SDK pushed into the pipeline, oldest first.</summary>
    public List<int> Volumes { get; } = [];

    /// <summary>Mute states the SDK pushed into the pipeline, oldest first.</summary>
    public List<bool> Mutes { get; } = [];

    public event EventHandler<AudioPipelineState>? StateChanged;

    public event EventHandler<AudioPipelineError>? ErrorOccurred;

    public Task StartAsync(
        AudioFormat format,
        long? targetTimestamp = null,
        CancellationToken cancellationToken = default)
    {
        StateChanged?.Invoke(this, State);
        _ = ErrorOccurred;
        return Task.CompletedTask;
    }

    public Task StopAsync() => Task.CompletedTask;

    public void NotifyReconnect()
    {
    }

    public void Clear(long? newTargetTimestamp = null)
    {
    }

    public void ReanchorTiming()
    {
    }

    public void ProcessAudioChunk(AudioChunk chunk)
    {
    }

    public void SetVolume(int volume) => Volumes.Add(volume);

    public void SetMuted(bool muted) => Mutes.Add(muted);

    public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// An <see cref="ISendspinConnection"/> that reports whatever state a test sets, and never opens a
/// socket.
/// </summary>
/// <remarks>
/// This is the seam that makes the arbitration contract testable at all:
/// <c>SendspinClientService</c> is sealed and <c>AdoptClientInitiated</c> takes the concrete type,
/// so a client can only be produced by constructing a real one — which this makes possible without
/// a server to dial.
/// </remarks>
internal sealed class FakeConnection : ISendspinConnection
{
    public ConnectionState State { get; private set; } = ConnectionState.Connected;

    public Uri? ServerUri => new("ws://test.invalid/sendspin");

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public event EventHandler<string>? TextMessageReceived;

    public event EventHandler<ReadOnlyMemory<byte>>? BinaryMessageReceived;

    /// <summary>Moves to a new state and raises the change, as a real transport would.</summary>
    public void TransitionTo(ConnectionState state)
    {
        var old = State;
        State = state;
        StateChanged?.Invoke(
            this,
            new ConnectionStateChangedEventArgs { OldState = old, NewState = state, Reason = "test" });
    }

    public Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        _ = TextMessageReceived;
        _ = BinaryMessageReceived;
        TransitionTo(ConnectionState.Connected);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        TransitionTo(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default)
        where T : IMessage =>
        Task.CompletedTask;

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>An <see cref="IStaticDelayStore"/> held in memory.</summary>
internal sealed class InMemoryStaticDelayStore : IStaticDelayStore
{
    private double? _value;

    public double? Load() => _value;

    public void Save(double staticDelayMs) => _value = staticDelayMs;
}

/// <summary>
/// An <see cref="ISendspinConnection"/> that keeps every message the SDK sends, serialized exactly
/// as it would go on the wire, and lets a test deliver inbound frames.
/// </summary>
/// <remarks>
/// <see cref="FakeConnection"/> discards what is sent, which is right for the arbitration tests.
/// This one exists because the advertisement tests have to assert the <em>wire</em> form: the
/// promise a server reads is the JSON, not the <c>ClientCapabilities</c> object it was built from,
/// and the SDK's translation between the two is private.
/// </remarks>
internal sealed class RecordingConnection : ISendspinConnection
{
    private readonly List<string> _sent = [];

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public Uri? ServerUri => new("ws://test.invalid/sendspin");

    /// <summary>
    /// Called with each message as it is sent. The handshake is a request/response — the SDK's
    /// <c>ConnectAsync</c> does not return until a <c>server/hello</c> arrives — so a test that
    /// wants a connected client has to answer from inside the send, not after it.
    /// </summary>
    public Action<RecordingConnection, string>? OnSent { get; set; }

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public event EventHandler<string>? TextMessageReceived;

    public event EventHandler<ReadOnlyMemory<byte>>? BinaryMessageReceived;

    /// <summary>Delivers an inbound frame, as the transport's receive loop would.</summary>
    public void Receive(string json) => TextMessageReceived?.Invoke(this, json);

    /// <summary>
    /// The messages sent so far of one protocol type, newest last.
    /// </summary>
    /// <remarks>
    /// A snapshot taken under the same lock the send path writes under. The SDK's time-sync loop
    /// sends from its own task while a test polls this, so an unlocked read is an enumeration
    /// racing an append.
    /// </remarks>
    public List<string> SentOfType(string messageType)
    {
        lock (_sent)
        {
            return [.. _sent.Where(json => MessageSerializer.GetMessageType(json) == messageType)];
        }
    }

    public Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        _ = BinaryMessageReceived;
        var old = State;
        State = ConnectionState.Connected;
        StateChanged?.Invoke(
            this,
            new ConnectionStateChangedEventArgs
            {
                OldState = old,
                NewState = State,
                Reason = "test"
            });

        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        State = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public Task SendMessageAsync<T>(T message, CancellationToken cancellationToken = default)
        where T : IMessage
    {
        var json = MessageSerializer.Serialize(message);

        lock (_sent)
        {
            _sent.Add(json);
        }

        OnSent?.Invoke(this, json);
        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
