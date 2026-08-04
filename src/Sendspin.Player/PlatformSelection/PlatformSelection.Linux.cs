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
    /// Environment variable that opts into the native Wayland backend.
    /// </summary>
    private const string WaylandOptInVariable = "SENDSPIN_WAYLAND";

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
    /// <para>
    /// X11 by default, which under a Wayland session means XWayland. Setting
    /// <c>SENDSPIN_WAYLAND=1</c> selects Avalonia 12.1's native Wayland backend instead.
    /// </para>
    /// <para>
    /// The default is X11 rather than Wayland on purpose. <c>UsePlatformDetect()</c> never picks
    /// the Wayland backend, it is marked experimental, and its README warns that compositor
    /// crash-and-restart is expected. What it buys is correct fractional HiDPI through
    /// <c>wp_fractional_scale_manager_v1</c>; what it does not buy is any of this app's desktop
    /// integration, because MPRIS, StatusNotifierItem, notifications and portals are all D-Bus
    /// and identical under either display protocol. It also does not bind
    /// <c>xdg-activation-v1</c>, <c>idle-inhibit</c> or <c>xdg-toplevel-icon</c>, so raising the
    /// window and inhibiting the screensaver are worse there, not better.
    /// </para>
    /// </remarks>
    public static AppBuilder ConfigureWindowing(AppBuilder builder)
    {
        var optIn = Environment.GetEnvironmentVariable(WaylandOptInVariable);

        if (string.Equals(optIn, "1", StringComparison.Ordinal) ||
            string.Equals(optIn, "true", StringComparison.OrdinalIgnoreCase))
        {
            return builder.UseWayland();
        }

        return builder.UsePlatformDetect();
    }
}
