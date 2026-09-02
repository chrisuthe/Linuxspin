using System.Diagnostics;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Sendspin.Player.Views;

/// <summary>
/// Reports a frame loop's CPU cost, the way the shell spike measured it: the process's CPU time
/// over the frames it drew, after a warm-up. Opt-in through <see cref="Variable"/>; off, it does
/// not exist, so a running loop pays nothing for it.
/// </summary>
/// <remarks>
/// The figure is the whole process, not the loop alone, which is what the spike's table quotes
/// too and why the two compare. Read it against that table with the same window size and head.
/// </remarks>
internal sealed class FrameProfile
{
    /// <summary>Set to anything to have each backdrop loop log its cost every few seconds.</summary>
    public const string Variable = "SENDSPIN_BACKDROP_PROFILE";

    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(10);

    private readonly string _name;
    private readonly ILogger? _logger;
    private readonly Stopwatch _wall = Stopwatch.StartNew();

    private TimeSpan _cpuAtStart;
    private TimeSpan _wallAtStart;
    private long _frames;
    private bool _warm;

    private FrameProfile(string name, ILogger? logger)
    {
        _name = name;
        _logger = logger;
    }

    /// <summary>A profile for the named loop, or null when profiling is not asked for.</summary>
    public static FrameProfile? Create(string name)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
        {
            return null;
        }

        var logger = (Application.Current as App)?.Services?
            .GetService<ILoggerFactory>()?
            .CreateLogger("Sendspin.Player.Backdrop");

        return new FrameProfile(name, logger);
    }

    /// <summary>Counts a drawn frame, and reports once enough of them have gone by.</summary>
    public void Tick()
    {
        var now = _wall.Elapsed;

        if (!_warm)
        {
            if (now < WarmUp)
            {
                return;
            }

            _warm = true;
            Restart(now);
            return;
        }

        _frames++;

        var span = now - _wallAtStart;
        if (span < ReportEvery)
        {
            return;
        }

        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - _cpuAtStart).TotalMilliseconds;
        var line = $"{_name}: {_frames} frames in {span.TotalSeconds:F1} s ({_frames / span.TotalSeconds:F1} fps), {cpu / _frames:F2} CPU ms/frame";

        if (_logger is { } logger)
        {
            logger.LogInformation("{Profile}", line);
        }
        else
        {
            Console.WriteLine(line);
        }

        Restart(now);
    }

    private void Restart(TimeSpan now)
    {
        _cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;
        _wallAtStart = now;
        _frames = 0;
    }
}
