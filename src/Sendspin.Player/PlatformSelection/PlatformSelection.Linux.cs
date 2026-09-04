using Avalonia;
using Avalonia.X11;
using Sendspin.Core.Platform;
using Sendspin.Platform.Linux.Platform;
using Sendspin.Platform.Linux.Portals;

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
    /// The desktop file's basename, which is also the application identity a Linux desktop
    /// matches a window against.
    /// </summary>
    internal const string DesktopEntryName = "io.sendspin.client";

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
    /// Reads the desktop's interface font family from the Settings portal, or null when the
    /// portal does not serve one.
    /// </summary>
    /// <remarks>
    /// The portal answers on Plasma as well as GNOME (measured: <c>"Noto Sans  10"</c> from the
    /// KDE backend on this box, and the same key from inside the Flatpak sandbox, where
    /// fontconfig's own default would otherwise be DejaVu Sans). Null leaves Avalonia to ask
    /// fontconfig. Bounded, because the app builder cannot wait on a portal that never answers.
    /// </remarks>
    public static string? ReadDesktopFontFamily() =>
        SettingsPortal.TryReadInterfaceFontFamily(TimeSpan.FromMilliseconds(500));

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
            LinuxWindowingBackend.X11 => builder.UseX11().With(CreateX11Options()),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "No windowing call for this backend."),
        })
        .UseSkia()
        .UseHarfBuzz();

    /// <summary>
    /// The X11 options, which exist to give the window an application identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WM_CLASS</c> is the only thing that ties a running window to its desktop file under
    /// X11, and Avalonia's default is the process name. That does not match
    /// <c>io.sendspin.client.desktop</c>, so the taskbar and the switcher find no entry and fall
    /// back to a generic icon however many sizes are installed under <c>hicolor</c>. The desktop
    /// files name the same string in <c>StartupWMClass</c>, which is the matching key.
    /// </para>
    /// <para>
    /// Built here rather than inlined so a test can assert the identity without standing up an
    /// X server: <c>With</c> defers the binding to <c>AppBuilder.Setup()</c>, which needs a
    /// display, so the value is unreachable from a headless test once it has been handed over.
    /// </para>
    /// </remarks>
    internal static X11PlatformOptions CreateX11Options() => new() { WmClass = DesktopEntryName };
}
