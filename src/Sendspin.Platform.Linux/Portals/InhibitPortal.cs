using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Sendspin.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

namespace Sendspin.Platform.Linux.Portals;

/// <inheritdoc cref="IInhibitPortal"/>
public sealed class InhibitPortal : IInhibitPortal
{
    private const string InhibitInterface = "org.freedesktop.portal.Inhibit";

    /// <summary>
    /// Inhibit flag 8: idle. The other flags (logout, user switch, suspend) are none of a music
    /// player's business.
    /// </summary>
    private const uint IdleFlag = 8;

    private readonly SessionBus _sessionBus;
    private readonly ILogger<InhibitPortal> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _requestPath;

    public InhibitPortal(SessionBus sessionBus, ILogger<InhibitPortal> logger)
    {
        ArgumentNullException.ThrowIfNull(sessionBus);
        ArgumentNullException.ThrowIfNull(logger);

        _sessionBus = sessionBus;
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool IsInhibited => _requestPath is not null;

    /// <inheritdoc/>
    public async Task<bool> InhibitIdleAsync(string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var connection = await _sessionBus.TryGetConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_requestPath is not null)
            {
                await CloseAsync(connection, _requestPath).ConfigureAwait(false);
                _requestPath = null;
            }

            // The Inhibit request has no Response signal to wait for: the inhibition is in force
            // from the moment the call returns and lasts until the request object is closed.
            _requestPath = await connection.CallMethodAsync(
                CreateInhibitMessage(connection, reason),
                static (Message message, object? state) => message.GetBodyReader().ReadObjectPathAsString(),
                null).ConfigureAwait(false);

            _logger.LogDebug("Idle inhibited via {RequestPath}", _requestPath);
            return true;
        }
        catch (Exception ex) when (IsPortalUnavailable(ex))
        {
            _logger.LogInformation(ex, "No inhibit portal on this session; the screen may blank during playback");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (_requestPath is null)
        {
            return;
        }

        var connection = await _sessionBus.TryGetConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var requestPath = _requestPath;
            if (requestPath is null)
            {
                return;
            }

            _requestPath = null;
            await CloseAsync(connection, requestPath).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsPortalUnavailable(Exception exception) =>
        exception is DBusExceptionBase or IOException or SocketException or ObjectDisposedException;

    private static MessageBuffer CreateInhibitMessage(DBusConnection connection, string reason)
    {
        var options = new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["handle_token"] = VariantValue.String(PortalRequest.NewToken()),
            ["reason"] = VariantValue.String(reason)
        };

        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            PortalRequest.Destination, PortalRequest.ObjectPath, InhibitInterface,
            "Inhibit", "sua{sv}", MessageFlags.None);

        writer.WriteString(string.Empty);
        writer.WriteUInt32(IdleFlag);
        writer.WriteDictionary(options);

        return writer.CreateMessage();
    }

    private async Task CloseAsync(DBusConnection connection, string requestPath)
    {
        try
        {
            await connection.CallMethodAsync(PortalRequest.CreateCloseMessage(connection, requestPath))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsPortalUnavailable(ex))
        {
            // A request the portal has already dropped — on a portal restart, for instance — cannot
            // be closed, and the inhibition it held is gone anyway.
            _logger.LogDebug(ex, "Could not close inhibit request {RequestPath}", requestPath);
        }
    }
}
