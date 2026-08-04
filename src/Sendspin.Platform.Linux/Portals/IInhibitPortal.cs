namespace Sendspin.Platform.Linux.Portals;

/// <summary>
/// <c>org.freedesktop.portal.Inhibit</c>: stops the session idling while audio is playing.
/// </summary>
/// <remarks>
/// The portal rather than the Wayland protocol, because Avalonia's Wayland backend does not bind
/// <c>idle-inhibit</c> and the X11 path has no equivalent either. An inhibition lasts until it is
/// released, so a player that takes one must release it when playback stops.
/// </remarks>
public interface IInhibitPortal
{
    /// <summary>
    /// Gets whether an inhibition is currently held.
    /// </summary>
    bool IsInhibited { get; }

    /// <summary>
    /// Inhibits idling, replacing any inhibition this instance already holds.
    /// </summary>
    /// <returns>True when the portal accepted the request.</returns>
    Task<bool> InhibitIdleAsync(string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the inhibition. Safe to call when none is held.
    /// </summary>
    Task ReleaseAsync(CancellationToken cancellationToken = default);
}
