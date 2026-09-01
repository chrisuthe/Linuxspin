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
    /// <para>
    /// Wayland by default; the choice and the reasoning behind it live in
    /// <see cref="LinuxWindowingSelection"/>.
    /// </para>
    /// <para>
    /// The rendering stack is chained onto whichever backend comes back, not spelled out per
    /// branch. <c>UsePlatformDetect()</c> wires Skia and HarfBuzz along with the windowing
    /// backend; the backend calls underneath it do not, and a branch that named one and forgot
    /// the other is what made <c>SENDSPIN_WAYLAND=1</c> abort at startup with "no rendering
    /// system configured". One chain cannot be wired for one backend and forgotten for another.
    /// </para>
    /// </remarks>
    public static AppBuilder ConfigureWindowing(AppBuilder builder) =>
        ConfigureWindowing(builder, LinuxWindowingSelection.Selected);

    /// <summary>
    /// Wires a named backend. Separate from the environment read so a test can name one.
    /// </summary>
    internal static AppBuilder ConfigureWindowing(AppBuilder builder, LinuxWindowingBackend backend) =>
        (backend switch
        {
            LinuxWindowingBackend.Wayland => builder.UseWayland(),
            LinuxWindowingBackend.X11 => builder.UseX11(),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "No windowing call for this backend."),
        })
        .UseSkia()
        .UseHarfBuzz();
}
