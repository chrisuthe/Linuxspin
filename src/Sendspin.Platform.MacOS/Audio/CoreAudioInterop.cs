using System.Runtime.InteropServices;
using CoreFoundation;

namespace Sendspin.Platform.MacOS.Audio;

/// <summary>
/// The CoreAudio HAL and the raw AUHAL render callback, which the .NET macOS bindings do not
/// reach.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately small. The workload's typed bindings already cover AUHAL's lifecycle —
/// <c>AudioUnit.AudioComponent</c> finds the HAL output component, <c>AudioUnit.AudioUnit</c>
/// wraps <c>AudioComponentInstanceNew</c>, <c>AudioUnitInitialize</c>,
/// <c>AudioOutputUnitStart</c>/<c>Stop</c>, <c>AudioUnitUninitialize</c> and
/// <c>AudioComponentInstanceDispose</c>, and <c>AudioToolbox.AudioStreamBasicDescription</c> and
/// <c>AudioUnit.AudioComponentDescription</c> are the same structs Apple documents. This project
/// uses those rather than reimplementing them.
/// </para>
/// <para>
/// Two areas are genuinely missing. There is <strong>no <c>CoreAudio</c> namespace in
/// Microsoft.macOS at all</strong>, so the whole HAL property API — device enumeration, device
/// and stream latency, nominal sample rates, buffer frame size — is declared here. And
/// <c>AudioUnit.AudioUnit.SetRenderCallback</c> takes a managed <c>RenderDelegate</c> and
/// constructs an <c>AudioBuffers</c> wrapper object on every invocation, which is precisely what
/// a realtime callback must not do, so the callback is installed by writing an
/// <see cref="AURenderCallbackStruct"/> holding a raw function pointer directly to
/// <c>kAudioUnitProperty_SetRenderCallback</c>.
/// </para>
/// <para>
/// There is deliberately no <c>AudioUnitGetProperty</c> here. Every audio unit property this
/// backend reads is exposed typed, and the one candidate for a raw read — the IO buffer size —
/// is not reachable through the unit's handle anyway
/// (<c>kAudioDevicePropertyBufferFrameSize</c> returns an error there); it comes from the device
/// object instead.
/// </para>
/// <para>
/// The structs the callback receives are declared here rather than taken from
/// <c>AudioToolbox</c> for the same reason: on the realtime path there must be nothing between
/// the OS and plain blittable layout.
/// </para>
/// </remarks>
internal static unsafe partial class CoreAudioInterop
{
    /// <summary>The system-wide HAL object, the root of every hardware query.</summary>
    internal const uint SystemObject = 1;

    /// <summary>Property element for the main (only) element of a device or stream.</summary>
    internal const uint ElementMain = 0;

    internal const uint ScopeGlobal = 0x676C6F62;   // 'glob'
    internal const uint ScopeOutput = 0x6F757470;   // 'outp'

    internal const uint HardwareDevices = 0x64657623;               // 'dev#'
    internal const uint HardwareDefaultOutputDevice = 0x644F7574;   // 'dOut'
    internal const uint HardwareTranslateUidToDevice = 0x75696464;  // 'uidd'

    internal const uint ObjectName = 0x6C6E616D;                    // 'lnam'
    internal const uint DeviceUid = 0x75696420;                     // 'uid '
    internal const uint DeviceStreamConfiguration = 0x736C6179;     // 'slay'
    internal const uint DeviceStreams = 0x73746D23;                 // 'stm#'
    internal const uint DeviceNominalSampleRate = 0x6E737274;       // 'nsrt'
    internal const uint DeviceAvailableNominalSampleRates = 0x6E737223; // 'nsr#'
    internal const uint DeviceIsAlive = 0x6C69766E;                 // 'livn'
    internal const uint DeviceSafetyOffset = 0x73616674;            // 'saft'
    internal const uint DeviceBufferFrameSize = 0x6673697A;         // 'fsiz'

    /// <summary>
    /// <c>kAudioDevicePropertyLatency</c> and <c>kAudioStreamPropertyLatency</c>.
    /// </summary>
    /// <remarks>
    /// One constant because they are literally the same selector, <c>'ltnc'</c>. The two
    /// properties are distinguished only by the object queried: the device id gives the device's
    /// latency, a stream id from <see cref="DeviceStreams"/> gives the stream's. Querying it
    /// twice on the device returns the device figure twice and silently loses the stream's
    /// contribution — 690 frames, 14.4 ms, on built-in speakers.
    /// </remarks>
    internal const uint Latency = 0x6C746E63;                       // 'ltnc'

    internal const uint AudioUnitPropertySetRenderCallback = 23;
    internal const uint AudioUnitScopeInput = 1;

    /// <summary>Set in <see cref="AudioTimeStampNative.Flags"/> when the host time is usable.</summary>
    internal const uint TimeStampHostTimeValid = 2;

    private const string CoreAudioLibrary = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string AudioToolboxLibrary = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string SystemLibrary = "/usr/lib/libSystem.dylib";

