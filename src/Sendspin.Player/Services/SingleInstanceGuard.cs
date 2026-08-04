using System.IO.Pipes;
using System.Text;

namespace Sendspin.Player;

/// <summary>
/// Ensures only one copy of the player runs, and lets a second launch ask the first to show
/// itself.
/// </summary>
/// <remarks>
/// <para>
/// Two instances of an audio endpoint is not merely untidy: both would advertise the same
/// persisted <c>client_id</c>, so a server would see one endpoint flapping between two
/// different players, and both would contend for the output device.
/// </para>
/// <para>
/// A named pipe rather than a mutex or a lock file, because it does both jobs at once: creating
/// the server end is the mutual exclusion, and the same pipe carries the "show yourself"
/// message. A lock file additionally has to deal with being left behind by a crash.
/// </para>
/// <para>
/// The name is per-user, not global. On a multi-user machine two people are entitled to their
/// own endpoint.
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>Message the second instance sends to ask the first to come forward.</summary>
    private const string ShowMessage = "show";

    private readonly NamedPipeServerStream? _server;
    private CancellationTokenSource? _listenCancellation;
    private Task? _listenTask;
    private bool _isDisposed;

    private SingleInstanceGuard(NamedPipeServerStream? server)
    {
        _server = server;
        IsPrimary = server is not null;
    }

    /// <summary>
    /// Gets whether this process is the one instance that should run.
    /// </summary>
    public bool IsPrimary { get; }

    /// <summary>
    /// Raised when another launch asked this instance to show its window. Fires on a background
    /// thread; the handler must marshal.
    /// </summary>
    public event EventHandler? ShowRequested;

    /// <summary>
    /// Claims the single-instance slot, returning a guard whose
    /// <see cref="IsPrimary"/> says whether this process got it.
    /// </summary>
    public static SingleInstanceGuard TryAcquire()
    {
        try
        {
            var server = new NamedPipeServerStream(
                GetPipeName(),
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            return new SingleInstanceGuard(server);
        }
        catch (IOException)
        {
            // The pipe name is taken, which is exactly how "another instance is running" is
            // reported. Not an error.
            return new SingleInstanceGuard(server: null);
        }
        catch (UnauthorizedAccessException)
        {
            return new SingleInstanceGuard(server: null);
        }
        catch (PlatformNotSupportedException)
        {
            // Named pipes are unavailable. Rather than refuse to start, allow this instance
            // through: a second player is a worse outcome than no second player, but refusing
            // to run at all is worse than both.
            return new SingleInstanceGuard(server: null) { AllowWithoutGuard = true };
        }
    }

    /// <summary>
    /// Gets whether the guard could not be established and the app should run anyway.
    /// </summary>
    /// <remarks>
    /// Distinguishes "another instance holds the slot" from "single-instance enforcement is not
    /// available here", which are different situations with different right answers.
    /// </remarks>
    public bool AllowWithoutGuard { get; private init; }

    /// <summary>
    /// Starts listening for a second launch. Call once, after
    /// <see cref="IsPrimary"/> has been confirmed.
    /// </summary>
    public void StartListening()
    {
        if (_server is null || _listenTask is not null)
        {
            return;
        }

        _listenCancellation = new CancellationTokenSource();
        _listenTask = ListenAsync(_listenCancellation.Token);
    }

    /// <summary>
    /// Asks the running instance to show its window.
    /// </summary>
    /// <remarks>
    /// Best-effort with a short timeout: if the primary is wedged or exiting, the second launch
    /// should still terminate promptly rather than hang on a pipe nobody is reading.
    /// </remarks>
    public void SignalPrimaryToShow()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", GetPipeName(), PipeDirection.Out);
            client.Connect(TimeSpan.FromSeconds(2));
            client.Write(Encoding.UTF8.GetBytes(ShowMessage));
            client.Flush();
        }
        catch (TimeoutException)
        {
            // Nobody listening. Nothing useful to do from a process that is about to exit.
        }
        catch (IOException)
        {
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        _listenCancellation?.Cancel();
        _server?.Dispose();

        // Disposing the server unblocks the listen loop; wait briefly so the task is finished
        // before the process exits rather than being torn down mid-await.
        try
        {
            _listenTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The loop's own error handling has already dealt with anything meaningful.
        }

        _listenCancellation?.Dispose();
    }

    /// <summary>
    /// Builds the per-user pipe name.
    /// </summary>
    private static string GetPipeName() => $"sendspin-player-{Environment.UserName}";

    /// <summary>
    /// Returns the pipe to the listening state, tolerating a client that has already gone.
    /// </summary>
    /// <remarks>
    /// <see cref="PipeStream.IsConnected"/> is not a usable guard here: it can flip between the test
    /// and the call, and <c>Disconnect</c> then throws <see cref="InvalidOperationException"/>. That
    /// type matched none of the loop's catch clauses, so the listen task faulted, nothing observed
    /// it, and the guard silently stopped answering — after which every later launch exited without
    /// raising the window. Catching it here keeps the loop alive.
    /// </remarks>
    private void Recycle()
    {
        try
        {
            _server?.Disconnect();
        }
        catch (InvalidOperationException)
        {
            // Already disconnected. Nothing to do.
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ShowMessage.Length];

        while (!cancellationToken.IsCancellationRequested && _server is not null)
        {
            try
            {
                await _server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var read = await _server.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read > 0)
                {
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                }

                Recycle();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (IOException)
            {
                // A client that connected and vanished. Reset and keep listening; the pipe is
                // still ours.
                Recycle();
            }
        }
    }
}
