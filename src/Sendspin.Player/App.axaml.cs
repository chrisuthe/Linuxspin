using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
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
using Sendspin.Core.Visualization;
using Sendspin.Discord;
using Sendspin.Platform.Shared.Client;
using Sendspin.Platform.Shared.Media;
using Sendspin.Platform.Shared.Notifications;
using Sendspin.Player.Theme;
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
    private PlatformColorChanges? _colorChanges;
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
            AppInfo.Version, platform.PlatformName);

        _services.GetRequiredService<IPlatformPaths>().EnsureDirectoriesExist();

        LogUiFont(logger);

        if (PlatformSettings is { } platformSettings)
        {
            // Fluent tracks the accent itself; the glyph colour drawn over it is ours to keep
            // in step, from the same event.
            _colorChanges = new PlatformColorChanges(platformSettings);
            _colorChanges.Changed += OnColorValuesChanged;
            _colorChanges.Publish();
        }

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
            AppInfo.Version));

        services.AddSingleton<IPlayerCommandSink>(p => p.GetRequiredService<SendspinPlayerService>());
        services.AddSingleton<IDiagnosticsProvider>(p => p.GetRequiredService<SendspinPlayerService>());
        services.AddSingleton<PlayerCommandRouter>();

        // The backdrop asks the loader whether a GPU is drawing, after the first frame.
        services.AddSingleton<IGraphicsContextProbe, MappedGraphicsProbe>();

        services.AddSingleton<TrayIconController>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<AmbientBackdropViewModel>();

        return services.BuildServiceProvider();
    }

    private void OnShowRequested(object? sender, EventArgs e) => ShowMainWindow();

    private void OnColorValuesChanged(PlatformColorValues values) =>
        AccentResources.Apply(Resources, values.AccentColor1);

    /// <summary>
    /// Records which face the platform default resolved to, which face the controls' font
    /// resource resolved to, and which face fills in glyphs it lacks — what the shell spike's
    /// <c>font</c> probe reports.
    /// </summary>
    private void LogUiFont(ILogger logger)
    {
        var fontManager = FontManager.Current;

        var controlFamily = TryGetResource("ContentControlThemeFontFamily", ActualThemeVariant, out var resource)
            && resource is FontFamily family
            ? family
            : FontFamily.Default;

        var fallback = fontManager.TryMatchCharacter(
            'A', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, null, null, out var fallbackTypeface)
            ? fallbackTypeface.FontFamily.Name
            : "(none)";

        logger.LogInformation(
            "UI font: $Default is {Default}, glyphs from {Face}; controls use {ControlFamily}, glyphs from {ControlFace}; fallback face {Fallback}",
            fontManager.DefaultFontFamily, Resolve(FontFamily.Default), controlFamily, Resolve(controlFamily), fallback);

        static string Resolve(FontFamily family) =>
            FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var glyphTypeface)
                ? glyphTypeface.FamilyName
                : "(unresolved)";
    }

    /// <summary>
    /// Tears down in a defined order, before the framework exits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Synchronous, and it blocks — deliberately. Teardown has exactly one owner: the view model
    /// is disposed as part of the container, the window disposes nothing, and this waits for the
    /// async teardown to finish. Two owners disposing the audio pipeline from different directions
    /// is how shutdown starts either throwing or hanging depending on which gets there first.
    /// </para>
    /// <para>
    /// The wait is bounded so a stuck service cannot stop the process exiting. Everything in the
    /// chain uses <c>ConfigureAwait(false)</c>, so blocking here does not deadlock on a
    /// continuation needing this thread — but a dispatcher continuation queued during teardown will
    /// not run until it returns, which is why the bound exists rather than being a formality.
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

        _colorChanges?.Dispose();
        _colorChanges = null;

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
