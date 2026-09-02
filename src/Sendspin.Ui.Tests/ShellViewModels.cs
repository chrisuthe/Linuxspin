using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.Core.Audio;
using Sendspin.Core.Configuration;
using Sendspin.Core.Control;
using Sendspin.Core.MediaSession;
using Sendspin.Core.Notifications;
using Sendspin.Core.Platform;
using Sendspin.Core.Presence;
using Sendspin.Core.Visualization;
using Sendspin.Platform.Shared.Client;
using Sendspin.Platform.Shared.Media;
using Sendspin.Platform.Shared.Notifications;
using Sendspin.Player.ViewModels;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;

namespace Sendspin.Ui.Tests;

/// <summary>The main view model and the settings it was built over.</summary>
internal sealed record ShellGraph(MainViewModel ViewModel, SettingsService Settings);

/// <summary>
/// A real <see cref="MainViewModel"/> over in-memory fakes, so the main window's compiled
/// bindings have the type they were compiled against.
/// </summary>
/// <remarks>
/// The view model takes every dependency by constructor and nothing is nullable, which is the
/// design (see its remarks) — so the shell tests build the whole graph rather than a stand-in.
/// None of it starts: the player service only touches the network and the audio device from
/// <c>StartAsync</c>, which nothing here calls, and the audio player factory throws to prove it.
/// </remarks>
internal static class ShellViewModels
{
    /// <param name="configure">
    /// Edits the settings the graph starts from. The view model reads the connection mode once,
    /// at construction, so a test that wants discover mode has to say so before it exists.
    /// </param>
    /// <param name="hasGpu">What the renderer probe answers; a GPU unless a test says otherwise.</param>
    public static ShellGraph CreateMain(Action<PlayerSettings>? configure = null, bool hasGpu = true)
    {
        var settings = CreateSettings(configure);
        var devices = new NoAudioDevices();
        var presence = new NullPresenceService();

        var player = new SendspinPlayerService(
            NullLoggerFactory.Instance,
            settings,
            new InMemoryStaticDelayStore(),
            devices,
            NoAudioPlayer,
            new ArtworkCache(new ScratchPaths(), NullLogger<ArtworkCache>.Instance),
            new SyncCorrectionPolicy(),
            "test");

        var viewModel = new MainViewModel(
            player,
            new PlayerCommandRouter(player, NullLogger<PlayerCommandRouter>.Instance),
            settings,
            new NullMediaSession(),
            new NotificationDispatcher(new NullNotificationService(), settings, NullLogger<NotificationDispatcher>.Instance),
            presence,
            new SettingsViewModel(settings, player, devices, presence, NullLogger<SettingsViewModel>.Instance),
            new DiagnosticsViewModel(player, new SyncCorrectionPolicy()),
            new AmbientBackdropViewModel(settings, new FixedProbe(hasGpu), NullLogger<AmbientBackdropViewModel>.Instance),
            NullLogger<MainViewModel>.Instance);

        return new ShellGraph(viewModel, settings);
    }

    /// <summary>A settings service over memory, starting from the defaults as <paramref name="configure"/> edits them.</summary>
    public static SettingsService CreateSettings(Action<PlayerSettings>? configure = null)
    {
        var initial = new PlayerSettings();
        configure?.Invoke(initial);

        return new SettingsService(new InMemorySettingsStore(initial), NullLogger<SettingsService>.Instance);
    }

    private static IAudioPlayer NoAudioPlayer() =>
        throw new InvalidOperationException("A shell test never opens an audio device.");

    /// <summary>A renderer probe with the answer a test wants.</summary>
    internal sealed class FixedProbe(bool hasGpu) : IGraphicsContextProbe
    {
        public bool HasGpuContext() => hasGpu;
    }

    private sealed class InMemorySettingsStore(PlayerSettings initial) : ISettingsStore
    {
        private PlayerSettings _settings = initial;

        public PlayerSettings Load() => _settings;

        public void Save(PlayerSettings settings) => _settings = settings;
    }

    private sealed class InMemoryStaticDelayStore : IStaticDelayStore
    {
        private double? _value;

        public double? Load() => _value;

        public void Save(double staticDelayMs) => _value = staticDelayMs;
    }

    private sealed class NoAudioDevices : IAudioDeviceEnumerator
    {
        public IReadOnlyList<AudioDeviceInfo> GetDevices() => [];

        public AudioDeviceInfo? GetDefaultDevice() => null;
    }

    /// <summary>Paths nothing writes to; the artwork cache only needs them to exist as strings.</summary>
    private sealed class ScratchPaths : PlatformPathsBase
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sendspin-ui-tests", Guid.NewGuid().ToString("n"));

        public override string ConfigDirectory => Path.Combine(_root, "config");

        public override string DataDirectory => Path.Combine(_root, "data");

        public override string CacheDirectory => Path.Combine(_root, "cache");
    }
}
