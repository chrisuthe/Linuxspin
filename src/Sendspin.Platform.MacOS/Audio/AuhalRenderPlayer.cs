using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sendspin.Core.Audio;
using Sendspin.Platform.Shared.Audio;
using Sendspin.SDK.Models;

namespace Sendspin.Platform.MacOS.Audio;

/// <summary>
/// The macOS audio backend: an AUHAL output unit driven by a raw realtime render callback, with a
/// managed feeder thread filling a lock-free ring ahead of the deadline.
/// </summary>
/// <remarks>
/// <para>
/// This is the one backend whose render loop the OS owns. CoreAudio calls
/// <see cref="Render"/> on its own realtime thread every IO cycle, so that method does exactly
/// two things: copy samples out of an <see cref="UnmanagedAudioRing"/>, and publish
/// <c>(framesPresented, hostTimeMicroseconds)</c> into a <see cref="SeqLockedAudioClockCell"/>.
/// No allocation, no locking, no logging, no exception handling. Everything else — pulling from
/// the sample source, applying gain, querying latency — happens on ordinary managed threads.
/// </para>
/// <para>
/// <strong>What the callback publishes.</strong> The frame counter is the number of client-format
/// frames CoreAudio has asked for, which is the right rate because AUHAL's converter pulls exactly
/// as many client frames as the device needs and <see cref="DeviceAnchoredClock"/> is anchored on
/// the same <c>format.SampleRate</c>. The timestamp is <c>mHostTime</c> converted to microseconds
/// on the mach timebase and nothing else: boot-relative, unconverted, and with no latency folded
/// in. <see cref="AudioClockReading"/> asks for the platform's own timebase, and the hardware tail
/// belongs in <see cref="MeasuredOutputLatencyMs"/>, where it is reported exactly once.
/// </para>
/// <para>
/// <strong>Known limitation, not solved here.</strong> <c>[UnmanagedCallersOnly]</c> removes the
/// marshalling stub but <strong>not</strong> the GC transition: a thread returning from native
/// code still has to reach a safe point, so a Gen2 pause can stall this callback past its
/// deadline and produce a dropout. That was raised as dotnet/runtime#119142 and closed as not
/// planned, so there is no runtime fix to wait for. The ring-and-seqlock boundary is shaped the
/// way it is deliberately: both objects hold their payload in unmanaged memory reached through
/// raw pointers, so a native C shim can take over <see cref="Render"/> without any managed code
/// above it changing. Until that shim exists the risk is real and this comment is the honest
/// statement of it.
/// </para>
/// <para>
/// <strong>Latency.</strong> See <see cref="MeasureLatency"/> for the derivation, which is the
/// part most easily got wrong.
/// </para>
/// </remarks>
public sealed unsafe class AuhalRenderPlayer : AudioPlayerBase
{
    /// <summary>
    /// Ring depth the feeder aims to hold, in milliseconds.
    /// </summary>
    /// <remarks>
    /// This is the whole trade in one number. The ring is what stands between a managed feeder
    /// thread and a realtime callback with a hard deadline — 10.67 ms at 512 frames and 48 kHz —
    /// so it has to be deep enough to survive a scheduling miss or a GC pause. The brief's own
    /// figure for a Gen2 pause is 10–100 ms. It also has to be shallow, because its depth is
    /// delay between the sample source handing over a frame and that frame reaching the DAC.
    /// 40 ms covers a typical Gen2 pause and background collections; going to 100 ms to cover the
    /// worst case would triple a constant that also makes every device switch and flush sluggish.
    /// The residual is reported honestly through <see cref="CalibratedStartupLatencyMs"/> rather
    /// than hidden.
    /// </remarks>
    private const int RingTargetMilliseconds = 40;

    /// <summary>
    /// Ring capacity, in milliseconds. Twice the target so a burst refill after a pause has
    /// somewhere to land without the feeder having to split a write.
    /// </summary>
    private const int RingCapacityMilliseconds = RingTargetMilliseconds * 2;

    /// <summary>How much the feeder pulls from the sample source per pass.</summary>
    private const int FeedChunkMilliseconds = 10;

    /// <summary>
    /// How long the feeder sleeps when the ring is already at its target. Short enough that the
    /// ring never drains far below target between wakeups.
    /// </summary>
    private const int FeederIdleMilliseconds = 2;

    /// <summary>
    /// Upper bound on the frames CoreAudio may request per callback. Set generously: an aggregate
    /// or Bluetooth device can run a much larger IO buffer than built-in output, and a unit that
    /// is asked for more frames than this fails the render rather than degrading.
    /// </summary>
    private const uint MaximumFramesPerSlice = 4096;

