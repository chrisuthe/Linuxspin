using Microsoft.Extensions.DependencyInjection;
using Sendspin.Core.Audio;
using Sendspin.Core.MediaSession;
using Sendspin.Core.Notifications;
using Sendspin.Core.Platform;
using Sendspin.Platform.Linux.Audio;
using Sendspin.Platform.Linux.DBus;
using Sendspin.Platform.Linux.MediaSession;
using Sendspin.Platform.Linux.Notifications;
using Sendspin.Platform.Linux.Portals;
using Sendspin.SDK.Audio;

namespace Sendspin.Platform.Linux.Platform;

/// <summary>
/// Registers the Linux implementations of the Core contracts.
/// </summary>
public sealed class LinuxPlatformInitializer : IPlatformInitializer
{
    /// <inheritdoc/>
    public string PlatformName => "Linux";

    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IPlatformPaths, LinuxPaths>();

        // One session-bus connection for MPRIS, notifications and both portals.
        services.AddSingleton<SessionBus>();

        services.AddSingleton<IAudioDeviceEnumerator, OpenAlDeviceEnumerator>();

        // Transient: the SDK's pipeline builds a player per stream and disposes it when the stream
        // ends, and an OpenAL device plus context is exactly what must not outlive that.
        services.AddTransient<IAudioPlayer, OpenAlRenderPlayer>();

        services.AddSingleton<IMediaSession, MprisMediaSession>();
        services.AddSingleton<INotificationService, FreedesktopNotificationService>();

        // Available to whatever decides the app should keep running or keep the screen awake. Both
        // degrade to a logged no-op when the portal is absent, so registering them unconditionally
        // costs nothing on a session without xdg-desktop-portal.
        services.AddSingleton<IBackgroundPortal, BackgroundPortal>();
        services.AddSingleton<IInhibitPortal, InhibitPortal>();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing to do before the services are used. The XDG directories are created by the app
    /// through <see cref="IPlatformPaths.EnsureDirectoriesExist"/>; the session bus, MPRIS, the
    /// notification daemon and the portals each connect on their own first use and report
    /// unavailability rather than failing, so there is nothing here that has to succeed for the
    /// player to start.
    /// </remarks>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
