using Microsoft.Extensions.DependencyInjection;

namespace Sendspin.Core.Platform;

/// <summary>
/// Registers one platform's implementations of the Core contracts.
/// </summary>
/// <remarks>
/// <para>
/// Each platform project supplies exactly one of these. The app resolves which to use at a
/// single wiring site, and everything downstream depends only on the contracts.
/// </para>
/// <para>
/// An implementation must register something for every contract, using the null objects
/// (<see cref="Sendspin.Core.MediaSession.NullMediaSession"/>,
/// <see cref="Sendspin.Core.Notifications.NullNotificationService"/>) where the platform has
/// no such surface, so that a missing integration degrades quietly instead of failing service
/// resolution at startup.
/// </para>
/// </remarks>
public interface IPlatformInitializer
{
    /// <summary>
    /// Gets a human-readable platform name, for logs and the diagnostics view.
    /// </summary>
    string PlatformName { get; }

    /// <summary>
    /// Registers this platform's services. Called once, before any UI exists.
    /// </summary>
    void RegisterServices(IServiceCollection services);

    /// <summary>
    /// Performs platform initialisation that must happen before those services are used.
    /// </summary>
    /// <remarks>
    /// Must not throw because an optional facility is absent; log and continue. A player that
    /// refuses to start over a missing notification daemon is worse than one that starts
    /// without notifications.
    /// </remarks>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
