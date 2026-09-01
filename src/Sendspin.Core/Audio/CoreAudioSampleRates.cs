namespace Sendspin.Core.Audio;

/// <summary>
/// A closed range of sample rates, as CoreAudio's <c>AudioValueRange</c> reports them. A device
/// with discrete rates reports one degenerate range per rate; aggregate and virtual devices report
/// genuinely continuous ones.
/// </summary>
public readonly record struct SampleRateRange(double Minimum, double Maximum);

/// <summary>
/// Works out which sample rates a CoreAudio device runs natively, per the contract on
/// <see cref="AudioDeviceInfo.SupportedSampleRates"/>.
/// </summary>
/// <remarks>
/// <para>
/// This lives in Core rather than in the macOS backend for the same reason the PipeWire parsing
/// does: it is a decision, not an adapter, and the test project can only reach decisions. The
/// CoreAudio property reads stay in <c>CoreAudioDeviceEnumerator</c>.
/// </para>
/// <para>
/// <strong>The decision, and the bug it fixes.</strong> <c>DeviceAvailableNominalSampleRates</c>
/// lists the rates a device could be <em>switched</em> to. It is not the set of rates that reach
/// the converter unaltered, because switching is something the player would have to do and
/// <c>AuhalRenderPlayer</c> never does it — it reads <c>DeviceNominalSampleRate</c> for latency
/// arithmetic and never sets it. Reporting the whole list made a Mac running at 48 kHz whose
/// output also lists 96 kHz advertise a 96 kHz format ahead of 48 kHz, which CoreAudio then
/// resampled straight back down: more bytes over the network to reach a resampler, at no gain in
/// depth.
/// </para>
/// <para>
/// So the native set is the nominal rate alone. If this player ever sets the nominal rate, the
/// available list becomes the right source and this is where that change belongs.
/// </para>
/// </remarks>
public static class CoreAudioSampleRates
{
    /// <summary>
    /// How far a reported range bound may sit from the nominal rate and still be taken to contain
    /// it. CoreAudio carries these as doubles, so an exact comparison is not safe.
    /// </summary>
    private const double RateTolerance = 1.0;

    /// <summary>
    /// Returns the rates the device renders without conversion.
    /// </summary>
    /// <param name="nominalRate">
    /// The device's current nominal rate, from <c>DeviceNominalSampleRate</c>. 0 when it could not
    /// be read.
    /// </param>
    /// <param name="availableRanges">
    /// What <c>DeviceAvailableNominalSampleRates</c> reported. Used only to confirm the device
    /// admits its own nominal rate — never to widen the answer.
    /// </param>
    public static IReadOnlyList<int> ResolveNative(int nominalRate, IReadOnlyList<SampleRateRange> availableRanges)
    {
        ArgumentNullException.ThrowIfNull(availableRanges);

        if (nominalRate <= 0)
        {
            // No nominal rate means nothing is known about what this device runs. Empty is the
            // honest answer; the advertisement falls back rather than guessing.
            return [];
        }

        if (availableRanges.Count == 0)
        {
            // The range query is unavailable on some devices. The nominal rate is still the rate
            // the device is running at, which is the whole of what this method claims.
            return [nominalRate];
        }

        foreach (var range in availableRanges)
        {
            if (nominalRate >= range.Minimum - RateTolerance && nominalRate <= range.Maximum + RateTolerance)
            {
                return [nominalRate];
            }
        }

        // A device that lists ranges and excludes the rate it says it is running is reporting
        // something inconsistent. Claiming nothing is the safe reading of that.
        return [];
    }
}
