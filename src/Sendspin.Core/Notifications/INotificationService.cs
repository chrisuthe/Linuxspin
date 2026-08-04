namespace Sendspin.Core.Notifications;

/// <summary>
/// The kinds of notification this player raises, each independently switchable.
/// </summary>
public enum NotificationKind
{
    TrackChange,
    PlaybackState,
    ConnectionState
}

/// <summary>
/// A notification to show, already reduced to what every backend needs.
/// </summary>
/// <param name="Kind">Which user toggle governs this notification.</param>
/// <param name="Title">First line.</param>
/// <param name="Body">Second line, or null.</param>
/// <param name="ArtworkFilePath">
/// Absolute path to an image, or null. Backends that cannot show an image ignore it. On Linux
/// and Windows it becomes the notification's icon rather than an inline body image, which is
/// all either shell renders.
/// </param>
public sealed record NotificationRequest(
    NotificationKind Kind,
    string Title,
    string? Body = null,
    string? ArtworkFilePath = null);

/// <summary>
/// Shows desktop notifications.
/// </summary>
/// <remarks>
/// One <see cref="ShowAsync"/> rather than a method per event, so adding a notification does
/// not mean touching three platform implementations. Filtering against the user's per-event
/// toggles happens above this interface for the same reason.
/// </remarks>
public interface INotificationService : IAsyncDisposable
{
    /// <summary>
    /// Gets whether notifications can actually be delivered.
    /// </summary>
    /// <remarks>
    /// False is a normal outcome, not a failure: there may be no notification daemon, the user
    /// may have denied permission, or registration may be unsupported for this packaging mode.
    /// Callers must not treat it as an error, and the implementation must have logged why.
    /// </remarks>
    bool IsAvailable { get; }

    /// <summary>
    /// Connects to the platform notification service. Must not throw when unavailable; report
    /// through <see cref="IsAvailable"/> and log the reason.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows a notification. A no-op when <see cref="IsAvailable"/> is false.
    /// </summary>
    Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Withdraws any notification this service currently has on screen.
    /// </summary>
    Task WithdrawAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The notification service used when a platform has none, or when one failed to start.
/// </summary>
public sealed class NullNotificationService : INotificationService
{
    /// <inheritdoc/>
    public bool IsAvailable => false;

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc/>
    public Task WithdrawAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
