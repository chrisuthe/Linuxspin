using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sendspin.Core.Audio;

namespace Sendspin.Platform.Linux.Audio;

/// <summary>
/// Reads the PipeWire graph by running <c>pw-dump</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a subprocess rather than libpipewire.</strong> Binding libpipewire means running a
/// main loop, a roundtrip and a registry listener on a background thread just to answer a question
/// asked once per device enumeration. <c>pw-dump</c> ships with the daemon, emits exactly this
/// document, and needs no device to be opened — which is the property that made PipeWire the right
/// source in the first place. The cost is one short-lived process per enumeration.
/// </para>
/// <para>
/// Every failure — no <c>pw-dump</c> on PATH, a wedged daemon, unparseable output — resolves to
/// null, which the caller reads as "PipeWire cannot answer" and falls back to today's behaviour.
/// A plain-ALSA or PulseAudio box takes that path and is not degraded by it.
/// </para>
/// </remarks>
public sealed class PipeWireCapabilityReader
{
    /// <summary>
    /// How long <c>pw-dump</c> is given before it is abandoned.
    /// </summary>
    /// <remarks>
    /// Enumeration blocks the caller, so this is a bound on how long a broken audio stack can
    /// stall a device list. Two seconds is far above the few milliseconds a healthy daemon takes
    /// and far below anything a person would sit through.
    /// </remarks>
    private const int TimeoutMilliseconds = 2_000;

    private readonly ILogger _logger;
    private readonly Func<string?> _dump;

    public PipeWireCapabilityReader(ILogger logger)
        : this(logger, RunPwDump)
    {
    }

    /// <summary>
    /// Test seam: takes the document from <paramref name="dump"/> instead of running a process.
    /// </summary>
    internal PipeWireCapabilityReader(ILogger logger, Func<string?> dump)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(dump);
        _logger = logger;
        _dump = dump;
    }

    /// <summary>
    /// Reads the graph, or returns null when PipeWire cannot be reached.
    /// </summary>
    public PipeWireGraph? Read()
    {
        string? json;

        try
        {
            json = _dump();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException
                                       or IOException
                                       or ObjectDisposedException
                                       or PlatformNotSupportedException)
        {
            _logger.LogDebug(ex, "pw-dump could not be run; PipeWire capabilities are unavailable");
            return null;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var graph = PipeWireCapabilityParser.Parse(json);

        if (graph is null)
        {
            _logger.LogDebug("pw-dump output could not be parsed; PipeWire capabilities are unavailable");
        }

        return graph;
    }

    private static string? RunPwDump()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pw-dump",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            return null;
        }

        // Read before waiting: pw-dump's document is far larger than a pipe buffer, so waiting
        // first would deadlock against a child blocked writing it.
        var stdout = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(TimeoutMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited between the timeout and the kill; nothing to do.
            }

            return null;
        }

        return process.ExitCode == 0 ? stdout : null;
    }
}
