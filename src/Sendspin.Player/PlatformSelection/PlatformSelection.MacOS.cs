using Avalonia;
using Sendspin.Core.Platform;
using Sendspin.Platform.MacOS.Platform;
using SkiaSharp;

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
    /// <para>
    /// The platform's own answer is Helvetica (measured on macOS 26, Avalonia 12.1.1), so the
    /// system face is named: <c>.AppleSystemUIFont</c>, the one name that resolved to it. The
    /// name is guarded, because an unresolvable <c>DefaultFamilyName</c> kills the process in
    /// the first layout pass — the rule and the measurement are on <see cref="MacSystemFont"/>.
    /// Skia's font manager is the resolver: it is what Avalonia asks at start-up, and it is
    /// already loaded through Avalonia.Skia.
    /// </para>
    /// <para>
    /// This runs before the container exists, so a miss goes to standard error rather than the
    /// logger; the start-up <c>UI font:</c> log line then shows what <c>$Default</c> became.
    /// </para>
    /// </remarks>
    public static string? ReadDesktopFontFamily()
    {
        var family = MacSystemFont.Select(Resolves);

        if (family is null)
        {
            Console.Error.WriteLine($"{MacSystemFont.FamilyName} does not resolve here; leaving the UI font to the platform.");
        }

        return family;

        static bool Resolves(string name)
        {
            using var typeface = SKFontManager.Default.MatchFamily(name);
            return typeface is not null;
        }
    }

    /// <summary>
    /// Selects the windowing backend.
    /// </summary>
    public static AppBuilder ConfigureWindowing(AppBuilder builder) => builder.UsePlatformDetect();
}