    private static readonly MachTimebaseInfo Timebase = ReadTimebase();

    /// <summary>
    /// Gets the numerator that converts <c>mach_absolute_time</c> ticks to nanoseconds.
    /// </summary>
    /// <remarks>
    /// Mach host time is not nanoseconds. On Apple Silicon the timebase is 24 MHz, so ticks must
    /// be scaled by numer/denom (125/3) before they mean anything.
    /// </remarks>
    internal static uint TimebaseNumerator => Timebase.Numer;

    /// <summary>
    /// Gets the denominator that converts <c>mach_absolute_time</c> ticks to nanoseconds.
    /// </summary>
    internal static uint TimebaseDenominator => Timebase.Denom;

    /// <summary>
    /// Converts a mach host time to microseconds on the same monotonic timebase.
    /// </summary>
    internal static long HostTimeToMicroseconds(ulong hostTime) =>
        (long)(hostTime * Timebase.Numer / Timebase.Denom / 1000UL);

    /// <summary>
    /// Returns the current mach host time in microseconds.
    /// </summary>
    internal static long NowMicroseconds() => HostTimeToMicroseconds(mach_absolute_time());

    /// <summary>
    /// Reads a fixed-size property into <paramref name="value"/>.
    /// </summary>
    /// <returns>True when the property exists and has the expected size.</returns>
    internal static bool TryGetProperty<T>(uint objectId, uint selector, uint scope, out T value)
        where T : unmanaged
    {
        value = default;

        var address = new AudioObjectPropertyAddress(selector, scope, ElementMain);
        var size = (uint)sizeof(T);

        fixed (T* target = &value)
        {
            return AudioObjectGetPropertyData(objectId, &address, 0, null, &size, target) == 0
                   && size == sizeof(T);
        }
    }

    /// <summary>
    /// Reads a variable-length property as an array of <typeparamref name="T"/>, or an empty
    /// array when the property is absent.
    /// </summary>
    internal static T[] GetPropertyArray<T>(uint objectId, uint selector, uint scope)
        where T : unmanaged
    {
        var address = new AudioObjectPropertyAddress(selector, scope, ElementMain);

        if (AudioObjectGetPropertyDataSize(objectId, &address, 0, null, out var byteSize) != 0
            || byteSize < sizeof(T))
        {
            return [];
        }

        var result = new T[byteSize / sizeof(T)];
        var size = byteSize;

        fixed (T* target = result)
        {
            if (AudioObjectGetPropertyData(objectId, &address, 0, null, &size, target) != 0)
            {
                return [];
            }
        }

        return size == byteSize ? result : result[..(int)(size / sizeof(T))];
    }

    /// <summary>
    /// Reads a CFString property, or null when the property is absent.
    /// </summary>
    internal static string? GetPropertyString(uint objectId, uint selector, uint scope)
    {
        var address = new AudioObjectPropertyAddress(selector, scope, ElementMain);
        var size = (uint)sizeof(nint);
        nint handle = 0;

        if (AudioObjectGetPropertyData(objectId, &address, 0, null, &size, &handle) != 0 || handle == 0)
        {
            return null;
        }

        // The HAL hands back a retained CFStringRef, so ownership transfers here.
        return CFString.FromHandle(handle, releaseHandle: true);
    }

    /// <summary>
    /// Resolves a device UID to its current <c>AudioObjectID</c>, or zero when no device
    /// matches.
    /// </summary>
    /// <remarks>
    /// UIDs are the only device identity worth persisting: <c>AudioObjectID</c>s are assigned
    /// per boot and are reused as devices come and go.
    /// </remarks>
    internal static uint TranslateDeviceUid(string uid)
    {
        var address = new AudioObjectPropertyAddress(HardwareTranslateUidToDevice, ScopeGlobal, ElementMain);
        var cfUid = CFString.CreateNative(uid);

        try
        {
            var qualifier = (nint)cfUid;
            var size = (uint)sizeof(uint);
            uint deviceId = 0;

            var status = AudioObjectGetPropertyData(
                SystemObject, &address, (uint)sizeof(nint), &qualifier, &size, &deviceId);

            return status == 0 ? deviceId : 0;
        }
        finally
        {
            CFString.ReleaseNative(cfUid);
        }
    }

    /// <summary>
    /// Installs a raw render callback on an audio unit.
    /// </summary>
    /// <param name="audioUnit">The audio unit's native handle.</param>
    /// <param name="callback">
    /// An <c>[UnmanagedCallersOnly]</c> function pointer. Must remain valid for as long as the
    /// unit is running.
    /// </param>
    /// <param name="refCon">
    /// Unmanaged state handed to every invocation. Must not be GC-tracked memory.
    /// </param>
    /// <returns>The OSStatus from <c>AudioUnitSetProperty</c>; zero on success.</returns>
    internal static int SetRenderCallback(nint audioUnit, delegate* unmanaged[Cdecl]<
        void*, uint*, AudioTimeStampNative*, uint, uint, AudioBufferListNative*, int> callback, void* refCon)
    {
        var descriptor = new AURenderCallbackStruct
        {
            InputProc = (nint)callback,
            InputProcRefCon = refCon
        };

        return AudioUnitSetProperty(
            audioUnit,
            AudioUnitPropertySetRenderCallback,
            AudioUnitScopeInput,
            ElementMain,
            &descriptor,
            (uint)sizeof(AURenderCallbackStruct));
    }