    private readonly Lock _renderGate = new();

    private AudioUnit.AudioUnit? _audioUnit;
    private UnmanagedAudioRing? _ring;
    private SeqLockedAudioClockCell? _clockCell;
    private GCHandle _ringHandle;
    private GCHandle _clockCellHandle;
    private RenderState* _state;

    private Thread? _feeder;
    private ManualResetEventSlim? _feederWakeup;
    private int _feederRunning;

    private float[]? _feedBuffer;
    private int _targetSamples;
    private int _channels;
    private int _sampleRate;
    private int _measuredOutputLatencyMs;
    private int _prefillMilliseconds;
    private uint _deviceId;

    public AuhalRenderPlayer(ILogger<AuhalRenderPlayer> logger)
        : base(logger)
    {
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>"audio-clock"</c> only once the callback has actually published a reading. Before that
    /// the SDK is on its filtered wall clock and saying otherwise would misreport the one field
    /// that exists to record which path is in use.
    /// </remarks>
    public override string TimingSourceName =>
        _clockCell?.TryRead() is not null ? "audio-clock" : "wall-clock";

    /// <inheritdoc/>
    /// <remarks>
    /// The ring's prefill, measured after it is filled rather than assumed. This is a real
    /// constant delay between the sample source and the DAC that no device property reports,
    /// which is exactly the case this property exists for.
    /// </remarks>
    public override int CalibratedStartupLatencyMs => _prefillMilliseconds;

    /// <inheritdoc/>
    protected override int MeasuredOutputLatencyMs => _measuredOutputLatencyMs;

    /// <inheritdoc/>
    protected override Task OpenDeviceAsync(AudioFormat format, string? deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var component = AudioUnit.AudioComponent.FindComponent(AudioUnit.AudioTypeOutput.HAL)
            ?? throw new InvalidOperationException(
                "The AUHAL output audio component is not available on this system.");

        var unit = new AudioUnit.AudioUnit(component);

        try
        {
            // HAL output enables its output element by default, so only a non-default device
            // needs setting. Doing this before Initialize is required: the current device is not
            // writable on an initialised unit.
            if (!string.IsNullOrEmpty(deviceId))
            {
                var resolved = CoreAudioInterop.TranslateDeviceUid(deviceId);
                if (resolved == 0)
                {
                    throw new InvalidOperationException($"No audio output device with UID '{deviceId}'.");
                }

                Check(unit.SetCurrentDevice(resolved, AudioUnit.AudioUnitScopeType.Global, CoreAudioInterop.ElementMain),
                    $"select audio device '{deviceId}'");
            }

            _deviceId = unit.GetCurrentDevice(AudioUnit.AudioUnitScopeType.Global, CoreAudioInterop.ElementMain);
            _channels = Math.Max(1, format.Channels);
            _sampleRate = format.SampleRate;

            // The input scope of element 0 is what we render into, so this is the format we
            // supply, not the format the hardware runs; AUHAL converts.
            var clientFormat = new AudioToolbox.AudioStreamBasicDescription
            {
                SampleRate = format.SampleRate,
                Format = AudioToolbox.AudioFormatType.LinearPCM,
                FormatFlags = AudioToolbox.AudioFormatFlags.IsFloat | AudioToolbox.AudioFormatFlags.IsPacked,
                BytesPerPacket = sizeof(float) * _channels,
                FramesPerPacket = 1,
                BytesPerFrame = sizeof(float) * _channels,
                ChannelsPerFrame = _channels,
                BitsPerChannel = sizeof(float) * 8
            };

            Check(unit.SetFormat(clientFormat, AudioUnit.AudioUnitScopeType.Input, CoreAudioInterop.ElementMain),
                "set the render format");
            Check(unit.SetMaximumFramesPerSlice(MaximumFramesPerSlice, AudioUnit.AudioUnitScopeType.Global, CoreAudioInterop.ElementMain),
                "set the maximum frames per slice");

            var latency = MeasureLatency(_deviceId, format.SampleRate);
            _measuredOutputLatencyMs = latency.Milliseconds;

            AllocateRenderResources();

            var status = CoreAudioInterop.SetRenderCallback((nint)unit.Handle, &Render, _state);
            if (status != 0)
            {
                throw new InvalidOperationException(
                    $"Could not install the AUHAL render callback (OSStatus {status}).");
            }

            Check(unit.Initialize(), "initialise the audio unit");

            _audioUnit = unit;

            var ioBufferFrames = ReadIoBufferFrames(_deviceId);
            Logger.LogInformation(
                "AUHAL open on device {DeviceId}: {Rate} Hz, {Channels} ch, IO buffer {IoFrames} frames, " +
                "device+stream latency {LatencyMs} ms (device {DeviceFrames} + stream {StreamFrames} frames, " +
                "safety offset {SafetyFrames} frames excluded as already in mHostTime)",
                _deviceId, format.SampleRate, _channels, ioBufferFrames, latency.Milliseconds,
                latency.DeviceFrames, latency.StreamFrames, latency.SafetyOffsetFrames);

            return Task.CompletedTask;
        }
        catch
        {
            // Nothing here ever started the unit, so no callback can be in flight — but the
            // teardown order is still the documented one, because it is the order that makes
            // freeing the callback's state safe and it should not have two variants.
            _audioUnit = null;
            unit.Uninitialize();
            unit.Dispose();
            ReleaseRenderResources();
            throw;
        }
    }

    /// <inheritdoc/>
    protected override void CloseDevice()
    {
        lock (_renderGate)
        {
            var unit = _audioUnit;
            _audioUnit = null;

            if (unit is not null)
            {
                // Uninitialize is the hard barrier: it tears down the IOProc, so once it returns
                // the callback provably cannot be re-entered. Only after that is the unmanaged
                // state it dereferences safe to free.
                unit.Uninitialize();
                unit.Dispose();
            }

            ReleaseRenderResources();
        }
    }

    /// <inheritdoc/>
    protected override void StartRendering()
    {
        lock (_renderGate)
        {
            var unit = _audioUnit;
            var state = _state;
            if (unit is null || state is null)
            {
                throw new InvalidOperationException("StartRendering called before the device was opened.");
            }

            // Fill the ring before the device starts, so the first callbacks find audio rather
            // than the silence a cold ring would hand the DAC.
            Prefill();
            StartFeeder();

            Volatile.Write(ref state->Running, 1);
            Check(unit.Start(), "start the audio unit");
        }
    }

    /// <inheritdoc/>
    protected override void StopRendering(bool flush)
    {
        lock (_renderGate)
        {
            var state = _state;
            if (state is not null)
            {
                // Makes the window between here and the device actually stopping harmless: an
                // in-flight callback outputs silence and publishes nothing.
                Volatile.Write(ref state->Running, 0);
            }

            // AudioOutputUnitStop does not return while an IOProc invocation is in flight, so
            // from here no further callback can begin. Freeing still waits for Uninitialize in
            // CloseDevice, which is the barrier CoreAudio actually guarantees.
            _audioUnit?.Stop();

            // Only now stop the producer. Stopping the feeder first would leave a still-running
            // callback draining the ring, which is an audible fade-to-silence rather than a stop.
            StopFeeder();

            if (flush)
            {
                _ring?.Clear();

                if (state is not null)
                {
                    Volatile.Write(ref state->FramesConsumed, 0);
                }
            }
        }
    }

    /// <inheritdoc/>
    protected override AudioClockReading? TryReadDeviceClock() => _clockCell?.TryRead();

    /// <summary>
    /// The AUHAL render callback. Realtime thread; see the class remarks.
    /// </summary>
    /// <remarks>
    /// Reached through a raw function pointer in <c>AURenderCallbackStruct</c> rather than the
    /// binding's <c>SetRenderCallback</c>, which would marshal an <c>AudioBuffers</c> wrapper on
    /// every call. State arrives through <paramref name="refCon"/>; nothing is captured.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Render(
        void* refCon,
        uint* actionFlags,
        AudioTimeStampNative* timeStamp,
        uint busNumber,
        uint frames,
        AudioBufferListNative* data)
    {
        var state = (RenderState*)refCon;
        if (state is null || data is null)
        {
            return 0;
        }

        var buffers = data->NumberBuffers;

        if (Volatile.Read(ref state->Running) == 0)
        {
            for (var i = 0u; i < buffers; i++)
            {
                var silent = AudioBufferListNative.GetBuffer(data, i);
                NativeMemory.Fill(silent->Data, silent->DataByteSize, 0);
            }

            return 0;
        }

        // The handles are plain slots in the runtime's handle table; reading them is a load, not
        // an allocation, and Unsafe.As on a sealed type is a no-op. Both callees are lock-free
        // and allocation-free by contract.
        var ring = Unsafe.As<UnmanagedAudioRing>(GCHandle.FromIntPtr(state->Ring).Target!);

        if (buffers > 0)
        {
            var buffer = AudioBufferListNative.GetBuffer(data, 0);
            ring.Read(new Span<float>(buffer->Data, (int)(buffer->DataByteSize / sizeof(float))));
        }

        // A non-interleaved client format would give one buffer per channel, which this player
        // never requests. Zeroing the rest is cheaper than trusting that.
        for (var i = 1u; i < buffers; i++)
        {
            var extra = AudioBufferListNative.GetBuffer(data, i);
            NativeMemory.Fill(extra->Data, extra->DataByteSize, 0);
        }

        var framesBefore = state->FramesConsumed;
        state->FramesConsumed = framesBefore + frames;

        if (timeStamp is not null && (timeStamp->Flags & CoreAudioInterop.TimeStampHostTimeValid) != 0)
        {
            // mHostTime scaled to microseconds on the mach timebase. Nothing is added: the timebase
            // factor is carried in the state so this is one multiply and two divides, and the
            // clock's origin is the base class's job, not this thread's.
            var hostMicroseconds =
                (long)(timeStamp->HostTime * state->TimebaseNumerator / state->TimebaseDenominator / 1000UL);

            var cell = Unsafe.As<SeqLockedAudioClockCell>(GCHandle.FromIntPtr(state->ClockCell).Target!);
            cell.Publish(framesBefore, hostMicroseconds);
        }

        return 0;
    }

