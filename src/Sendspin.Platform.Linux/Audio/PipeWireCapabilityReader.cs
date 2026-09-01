using System.Diagnostics;
using System.Text;
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
    /// <para>
    /// This is a bound on how long a wedged audio stack can stall a device list, and the caller
    /// makes it a user-visible bound: <c>SettingsViewModel.RefreshDevices</c> enumerates
    /// synchronously from its constructor and from the refresh button, on the UI thread. So the
    /// figure is chosen against what a person would notice, not against what a process needs.
    /// </para>
    /// <para>
    /// Measured, a healthy daemon answers in about 10 ms. Half a second is fifty times that — far
    /// beyond any plausible slow-but-working case — while keeping the worst case a hitch rather
    /// than a freeze. The earlier two seconds was a hang by the standards of the thread it runs on.
    /// Making enumeration properly asynchronous would remove the stall rather than bound it, but
    /// that is a change to the view model and its bindings rather than to this reader.
    /// </para>
    /// </remarks>
    private const int TimeoutMilliseconds = 500;

    private readonly ILogger _logger;

    public PipeWireCapabilityReader(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Reads the graph, or returns null when PipeWire cannot be reached.
    /// </summary>
    public PipeWireGraph? Read()
    {
        string? json;

        try
        {
            json = RunPwDump();
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

        // Both pipes are drained concurrently, and neither with a blocking read. pw-dump's
        // document is far larger than a pipe buffer, so a child blocked writing either stream
        // while this thread blocks reading the other is a deadlock that no timeout can break —
        // WaitForExit is never reached to enforce one. Draining stderr matters as much as stdout:
        // it is redirected so a warning does not land in the app's own output, and a redirected
        // pipe nobody reads is exactly the pipe that fills up.
        var stdout = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, _) => { };

        if (!process.Start())
        {
            return null;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

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

        // The parameterless overload after a timed one: it is what flushes the async readers, so
        // without it the last of the document can still be in flight when it is parsed.
        process.WaitForExit();

        return process.ExitCode == 0 ? stdout.ToString() : null;
    }
}
