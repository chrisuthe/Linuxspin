using Avalonia;
using Sendspin.Core.Platform;
using Sendspin.Platform.Windows.Platform;

namespace Sendspin.Player;

/// <summary>
/// Platform wiring for the Windows head.
/// </summary>
/// <remarks>
/// Wiring only — see the remarks on the Linux variant for why nothing else may live here.
/// </remarks>
internal static class PlatformSelection
{
    /// <summary>
    /// Creates this build's platform initializer.
    /// </summary>
    public static IPlatformInitializer CreateInitializer() => new WindowsPlatformInitializer();

    /// <summary>
    /// Runs host initialisation that must happen before Avalonia is configured.
    /// </summary>
    public static void PreInitializeHost()
    {
        // Nothing to do: per-monitor DPI awareness comes from app.manifest, not from code.
    }

    /// <summary>
    /// Selects the windowing backend.
    /// </summary>
    public static AppBuilder ConfigureWindowing(AppBuilder builder) => builder.UsePlatformDetect();
}