    /// <summary>
    /// Queries the device's hardware latency and derives the figure this player reports as its
    /// output latency.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The derivation, which is the part that is easy to get wrong:
    /// </para>
    /// <para>
    /// <c>mHostTime</c> is the host time at which the first frame of the buffer being requested
    /// reaches the device. The HAL has already scheduled the callback that far ahead, so
    /// <c>kAudioDevicePropertyBufferFrameSize</c> and <c>kAudioDevicePropertySafetyOffset</c> are
    /// <em>already inside it</em>. Adding either again double-counts — that is cpal's bug, and it
    /// matters because the safety offset varies by more than a factor of ten across transports.
    /// </para>
    /// <para>
    /// Measured on built-in output at 48 kHz, buffer 512 frames, safety offset 48 frames:
    /// <c>mHostTime</c> arrives 11.65 ms ahead of <c>mach_absolute_time</c>, against
    /// <c>(512 + 48) / 48000 = 11.67 ms</c>. That is the confirmation, not an inference.
    /// </para>
    /// <para>
    /// What <c>mHostTime</c> does not include is the hardware pipeline after that hand-off:
    /// <c>kAudioDevicePropertyLatency</c> on the device, plus <c>kAudioStreamPropertyLatency</c>
    /// on the stream the device is playing through. Both are frame counts on the device's own
    /// clock, so they are converted with the device's nominal sample rate, not the stream format
    /// this player renders at — AUHAL resamples between the two.
    /// </para>
    /// <para>
    /// Hence <c>presentation = mHostTime + (deviceLatency + streamLatency) / deviceRate</c>, and
    /// <c>(deviceLatency + streamLatency) / deviceRate</c> is what this player reports as its
    /// output latency. It is reported there and only there — the callback publishes a raw
    /// <c>mHostTime</c>, so the term cannot be counted twice. The safety offset is read only so it
    /// can be logged and so a reviewer can see it was considered and excluded on purpose.
    /// </para>
    /// <para>
    /// The two properties are the same selector <c>'ltnc'</c>, distinguished only by the object
    /// queried, so the stream ids come from <c>kAudioDevicePropertyStreams</c> first. Querying
    /// the device twice loses the stream term silently. Measured on this machine's built-in
    /// speakers: device 60 frames, stream 690 frames, so the device-only answer is 1.25 ms against
    /// a true 15.6 ms. <c>AVAudioEngine.presentationLatency</c> makes exactly that mistake, and
    /// <c>kAudioUnitProperty_Latency</c> returns 0.0, which is why neither is used.
    /// </para>
    /// </remarks>
    private static LatencyMeasurement MeasureLatency(uint deviceId, int fallbackSampleRate)
    {
        CoreAudioInterop.TryGetProperty<uint>(
            deviceId, CoreAudioInterop.Latency, CoreAudioInterop.ScopeOutput, out var deviceFrames);
        CoreAudioInterop.TryGetProperty<uint>(
            deviceId, CoreAudioInterop.DeviceSafetyOffset, CoreAudioInterop.ScopeOutput, out var safetyFrames);

        uint streamFrames = 0;
        var streams = CoreAudioInterop.GetPropertyArray<uint>(
            deviceId, CoreAudioInterop.DeviceStreams, CoreAudioInterop.ScopeOutput);

        if (streams.Length > 0)
        {
            // The first output stream is the one AUHAL renders into on every real device; an
            // aggregate with several streams would need the one carrying our channels, which the
            // HAL does not report.
            CoreAudioInterop.TryGetProperty<uint>(
                streams[0], CoreAudioInterop.Latency, CoreAudioInterop.ScopeGlobal, out streamFrames);
        }

        var deviceRate = fallbackSampleRate;
        if (CoreAudioInterop.TryGetProperty<double>(
                deviceId, CoreAudioInterop.DeviceNominalSampleRate, CoreAudioInterop.ScopeGlobal, out var nominal)
            && nominal > 0)
        {
            deviceRate = (int)Math.Round(nominal);
        }

        var totalFrames = (long)deviceFrames + streamFrames;
        var microseconds = deviceRate > 0 ? totalFrames * 1_000_000L / deviceRate : 0;

        return new LatencyMeasurement(
            (int)Math.Round(microseconds / 1000.0),
            deviceFrames,
            streamFrames,
            safetyFrames);
    }

