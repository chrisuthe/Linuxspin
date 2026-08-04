using Microsoft.Extensions.DependencyInjection;
using Sendspin.Core.Audio;
using Sendspin.Core.MediaSession;
using Sendspin.Core.Notifications;
using Sendspin.Core.Platform;
using Sendspin.Platform.MacOS.Audio;
using Sendspin.Platform.MacOS.MediaSession;
using Sendspin.Platform.MacOS.Notifications;
using Sendspin.SDK.Audio;

namespace Sendspin.Platform.MacOS.Platform;

/// <summary>
/// Registers the macOS implementations of the Core contracts.
/// </summary>
/// <remarks>
/// Nothing here starts work. The notification service and the media session both have an
/// <c>InitializeAsync</c> the app calls once a UI exists, because both need the main thread and one
/// of them may put a permission dialog on screen — neither belongs in service construction.
/// </remarks>
public sealed class MacPlatformInitializer : IPlatformInitializer
{
    /// <inheritdoc/>
    public string PlatformName => "macOS";

    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IPlatformPaths, MacPaths>();
        services.AddSingleton<IAudioDeviceEnumerator, CoreAudioDeviceEnumerator>();

        // Transient: the SDK's AudioPipeline takes a Func<IAudioPlayer> and builds a player per
        // stream, disposing the previous one. A singleton would be handed back after disposal, with
        // its audio unit already uninitialised and its ring already freed.
        services.AddTransient<IAudioPlayer, AuhalRenderPlayer>();

        services.AddSingleton<IMediaSession, NowPlayingMediaSession>();
        services.AddSingleton<INotificationService, UserNotificationService>();
        services.AddSingleton<IStatusItemPresenter, StatusItemPresenter>();
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Every macOS facility this backend uses is either always present (CoreAudio, MediaPlayer)
        // or reports its own availability (UserNotificationService). There is nothing to probe here
        // that would not be a duplicate check.
        return Task.CompletedTask;
    }
}
