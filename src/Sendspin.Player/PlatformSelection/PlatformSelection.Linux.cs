using Avalonia;
using Sendspin.Core.Platform;
using Sendspin.Platform.Linux.Platform;

namespace Sendspin.Player;

/// <summary>
/// Platform wiring for the plain <c>net10.0</c> head, which is the Linux build.
/// </summary>
/// <remarks>
/// Wiring only. Behaviour that appears in one of the three <c>PlatformSelection.*.cs</c> files
/// has been put in the wrong place: the compiler cannot tell you when one of three parallel
/// files falls behind the others, which is precisely the failure mode a three-way
/// <c>#if</c> has. Behaviour belongs in the platform projects, or behind a runtime
/// <c>OperatingSystem.IsX()</c> check.
/// </remarks>
internal static class PlatformSelection
{
    /// <summary>
    /// Creates this build's platform initializer.
    /// </summary>
    public static IPlatformInitializer CreateInitializer() => new LinuxPlatformInitializer();

    /// <summary>
    /// Runs host initialisation that must happen before Avalonia is configured.
    /// </summary>
    public static void PreInitializeHost()
    {
        // Nothing to do: neither the X11 nor the Wayland backend needs pre-initialisation.
    }

    /// <summary>
    /// Selects the windowing backend.
    /// </summary>
    /// <remarks>
    /// X11 by default, which under a Wayland session means XWayland. The opt-in and the reasoning
    /// behind the default both live in <see cref="WaylandOptIn"/>.
    /// </remarks>
    public static AppBuilder ConfigureWindowing(AppBuilder builder) =>
        WaylandOptIn.IsRequested ? builder.UseWayland() : builder.UsePlatformDetect();
}