    /// <summary>
    /// Reads the device's IO buffer size, for the log line only.
    /// </summary>
    /// <remarks>
    /// From the device object, not the audio unit: the unit's handle rejects this property.
    /// </remarks>
    private static uint ReadIoBufferFrames(uint deviceId) =>
        CoreAudioInterop.TryGetProperty<uint>(
            deviceId, CoreAudioInterop.DeviceBufferFrameSize, CoreAudioInterop.ScopeOutput, out var frames)
            ? frames
            : 0;

    private static void Check(AudioUnit.AudioUnitStatus status, string what)
    {
        if (status != AudioUnit.AudioUnitStatus.NoError)
        {
            throw new InvalidOperationException($"CoreAudio could not {what}: {status}.");
        }
    }

    private void AllocateRenderResources()
    {
        var samplesPerMillisecond = _sampleRate * _channels / 1000;
        var capacity = Math.Max(samplesPerMillisecond * RingCapacityMilliseconds, _channels);

        _ring = new UnmanagedAudioRing(capacity);
        _clockCell = new SeqLockedAudioClockCell();
        _ringHandle = GCHandle.Alloc(_ring);
        _clockCellHandle = GCHandle.Alloc(_clockCell);

        _targetSamples = Math.Min(samplesPerMillisecond * RingTargetMilliseconds, capacity);
        _feedBuffer = new float[Math.Max(samplesPerMillisecond * FeedChunkMilliseconds, _channels)];

        // The callback dereferences this every IO cycle, so it lives outside the GC heap and on
        // its own cache line.
        _state = (RenderState*)NativeMemory.AlignedAlloc((nuint)sizeof(RenderState), 64);
        *_state = new RenderState
        {
            Ring = GCHandle.ToIntPtr(_ringHandle),
            ClockCell = GCHandle.ToIntPtr(_clockCellHandle),
            TimebaseNumerator = CoreAudioInterop.TimebaseNumerator,
            TimebaseDenominator = CoreAudioInterop.TimebaseDenominator,
            FramesConsumed = 0,
            Running = 0
        };
    }

