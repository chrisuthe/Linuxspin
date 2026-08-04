using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sendspin.Core.Audio;
using Sendspin.Core.MediaSession;
using Sendspin.Core.Notifications;
using Sendspin.Core.Platform;
using Sendspin.Platform.Windows.Audio;
using Sendspin.Platform.Windows.MediaSession;
using Sendspin.Platform.Windows.Notifications;
using Sendspin.SDK.Audio;

namespace Sendspin.Platform.Windows.Platform;

/// <summary>
/// Registers the Windows implementations of the Core contracts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What the app must supply.</strong> Three of these services need the main window's
/// handle, and this project deliberately references no UI framework. They take a
/// <c>Func&lt;nint?&gt;</c>, which the app registers in the same container:
/// </para>
/// <code>
/// services.AddSingleton&lt;Func&lt;nint?&gt;&gt;(() =&gt; mainWindow.TryGetPlatformHandle()?.Handle);
/// </code>
/// <para>
/// Without it the media session, the taskbar badge and notifications each stay inactive and each
/// say so in the log, rather than failing service resolution or throwing at startup.
/// </para>
/// </remarks>
public sealed class WindowsPlatformInitializer : IPlatformInitializer
{
    /// <inheritdoc/>
    public string PlatformName => "Windows";

    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IPlatformPaths, WindowsPaths>();
        services.AddSingleton<IAudioDeviceEnumerator, WasapiDeviceEnumerator>();

        // Transient: the SDK's AudioPipeline takes a Func<IAudioPlayer> and builds one player per
        // stream. Each owns an endpoint handle, a preallocated buffer and a render thread, so
        // sharing one across streams would have a second stream reinitialise the device under the
        // first.
        services.AddTransient<IAudioPlayer, WasapiRenderPlayer>();

        services.AddSingleton<IMediaSession>(provider => new SmtcMediaSession(
            ResolveWindowHandleProvider(provider),
            provider.GetRequiredService<ILogger<SmtcMediaSession>>()));

        services.AddSingleton<INotificationService>(provider => new ShellBalloonNotificationService(
            ResolveWindowHandleProvider(provider),
            provider.GetRequiredService<ILogger<ShellBalloonNotificationService>>()));

        // Not behind a contract: the taskbar badge is a Windows-only surface with no counterpart
        // on the other platforms, so the app resolves it by type where it is running on Windows.
        services.AddSingleton(provider => new TaskbarTransport(
            ResolveWindowHandleProvider(provider),
            provider.GetRequiredService<ILogger<TaskbarTransport>>()));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing to do. WASAPI and SMTC are both activated on demand from the calls that use them,
    /// and the runtime initialises COM per thread, so there is no process-wide Windows setup this
    /// player depends on.
    /// </remarks>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the app's window-handle accessor, or one that reports no window.
    /// </summary>
    /// <remarks>
    /// Absence is not an error: a headless or pre-window container resolves these services fine
    /// and each reports itself inactive.
    /// </remarks>
    private static Func<nint?> ResolveWindowHandleProvider(IServiceProvider provider) =>
        provider.GetService<Func<nint?>>() ?? (static () => null);
}
