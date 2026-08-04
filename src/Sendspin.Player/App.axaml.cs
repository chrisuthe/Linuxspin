using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sendspin.Core.Audio;
using Sendspin.Core.Configuration;
using Sendspin.Core.Control;
using Sendspin.Core.Diagnostics;
using Sendspin.Core.MediaSession;
using Sendspin.Core.Platform;
using Sendspin.Core.Presence;
using Sendspin.Discord;
using Sendspin.Platform.Shared.Client;
using Sendspin.Platform.Shared.Media;
using Sendspin.Platform.Shared.Notifications;
using Sendspin.Player.ViewModels;
using Sendspin.Player.Views;
using Sendspin.SDK.Client;

namespace Sendspin.Player;

/// <summary>
/// The Avalonia application: builds the service container, creates the window and the tray, and
/// owns shutdown.
/// </summary>
public sealed partial class App : Application
{
    private ServiceProvider? _services;
    private MainWindow? _mainWindow;
    private TrayIconController? _tray;
    private bool _shutdownStarted;

    /// <summary>
    /// Gets or sets the single-instance guard, handed over by the entry point.
    /// </summary>
    /// <remarks>
    /// A static because the guard has to be claimed before Avalonia exists — deciding whether to
    /// start at all cannot wait until the framework has initialised.
    /// </remarks>
    internal static SingleInstanceGuard? SingleInstance { get; set; }

    /// <summary>
    /// Gets the service container, or null before it is built and after shutdown has released it.
    /// </summary>
    /// <remarks>
    /// Views resolve through this rather than taking constructor dependencies, because Avalonia
    /// constructs them from XAML. Nullable on purpose: null means "not running", which the
    /// designer and shutdown both are.
    /// </remarks>
    internal IServiceProvider? Services => _services;

    /// <inheritdoc/>
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc/>
    public override void OnFrameworkInitializationCompleted()
    {
        if (Design.IsDesignMode)
        {
            // The designer instantiates the application but has no platform services and no
            // desktop lifetime. Building the container here would try to open an audio device.
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var platform = PlatformSelection.CreateInitializer();
        _services = BuildServices(platform);

        var logger = _services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("Sendspin Player {Version} starting on {Platform}",
            AppVersion, platform.PlatformName);

        _services.GetRequiredService<IPlatformPaths>().EnsureDirectoriesExist();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Shut down on an explicit request only. Closing the window hides to tray when the
            // user has asked for that, and an endpoint whose window is shut should keep playing.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var viewModel = _services.GetRequiredService<MainViewModel>();
            _mainWindow = new MainWindow { DataContext = viewModel };

            _tray = _services.GetRequiredService<TrayIconController>();
            _tray.Attach(viewModel);

            if (SingleInstance is { } guard)
            {
                guard.ShowRequested += OnShowRequested;
                guard.StartListening();
            }

            var settings = _services.GetRequiredService<SettingsService>().Current;
            if (!settings.StartMinimizedToTray)
            {
                _mainWindow.Show();
            }
            else
            {
                logger.LogInformation("Starting minimized to tray");
            }

            desktop.ShutdownRequested += OnShutdownRequested;

            // Start the session after the window exists, so its first state report has somewhere
            // to land. Failures here are surfaced on the view model rather than thrown into the
            // framework's initialisation path.
            viewModel.BeginStartup(platform);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Brings the main window forward, creating nothing: it always exists once started.
    /// </summary>
    internal void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            _mainWindow.Show();

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Activate();
        });
    }

    /// <summary>
    /// Begins an orderly shutdown.
    /// </summary>
    internal void RequestShutdown()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>
    /// Gets this build's informational version, for the protocol's <c>device_info</c>.
    /// </summary>
    private static string AppVersion =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version?.ToString()
        ?? "1.0.0";

    /// <summary>
    /// Builds the service container.
    /// </summary>
    /// <remarks>
    /// Platform services are registered first so that the shared registrations below can depend
    /// on them, and so a platform is free to replace a default.
    /// </remarks>
    private static ServiceProvider BuildServices(IPlatformInitializer platform)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddSimpleConsole(options => options.SingleLine = true);
        });

        platform.RegisterServices(services);
        services.AddSingleton(platform);

        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<IStaticDelayStore, SettingsStaticDelayStore>();
        services.AddSingleton<ArtworkCache>();
        services.AddSingleton<NotificationDispatcher>();

        // The correction limits are a shipped default, deliberately not a user setting: they are
        // buffer internals, and the only calibration the UI exposes is static delay and the
        // per-device latency offset.
        services.AddSingleton(new SyncCorrectionPolicy());

        services.AddSingleton<IPresenceService, DiscordPresenceService>();

        services.AddSingleton(provider => new SendspinPlayerService(
            provider.GetRequiredService<ILoggerFactory>(),
            provider.GetRequiredService<SettingsService>(),
            provider.GetRequiredService<IStaticDelayStore>(),
            provider.GetRequiredService<IAudioDeviceEnumerator>(),
            provider.GetRequiredService<Sendspin.SDK.Audio.IAudioPlayer>,
            provider.GetRequiredService<ArtworkCache>(),
            provider.GetRequiredService<SyncCorrectionPolicy>(),
            AppVersion));

        services.AddSingleton<IPlayerCommandSink>(p => p.GetRequiredService<SendspinPlayerService>());
        services.AddSingleton<IDiagnosticsProvider>(p => p.GetRequiredService<SendspinPlayerService>());
        services.AddSingleton<PlayerCommandRouter>();

        services.AddSingleton<TrayIconController>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();

        return services.BuildServiceProvider();
    }

    private void OnShowRequested(object? sender, EventArgs e) => ShowMainWindow();

    /// <summary>
    /// Tears down in a defined order, before the framework exits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Synchronous, and it blocks. That is the point: the previous arrangement had an
    /// <c>async void</c> window-closing handler disposing the view model while this handler
    /// disposed the service provider, so the audio pipeline could be disposed from two
    /// directions at once and shutdown either threw or hung depending on which won.
    /// </para>
    /// <para>
    /// Now there is one owner. The view model is disposed as part of the container, the window
    /// does not dispose anything, and this waits for the async teardown to finish with a bound
    /// so a stuck service cannot prevent the process exiting.
    /// </para>
    /// </remarks>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;

        var logger = _services?.GetService<ILogger<App>>();
        logger?.LogInformation("Shutting down");

        _tray?.Detach();

        if (SingleInstance is { } guard)
        {
            guard.ShowRequested -= OnShowRequested;
        }

        if (_services is { } services)
        {
            _services = null;

            // DisposeAsync on the container disposes every IAsyncDisposable it created — the
            // player service, the media session, notifications, presence — in reverse
            // registration order.
            var disposal = services.DisposeAsync().AsTask();

            if (!disposal.Wait(TimeSpan.FromSeconds(10)))
            {
                logger?.LogWarning("Service disposal did not complete within 10s; exiting anyway");
            }
        }
    }
}
