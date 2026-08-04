using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Sendspin.Core.Notifications;
using Sendspin.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

namespace Sendspin.Platform.Linux.Notifications;

/// <summary>
/// Shows notifications through <c>org.freedesktop.Notifications</c> on the session bus.
/// </summary>
/// <remarks>
/// <para>
/// Direct D-Bus rather than a <c>notify-send</c> shell-out. The shell-out cannot pass actions,
/// cannot pass image data, cannot close a notification by id, costs a process per toast, and does
/// not exist inside a Flatpak at all.
/// </para>
/// <para>
/// <strong>Artwork becomes the icon.</strong> On both shells <c>image-path</c> replaces the
/// notification's icon; neither renders an inline body image, and GNOME renders no hyperlinks
/// either. So the body is plain text that reads correctly with no picture at all.
/// </para>
/// <para>
/// Successive notifications replace rather than stack, through <c>replaces_id</c>. A player that
/// posts a fresh toast per track leaves a column of stale "now playing" cards behind it.
/// </para>
/// </remarks>
public sealed class FreedesktopNotificationService : INotificationService
{
    private const string ServiceName = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";
    private const string InterfaceName = "org.freedesktop.Notifications";

    /// <summary>
    /// The name shown as the notification's source, and the desktop file it is attributed to.
    /// </summary>
    private const string ApplicationName = "Sendspin";

    private const string ApplicationId = "io.sendspin.client";

    /// <summary>
    /// Let the shell choose how long to show a toast. GNOME ignores the field entirely, and a
    /// value chosen here would only override KDE's user preference.
    /// </summary>
    private const int DaemonDefaultTimeout = -1;

    /// <summary>Normal urgency, from the notification specification's hint table.</summary>
    private const byte NormalUrgency = 1;

    private readonly SessionBus _sessionBus;
    private readonly ILogger<FreedesktopNotificationService> _logger;

    private DBusConnection? _connection;
    private IDisposable? _actionInvokedWatch;
    private IDisposable? _activationTokenWatch;
    private IDisposable? _closedWatch;
    private uint _currentNotificationId;
    private string? _lastActivationToken;
    private bool _isAvailable;
    private bool _disposed;

    public FreedesktopNotificationService(SessionBus sessionBus, ILogger<FreedesktopNotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(sessionBus);
        ArgumentNullException.ThrowIfNull(logger);

        _sessionBus = sessionBus;
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool IsAvailable => _isAvailable;

    /// <summary>
    /// Gets the most recent XDG activation token offered by the daemon, or null.
    /// </summary>
    /// <remarks>
    /// Spec 1.3 emits <c>ActivationToken(u, s)</c> immediately <em>before</em> <c>ActionInvoked</c>,
    /// and under Wayland it is what lets the application legitimately raise its window in response
    /// to a click. Recorded here for whoever raises the window; nothing in this class uses it,
    /// because a notification service has no business focusing windows.
    /// </remarks>
    public string? LastActivationToken => _lastActivationToken;

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _isAvailable)
        {
            return;
        }

