using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Sendspin.Platform.Linux.DBus;

/// <summary>
/// The one session-bus connection shared by MPRIS, notifications and the portals.
/// </summary>
/// <remarks>
/// <para>
/// One connection rather than three: a D-Bus connection can own bus names and make calls at the
/// same time, so three sockets to the same bus would buy nothing but three sets of teardown to
/// get wrong.
/// </para>
/// <para>
/// <strong>No session bus is a normal outcome.</strong> Inside a container, over plain ssh, or on
/// a machine with no desktop session at all there is no <c>DBUS_SESSION_BUS_ADDRESS</c>. Every
/// consumer of this type must treat a null connection as "this facility is unavailable" and carry
/// on; nothing here throws for it.
/// </para>
/// <para>
/// Auto-reconnect is deliberately off. Reconnecting would silently drop the MPRIS bus name and
/// the exported object with it, leaving an app that looks connected and is invisible to the
/// shell. A session bus that dies is a session that is ending.
/// </para>
/// </remarks>
public sealed class SessionBus : IAsyncDisposable
{
    private readonly ILogger<SessionBus> _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private DBusConnection? _connection;
    private bool _attempted;
    private bool _disposed;

    public SessionBus(ILogger<SessionBus> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Gets whether a connection has been established.
    /// </summary>
    public bool IsConnected => _connection is not null;

    /// <summary>
    /// Returns the shared connection, connecting on first use, or null when the session bus is
    /// unavailable.
    /// </summary>
    /// <remarks>
    /// The failure is logged once. Callers may retry cheaply — a failed attempt is remembered, so
    /// a machine with no bus does not pay for a connect attempt per notification.
    /// </remarks>
    public async ValueTask<DBusConnection?> TryGetConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _attempted)
        {
            return _connection;
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_disposed || _attempted)
            {
                return _connection;
            }

            _attempted = true;

            var address = DBusAddress.Session;
            if (string.IsNullOrEmpty(address))
            {
                _logger.LogInformation(
                    "No DBUS_SESSION_BUS_ADDRESS: MPRIS, notifications and portals are unavailable");
                return null;
            }

            var connection = new DBusConnection(address);

            try
            {
                await connection.ConnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DBusExceptionBase or IOException or System.Net.Sockets.SocketException)
            {
                connection.Dispose();
                _logger.LogWarning(ex, "Could not connect to the session bus at {Address}", address);
                return null;
            }

            _connection = connection;
            _logger.LogInformation("Session bus connected as {UniqueName}", connection.UniqueName);
            return connection;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        _connection?.Dispose();
        _connection = null;
        _connectGate.Dispose();

        return ValueTask.CompletedTask;
    }
}
