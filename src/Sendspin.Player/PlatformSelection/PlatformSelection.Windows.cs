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
    /// The desktop font family <c>$Default</c> should resolve to, or null to let the platform
    /// answer.
    /// </summary>
    /// <remarks>
    /// Null on purpose: measured. <c>FontManager.DefaultFontFamily</c> resolves to plain
    /// <c>Segoe UI</c> on Windows 11 (10.0.26200, Avalonia 12.1.1), not Segoe UI Variable, and
    /// that is the face the WPF reference app uses, so the platform's answer is the right one.
    /// Glyph fallback through the composite works there (日 → Yu Gothic UI). To re-check after
    /// a bump: <c>dotnet run --project scripts/spike/ShellSpike -- font</c>, the
    /// <c>FontManager.DefaultFontFamily</c> line.
    /// </remarks>
    public static string? ReadDesktopFontFamily() => null;

    /// <summary>
    /// Selects the windowing backend.
    /// </summary>
    public static AppBuilder ConfigureWindowing(AppBuilder builder) => builder.UsePlatformDetect();
}