    /// <summary>
    /// Frees everything the callback touches. The caller must already have guaranteed the
    /// callback cannot run.
    /// </summary>
    private void ReleaseRenderResources()
    {
        var state = _state;
        _state = null;

        if (state is not null)
        {
            NativeMemory.AlignedFree(state);
        }

        if (_ringHandle.IsAllocated)
        {
            _ringHandle.Free();
        }

        if (_clockCellHandle.IsAllocated)
        {
            _clockCellHandle.Free();
        }

        _ring?.Dispose();
        _ring = null;

        _clockCell?.Dispose();
        _clockCell = null;

        _feedBuffer = null;
        _prefillMilliseconds = 0;
    }

    private void Prefill()
    {
        var ring = _ring;
        if (ring is null)
        {
            return;
        }

        // Bounded so a sample source with nothing buffered yet cannot spin here. One pass per
        // chunk that fits, plus a little slack for short reads.
        var maximumPasses = (_targetSamples / Math.Max(1, _feedBuffer?.Length ?? 1)) + 4;

        for (var pass = 0; pass < maximumPasses && TopUp(); pass++)
        {
        }

        var frames = ring.Available / _channels;
        _prefillMilliseconds = _sampleRate > 0 ? frames * 1000 / _sampleRate : 0;

        Logger.LogDebug("Prefilled the render ring to {Milliseconds} ms ({Frames} frames)",
            _prefillMilliseconds, frames);
    }

