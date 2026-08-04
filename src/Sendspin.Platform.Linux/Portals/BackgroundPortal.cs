using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Sendspin.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

namespace Sendspin.Platform.Linux.Portals;

/// <inheritdoc cref="IBackgroundPortal"/>
public sealed class BackgroundPortal : IBackgroundPortal
{
    private const string BackgroundInterface = "org.freedesktop.portal.Background";

    /// <summary>
    /// The portal's own cap on the status message.
    /// </summary>
    private const int MaxStatusLength = 96;

    /// <summary>
    /// How long to wait for the portal's answer. Generous because on some shells the answer is a
    /// dialog the user has to see; bounded because a portal that never answers must not leave a
    /// pending task behind.
    /// </summary>
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

    private readonly SessionBus _sessionBus;
    private readonly ILogger<BackgroundPortal> _logger;

    public BackgroundPortal(SessionBus sessionBus, ILogger<BackgroundPortal> logger)
    {
        ArgumentNullException.ThrowIfNull(sessionBus);
        ArgumentNullException.ThrowIfNull(logger);

        _sessionBus = sessionBus;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<bool> RequestBackgroundAsync(string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var connection = await _sessionBus.TryGetConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return false;
        }

        var uniqueName = connection.UniqueName;
        if (string.IsNullOrEmpty(uniqueName))
        {
            // No unique name means the bus never completed the handshake, so there is no request
            // path to predict and no point making the call.
            _logger.LogDebug("Background portal skipped: the session bus has no unique name yet");
            return false;
        }

        var token = PortalRequest.NewToken();
        var expectedPath = PortalRequest.PredictPath(uniqueName, token);
        var response = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            // Subscribed before the call: a portal that grants without prompting answers before the
            // reply to RequestBackground has even been read.
            using var watch = await connection.WatchSignalAsync<uint>(
                PortalRequest.Destination, expectedPath, PortalRequest.RequestInterface, "Response",
                PortalRequest.ReadResponseCode,
                notification => OnResponse(notification, response),
                flags: ObserverFlags.None,
                emitOnCapturedContext: false,
                state: null).ConfigureAwait(false);

            var handle = await connection.CallMethodAsync(
                CreateRequestMessage(connection, reason, token),
                static (Message message, object? state) => message.GetBodyReader().ReadObjectPathAsString(),
                null).ConfigureAwait(false);

            if (!string.Equals(handle, expectedPath, StringComparison.Ordinal))
            {
                // Pre-handle_token portal. Its answer will arrive on a path nothing is listening to,
                // so the outcome is genuinely unknown rather than refused.
                _logger.LogInformation(
                    "Background portal returned {Handle} instead of {Expected}; not waiting for its answer",
                    handle, expectedPath);
                return false;
            }

            var code = await response.Task.WaitAsync(ResponseTimeout, cancellationToken).ConfigureAwait(false);
            var granted = code == PortalRequest.SuccessResponse;

            _logger.LogInformation("Background portal answered {Code} ({Outcome})",
                code, granted ? "granted" : "refused");

            return granted;
        }
        catch (TimeoutException)
        {
            _logger.LogInformation("Background portal did not answer within {Seconds}s", ResponseTimeout.TotalSeconds);
            return false;
        }
        catch (Exception ex) when (IsPortalUnavailable(ex))
        {
            _logger.LogInformation(ex, "No background portal on this session");
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task SetStatusAsync(string status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        var connection = await _sessionBus.TryGetConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return;
        }

        var message = status.Length > MaxStatusLength ? status[..MaxStatusLength] : status;

        try
        {
            await connection.CallMethodAsync(CreateSetStatusMessage(connection, message)).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsPortalUnavailable(ex))
        {
            _logger.LogDebug(ex, "Background portal would not take a status message");
        }
    }

    /// <summary>
    /// Whether a failure means the portal is simply not there, which on a session without
    /// <c>xdg-desktop-portal</c> is the normal case.
    /// </summary>
    private static bool IsPortalUnavailable(Exception exception) =>
        exception is DBusExceptionBase or IOException or SocketException or ObjectDisposedException;

    private static MessageBuffer CreateRequestMessage(DBusConnection connection, string reason, string token)
    {
        var options = new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["handle_token"] = VariantValue.String(token),
            ["reason"] = VariantValue.String(reason),

            // Autostart is a separate permission and a separate user expectation: this asks only to
            // keep running once started.
            ["autostart"] = VariantValue.Bool(false)
        };

        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            PortalRequest.Destination, PortalRequest.ObjectPath, BackgroundInterface,
            "RequestBackground", "sa{sv}", MessageFlags.None);

        // No parent window: this is not a request made from a dialog's owner.
        writer.WriteString(string.Empty);
        writer.WriteDictionary(options);

        return writer.CreateMessage();
    }

    private static MessageBuffer CreateSetStatusMessage(DBusConnection connection, string status)
    {
        var options = new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["message"] = VariantValue.String(status)
        };

        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            PortalRequest.Destination, PortalRequest.ObjectPath, BackgroundInterface,
            "SetStatus", "a{sv}", MessageFlags.None);
        writer.WriteDictionary(options);

        return writer.CreateMessage();
    }

    private void OnResponse(Notification<uint> notification, TaskCompletionSource<uint> response)
    {
        if (notification.Type == NotificationType.Value && notification.HasValue)
        {
            response.TrySetResult(notification.Value);
            return;
        }

        _logger.LogDebug("Background portal response subscription ended: {Reason}", notification.Type);
    }
}
