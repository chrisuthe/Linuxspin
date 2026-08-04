namespace Sendspin.Platform.Linux.Portals;

/// <summary>
/// <c>org.freedesktop.portal.Background</c>: permission to keep running without a window, and a
/// one-line status the shell can show.
/// </summary>
/// <remarks>
/// <para>
/// This is Plasma 6.7's "Background Apps" surface, and it matters here because the tray does not
/// work everywhere: GNOME needs the AppIndicator extension on every version in range, and the
/// extension owns the bus name so it disappears on a shell restart. The portal needs no extension
/// and works on GNOME plumbing too.
/// </para>
/// <para>
/// Deliberately not in <c>Sendspin.Core</c>: nothing on Windows or macOS has an equivalent, and a
/// cross-platform contract for one platform's facility is a contract that only ever has one
/// implementation.
/// </para>
/// </remarks>
public interface IBackgroundPortal
{
    /// <summary>
    /// Asks to keep running in the background.
    /// </summary>
    /// <returns>
    /// True only when the portal answered and granted the request. False covers refusal, no
    /// portal, and no answer within the timeout — all logged, none fatal.
    /// </returns>
    Task<bool> RequestBackgroundAsync(string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the status line shown beside the application in the shell's background-apps list.
    /// </summary>
    /// <remarks>
    /// The portal caps the message at 96 characters; longer text is truncated rather than
    /// rejected.
    /// </remarks>
    Task SetStatusAsync(string status, CancellationToken cancellationToken = default);
}
