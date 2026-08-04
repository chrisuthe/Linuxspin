using Microsoft.Extensions.Logging;

namespace Sendspin.Platform.Shared.Client;

/// <summary>
/// Tracks background tasks so that every one is cancellable and every failure is observed.
/// </summary>
/// <remarks>
/// <para>
/// The pattern this replaces is <c>_ = Task.Run(...)</c>. That is not merely untidy: the task
/// has no owner, so nothing cancels it at shutdown, and if it throws, the exception is
/// swallowed by the task's unobserved state and the work simply stops happening with no trace.
/// The old code did it in three places, one of them from a constructor.
/// </para>
/// <para>
/// Work started here is bound to a shared <see cref="CancellationToken"/>, kept in a set until
/// it completes, and logged if it faults. <see cref="DisposeAsync"/> cancels and then waits, so
/// shutdown is ordered rather than racing whatever the tasks were touching.
/// </para>
/// </remarks>
public sealed class BackgroundTaskSet : IAsyncDisposable
{
    /// <summary>
    /// How long shutdown waits for tracked work. A task that ignores its token must not be
    /// able to stop the app exiting, but it should be reported when it does.
    /// </summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _running = [];

    private bool _disposed;

    public BackgroundTaskSet(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Gets the token every tracked task observes. Cancelled by <see cref="DisposeAsync"/>.
    /// </summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>
    /// Starts <paramref name="work"/> and tracks it.
    /// </summary>
    /// <param name="description">
    /// What the work is, used in the log line if it faults. Worth writing properly: it is the
    /// only context a reader of that log will have.
    /// </param>
    /// <param name="work">The work, which must observe the token it is handed.</param>
    public void Run(string description, Func<CancellationToken, Task> work)
    {
        ArgumentException.ThrowIfNullOrEmpty(description);
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var task = RunTrackedAsync(description, work);

        lock (_gate)
        {
            _running.Add(task);
        }
    }

    /// <summary>
    /// Cancels every tracked task and waits for them to finish.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cancellation.CancelAsync().ConfigureAwait(false);

        Task[] pending;
        lock (_gate)
        {
            pending = [.. _running];
        }

        if (pending.Length > 0)
        {
            await Task.WhenAny(
                Task.WhenAll(pending),
                Task.Delay(ShutdownTimeout, CancellationToken.None)).ConfigureAwait(false);

            var stillRunning = pending.Count(task => !task.IsCompleted);
            if (stillRunning > 0)
            {
                _logger.LogWarning(
                    "{Count} background task(s) did not stop within {Timeout}; continuing shutdown",
                    stillRunning, ShutdownTimeout);
            }
        }

        _cancellation.Dispose();
    }

    private async Task RunTrackedAsync(string description, Func<CancellationToken, Task> work)
    {
        try
        {
            await work(_cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            _logger.LogDebug("Background task cancelled: {Description}", description);
        }
        catch (Exception ex)
        {
            // The whole point of tracking: without this the exception would be unobserved and
            // the work would silently stop.
            _logger.LogError(ex, "Background task failed: {Description}", description);
        }
        finally
        {
            lock (_gate)
            {
                _running.RemoveWhere(t => t.IsCompleted);
            }
        }
    }
}
