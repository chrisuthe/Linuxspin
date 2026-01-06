namespace SendspinClient.Linux.Services.Notifications;

/// <summary>
/// Provides desktop notification capabilities for the Sendspin client.
/// </summary>
public interface INotificationService : IAsyncDisposable
{
    /// <summary>
    /// Initializes the notification service and establishes the D-Bus connection.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous initialization operation.</returns>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Displays a notification for a track change.
    /// </summary>
    /// <param name="title">The track title.</param>
    /// <param name="artist">The artist name.</param>
    /// <param name="albumArtPath">Optional path to the album art image file.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ShowTrackChangeAsync(string title, string artist, string? albumArtPath = null);

    /// <summary>
    /// Displays a notification for connection status changes.
    /// </summary>
    /// <param name="serverName">The name of the server.</param>
    /// <param name="connected">True if connected, false if disconnected.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task ShowConnectionStatusAsync(string serverName, bool connected);

    /// <summary>
    /// Closes a notification by its ID.
    /// </summary>
    /// <param name="notificationId">The ID of the notification to close.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task CloseNotificationAsync(uint notificationId);
}