        var connection = await _sessionBus.TryGetConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            _logger.LogInformation("Notifications unavailable: no session bus");
            return;
        }

        try
        {
            var capabilities = await GetCapabilitiesAsync(connection).ConfigureAwait(false);

            // Logged once at information level on purpose: which capabilities exist is genuinely
            // per-shell (GNOME has no inline images, KDE has inline-reply) and it is the first
            // thing worth knowing in a bug report about a notification that looked wrong.
            _logger.LogInformation("Notification daemon capabilities: {Capabilities}",
                capabilities.Length == 0 ? "(none reported)" : string.Join(", ", capabilities));

            await SubscribeAsync(connection).ConfigureAwait(false);

            _connection = connection;
            _isAvailable = true;
        }
        catch (Exception ex) when (IsBusFailure(ex))
        {
            _logger.LogWarning(ex, "No notification daemon answered on {Service}", ServiceName);
        }
    }

    /// <inheritdoc/>
    public async Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = _connection;
        if (_disposed || !_isAvailable || connection is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            _currentNotificationId = await NotifyAsync(connection, request).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBusFailure(ex))
        {
            _logger.LogWarning(ex, "Could not show the {Kind} notification", request.Kind);
        }
    }

    /// <inheritdoc/>
    public async Task WithdrawAsync(CancellationToken cancellationToken = default)
    {
        var connection = _connection;
        var id = _currentNotificationId;

        if (_disposed || connection is null || id == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await connection.CallMethodAsync(CreateCloseMessage(connection, id)).ConfigureAwait(false);
            _currentNotificationId = 0;
        }
        catch (Exception ex) when (IsBusFailure(ex))
        {
            _logger.LogDebug(ex, "Could not close notification {Id}", id);
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
        _isAvailable = false;

        _actionInvokedWatch?.Dispose();
        _activationTokenWatch?.Dispose();
        _closedWatch?.Dispose();

        // The connection belongs to SessionBus; only the subscriptions are ours to release.
        _connection = null;

        return ValueTask.CompletedTask;
    }

    private static bool IsBusFailure(Exception exception) =>
        exception is DBusExceptionBase or IOException or SocketException or ObjectDisposedException;

    /// <summary>
    /// Builds a <c>CloseNotification</c> call.
    /// </summary>
    /// <remarks>
    /// A separate method because <see cref="MessageWriter"/> is a by-reference struct that cannot
    /// survive an <c>await</c>: the message has to be finished before the call is made.
    /// </remarks>
    private static MessageBuffer CreateCloseMessage(DBusConnection connection, uint id)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, ObjectPath, InterfaceName, "CloseNotification", "u", MessageFlags.None);
        writer.WriteUInt32(id);
        return writer.CreateMessage();
    }

    private static MessageBuffer CreateCapabilitiesMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, ObjectPath, InterfaceName, "GetCapabilities", null, MessageFlags.None);
        return writer.CreateMessage();
    }

    /// <summary>
    /// Reads a <c>(u, s)</c> signal body, the shape of both <c>ActionInvoked</c> and
    /// <c>ActivationToken</c>.
    /// </summary>
    private static (uint Id, string Value) ReadIdAndString(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        return (reader.ReadUInt32(), reader.ReadString());
    }

    /// <summary>
    /// Reads a <c>NotificationClosed(u, u)</c> body: the id and the close reason.
    /// </summary>
    private static (uint Id, uint Reason) ReadClosed(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        return (reader.ReadUInt32(), reader.ReadUInt32());
    }

    private static Task<string[]> GetCapabilitiesAsync(DBusConnection connection) =>
        connection.CallMethodAsync(
            CreateCapabilitiesMessage(connection),
            static (Message message, object? state) => message.GetBodyReader().ReadArrayOfString(),
            null);

    private async Task SubscribeAsync(DBusConnection connection)
    {
        // ActivationToken is emitted immediately before ActionInvoked, so it is subscribed first:
        // registering in the other order leaves a window where a click delivers the action with no
        // token to raise the window with.
        _activationTokenWatch = await connection.WatchSignalAsync<(uint Id, string Value)>(
            ServiceName, ObjectPath, InterfaceName, "ActivationToken",
            ReadIdAndString,
            OnActivationToken,
            flags: ObserverFlags.None,
            emitOnCapturedContext: false,
            state: null).ConfigureAwait(false);

        _actionInvokedWatch = await connection.WatchSignalAsync<(uint Id, string Value)>(
            ServiceName, ObjectPath, InterfaceName, "ActionInvoked",
            ReadIdAndString,
            OnActionInvoked,
            flags: ObserverFlags.None,
            emitOnCapturedContext: false,
            state: null).ConfigureAwait(false);

        _closedWatch = await connection.WatchSignalAsync<(uint Id, uint Reason)>(
            ServiceName, ObjectPath, InterfaceName, "NotificationClosed",
            ReadClosed,
            OnNotificationClosed,
            flags: ObserverFlags.None,
            emitOnCapturedContext: false,
            state: null).ConfigureAwait(false);
    }

    private Task<uint> NotifyAsync(DBusConnection connection, NotificationRequest request) =>
        connection.CallMethodAsync(
            CreateNotifyMessage(connection, request),
            static (Message message, object? state) => message.GetBodyReader().ReadUInt32(),
            null);

    private MessageBuffer CreateNotifyMessage(DBusConnection connection, NotificationRequest request)
    {
        var hints = new Dictionary<string, VariantValue>(StringComparer.Ordinal)
        {
            ["urgency"] = VariantValue.Byte(NormalUrgency),

            // Lets a shell attribute the toast to the installed desktop file, which is how it
            // finds an icon and how KDE groups notifications by application.
            ["desktop-entry"] = VariantValue.String(ApplicationId)
        };

        if (FileUrl.TryCreate(request.ArtworkFilePath, out var artworkUrl))
        {
            hints["image-path"] = VariantValue.String(artworkUrl);
        }

        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, ObjectPath, InterfaceName, "Notify", "susssasa{sv}i", MessageFlags.None);
        writer.WriteString(ApplicationName);

        // Replace the toast already on screen rather than adding to a stack of stale ones. Zero
        // on the first call, which the daemon reads as "this is a new notification".
        writer.WriteUInt32(_currentNotificationId);

        writer.WriteString(ApplicationId);
        writer.WriteString(request.Title);
        writer.WriteString(request.Body ?? string.Empty);

        // No actions: nothing this player shows a toast for has a second thing for the user to
        // choose. The ActivationToken and ActionInvoked signals are still handled, because the
        // default click on the body arrives as one.
        writer.WriteArray(Array.Empty<string>());

        writer.WriteDictionary(hints);
        writer.WriteInt32(DaemonDefaultTimeout);

        return writer.CreateMessage();
    }

    private void OnActivationToken(Notification<(uint Id, string Value)> notification)
    {
        if (!TryReadSignal(notification, "ActivationToken", out var signal))
        {
            return;
        }

        _lastActivationToken = signal.Value;
        _logger.LogDebug("Notification {Id} offered an activation token", signal.Id);
    }

    private void OnActionInvoked(Notification<(uint Id, string Value)> notification)
    {
        if (!TryReadSignal(notification, "ActionInvoked", out var signal))
        {
            return;
        }

        _logger.LogDebug("Notification {Id} action {Action} invoked", signal.Id, signal.Value);
    }

    private void OnNotificationClosed(Notification<(uint Id, uint Reason)> notification)
    {
        if (!TryReadSignal(notification, "NotificationClosed", out var signal))
        {
            return;
        }

        if (signal.Id == _currentNotificationId)
        {
            _currentNotificationId = 0;
        }
    }

    /// <summary>
    /// Unwraps a signal notification, logging and rejecting anything that is not a delivered value.
    /// </summary>
    private bool TryReadSignal<T>(Notification<T> notification, string signalName, out T value)
    {
        if (notification.Type == NotificationType.Value && notification.HasValue)
        {
            value = notification.Value;
            return true;
        }

        value = default!;
        _logger.LogDebug("{Signal} subscription ended: {Reason}", signalName, notification.Type);
        return false;
    }
}