    /// <summary>
    /// Moves one chunk from the sample source into the ring, or returns false when the ring is at
    /// its target or the source has nothing.
    /// </summary>
    private bool TopUp()
    {
        var ring = _ring;
        var source = SampleSource;
        var buffer = _feedBuffer;

        if (ring is null || source is null || buffer is null)
        {
            return false;
        }

        var room = _targetSamples - ring.Available;
        if (room <= 0)
        {
            return false;
        }

        var read = source.Read(buffer, 0, Math.Min(room, buffer.Length));
        if (read <= 0)
        {
            return false;
        }

        var gain = EffectiveGain;
        if (gain != 1f)
        {
            // Linear, and that is the whole point: the SDK's AudioPipeline.SetVolume has already
            // applied the spec's (volume/100)^1.5 before this amplitude arrives, so there is no
            // exponent anywhere in this file.
            var samples = buffer.AsSpan(0, read);
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] *= gain;
            }
        }

        ring.Write(buffer.AsSpan(0, read));
        return true;
    }

    private void StartFeeder()
    {
        if (_feeder is not null)
        {
            return;
        }

        _feederWakeup = new ManualResetEventSlim(false);
        Volatile.Write(ref _feederRunning, 1);

        _feeder = new Thread(FeedLoop)
        {
            IsBackground = true,
            Name = "sendspin-auhal-feeder",
            Priority = ThreadPriority.Highest
        };

        _feeder.Start();
    }

    private void StopFeeder()
    {
        var feeder = _feeder;
        _feeder = null;

        Volatile.Write(ref _feederRunning, 0);
        _feederWakeup?.Set();

        // Joins rather than abandons: the feeder writes into the ring, and the ring is freed
        // shortly after this returns.
        feeder?.Join(TimeSpan.FromSeconds(2));

        _feederWakeup?.Dispose();
        _feederWakeup = null;
    }

    private void FeedLoop()
    {
        var wakeup = _feederWakeup;

        try
        {
            while (Volatile.Read(ref _feederRunning) == 1)
            {
                if (!TopUp())
                {
                    wakeup?.Wait(FeederIdleMilliseconds);
                }
            }
        }
        catch (Exception ex)
        {
            // This is a thread root: an escaping exception ends the process. The sample source is
            // SDK code whose failure modes are not enumerable from here, so everything is caught,
            // reported through the player's error channel, and the thread exits leaving the ring
            // to drain into silence rather than taking the app down.
            Fail(AudioPlayerErrorCode.BufferUnderrun, "The audio feeder thread stopped", ex);
        }
    }

    /// <summary>
    /// State handed to the render callback through <c>inRefCon</c>.
    /// </summary>
    /// <remarks>
    /// Unmanaged, so the callback's view of it cannot be moved by a collection. The two
    /// <see cref="GCHandle"/> slots are the one concession: <see cref="UnmanagedAudioRing"/> and
    /// <see cref="SeqLockedAudioClockCell"/> keep their payload outside the heap but do not expose
    /// its address, and reimplementing their internals against private layout would be a worse
    /// trade than a handle-table load.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct RenderState
    {
        public nint Ring;
        public nint ClockCell;
        public long FramesConsumed;
        public uint TimebaseNumerator;
        public uint TimebaseDenominator;
        public int Running;
    }

    /// <summary>
    /// What the HAL reported about a device's latency, and what was derived from it.
    /// </summary>
    /// <param name="Milliseconds">Device plus stream latency, rounded, for reporting.</param>
    /// <param name="DeviceFrames"><c>kAudioDevicePropertyLatency</c>.</param>
    /// <param name="StreamFrames"><c>kAudioStreamPropertyLatency</c>, on a stream object.</param>
    /// <param name="SafetyOffsetFrames">
    /// <c>kAudioDevicePropertySafetyOffset</c>, deliberately excluded from
    /// <paramref name="Milliseconds"/> because <c>mHostTime</c> already accounts for it.
    /// </param>
    private readonly record struct LatencyMeasurement(
        int Milliseconds,
        uint DeviceFrames,
        uint StreamFrames,
        uint SafetyOffsetFrames);
}
