using Avalonia;
using Sendspin.Core.Platform;
using Sendspin.Platform.MacOS.Platform;

namespace Sendspin.Player;

/// <summary>
/// Platform wiring for the macOS head.
/// </summary>
/// <remarks>
/// Wiring only — see the remarks on the Linux variant for why nothing else may live here.
/// </remarks>
internal static class PlatformSelection
{
    /// <summary>
    /// Creates this build's platform initializer.
    /// </summary>
    public static IPlatformInitializer CreateInitializer() => new MacPlatformInitializer();

    /// <summary>
    /// Runs host initialisation that must happen before Avalonia is configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Avalonia bootstraps <c>NSApplication</c> through its own <c>libAvaloniaNative.dylib</c>,
    /// so the macios runtime never records which thread is the UI thread. AppKit bindings that
    /// carry an <c>EnsureUIThread()</c> check then throw <c>AppKitThreadAccessException</c> on
    /// the genuine main thread. Calling <c>NSApplication.Init()</c> first records it.
    /// </para>
    /// <para>
    /// It fails selectively, which is what makes it hard to diagnose:
    /// <c>MPNowPlayingInfoCenter</c> has no such check and works either way, so Now Playing
    /// looks fine while the status item throws.
    /// </para>
    /// </remarks>
    public static void PreInitializeHost() => AppKit.NSApplication.Init();

    /// <summary>
    /// The desktop font family <c>$Default</c> should resolve to, or null to let the platform
    /// answer.
    /// </summary>
    /// <remarks>
    /// Null for now, and measured: <c>FontManager.DefaultFontFamily</c> resolves to
    /// <c>Helvetica</c> on macOS 26 (Avalonia 12.1.1), not the system UI font (SF Pro). So
    /// <c>$Default</c> is the wrong face here and a per-platform name will be needed; which
    /// family name Skia resolves to SF is still being measured, and guessing one would be worse
    /// than Helvetica. The SF override is a follow-up once the resolvable name is known
    /// (<c>dotnet run --project scripts/spike/ShellSpike -- font</c>, with the candidate names).
    /// </remarks>
    public static string? ReadDesktopFontFamily() => null;

    /// <summary>
    /// Selects the windowing backend.
    /// </summary>
    public static AppBuilder ConfigureWindowing(AppBuilder builder) => builder.UsePlatformDetect();
}
