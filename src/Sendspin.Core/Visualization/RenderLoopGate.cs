namespace Sendspin.Core.Visualization;

/// <summary>
/// Tracks whether a per-frame loop should be running, and calls the start and stop actions on
/// the transitions only.
/// </summary>
/// <remarks>
/// The inputs to "should this run" arrive from several places (attachment, visibility, the
/// backdrop mode, a palette arriving), and each of them re-evaluates the whole condition. Doing
/// the transition here is what keeps a loop from being started twice, or stopped when it never
/// ran. Ported from the WPF player.
/// </remarks>
public sealed class RenderLoopGate
{
    private readonly Action _start;
    private readonly Action _stop;

    private bool _running;

    /// <param name="start">Called when the loop goes from stopped to running.</param>
    /// <param name="stop">Called when the loop goes from running to stopped.</param>
    public RenderLoopGate(Action start, Action stop)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(stop);

        _start = start;
        _stop = stop;
    }

    /// <summary>Gets whether the loop is running.</summary>
    public bool IsRunning => _running;

    /// <summary>Starts or stops the loop when <paramref name="shouldRun"/> differs from its state.</summary>
    public void Update(bool shouldRun)
    {
        if (shouldRun == _running)
        {
            return;
        }

        _running = shouldRun;
        if (shouldRun)
        {
            _start();
        }
        else
        {
            _stop();
        }
    }
}
