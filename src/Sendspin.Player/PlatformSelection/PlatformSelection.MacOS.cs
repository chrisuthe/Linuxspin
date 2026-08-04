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
    /// Selects the windowing backend.
    /// </summary>
    public static AppBuilder ConfigureWindowing(AppBuilder builder) => builder.UsePlatformDetect();
}