    [LibraryImport(CoreAudioLibrary)]
    private static partial int AudioObjectGetPropertyData(
        uint objectId,
        AudioObjectPropertyAddress* address,
        uint qualifierDataSize,
        void* qualifierData,
        uint* dataSize,
        void* data);

    [LibraryImport(CoreAudioLibrary)]
    private static partial int AudioObjectGetPropertyDataSize(
        uint objectId,
        AudioObjectPropertyAddress* address,
        uint qualifierDataSize,
        void* qualifierData,
        out uint dataSize);

    [LibraryImport(AudioToolboxLibrary)]
    private static partial int AudioUnitSetProperty(
        nint audioUnit,
        uint propertyId,
        uint scope,
        uint element,
        void* data,
        uint dataSize);

    [LibraryImport(SystemLibrary)]
    private static partial ulong mach_absolute_time();

    [LibraryImport(SystemLibrary)]
    private static partial int mach_timebase_info(MachTimebaseInfo* info);

    private static MachTimebaseInfo ReadTimebase()
    {
        MachTimebaseInfo info = default;

        if (mach_timebase_info(&info) != 0 || info.Numer == 0 || info.Denom == 0)
        {
            // Identity, i.e. treat ticks as nanoseconds. Wrong on Apple Silicon, but this call
            // does not fail in practice and a zero timebase would make every reading zero.
            return new MachTimebaseInfo { Numer = 1, Denom = 1 };
        }

        return info;
    }
}

/// <summary>
/// A CoreAudio HAL property address: what to read, in which scope, on which element.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioObjectPropertyAddress
{
    public uint Selector;
    public uint Scope;
    public uint Element;

    public AudioObjectPropertyAddress(uint selector, uint scope, uint element)
    {
        Selector = selector;
        Scope = scope;
        Element = element;
    }
}

/// <summary>
/// An inclusive range of sample rates, as <c>kAudioDevicePropertyAvailableNominalSampleRates</c>
/// reports them.
/// </summary>
/// <remarks>
/// Fixed-rate hardware reports each supported rate as a degenerate range where minimum equals
/// maximum. A device with a genuine continuous range (some aggregate and virtual devices) is why
/// this is a range at all.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioValueRange
{
    public double Minimum;
    public double Maximum;
}

/// <summary>
/// <c>mach_timebase_info_data_t</c>: the rational factor from host ticks to nanoseconds.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MachTimebaseInfo
{
    public uint Numer;
    public uint Denom;
}

/// <summary>
/// One buffer of an <see cref="AudioBufferListNative"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AudioBufferNative
{
    public uint NumberChannels;
    public uint DataByteSize;
    public void* Data;
}

/// <summary>
/// <c>AudioBufferList</c> as the render callback receives it.
/// </summary>
/// <remarks>
/// The native struct declares <c>mBuffers</c> as a one-element array that is in fact
/// <c>mNumberBuffers</c> long, so buffers past the first can only be reached by pointer
/// arithmetic. <see cref="GetBuffer"/> does that.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AudioBufferListNative
{
    public uint NumberBuffers;
    public AudioBufferNative FirstBuffer;

    public static AudioBufferNative* GetBuffer(AudioBufferListNative* list, uint index) =>
        &list->FirstBuffer + index;
}

/// <summary>
/// <c>SMPTETime</c>. Present only so that <see cref="AudioTimeStampNative"/> has the right
/// layout; nothing here reads it.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SmpteTimeNative
{
    public short Subframes;
    public short SubframeDivisor;
    public uint Counter;
    public uint Type;
    public uint Flags;
    public short Hours;
    public short Minutes;
    public short Seconds;
    public short Frames;
}

/// <summary>
/// <c>AudioTimeStamp</c> as the render callback receives it.
/// </summary>
/// <remarks>
/// <see cref="HostTime"/> is the field that matters: it is the mach host time at which the first
/// frame of the requested buffer reaches the device, and the HAL has already folded the IO buffer
/// size and the device's safety offset into it.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioTimeStampNative
{
    public double SampleTime;
    public ulong HostTime;
    public double RateScalar;
    public ulong WordClockTime;
    public SmpteTimeNative SmpteTime;
    public uint Flags;
    public uint Reserved;
}

/// <summary>
/// <c>AURenderCallbackStruct</c>: the function pointer and its context.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AURenderCallbackStruct
{
    public nint InputProc;
    public void* InputProcRefCon;
}
