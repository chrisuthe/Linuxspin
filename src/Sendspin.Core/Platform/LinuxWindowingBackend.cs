namespace Sendspin.Core.Platform;

/// <summary>
/// A windowing backend the Linux head can run on.
/// </summary>
public enum LinuxWindowingBackend
{
    /// <summary>
    /// Avalonia's native Wayland backend. The default under a Wayland session.
    /// </summary>
    Wayland,

    /// <summary>
    /// Avalonia's X11 backend, which under a Wayland session means XWayland.
    /// </summary>
    X11,
}

/// <summary>
/// Chooses the windowing backend the Linux head starts on.
/// </summary>
/// <remarks>
/// <para>
/// The decision lives here, not in the Linux <c>PlatformSelection</c> file, because that file is
/// one of three parallel per-TFM files and the compiler cannot tell you when one of them drifts.
/// Those files hold wiring — a type name and a backend call — and anything with a decision in it
/// belongs somewhere testable and singular.
/// </para>
/// <para>
/// Wayland is the default. It gives correct fractional HiDPI, and the two things Avalonia's Wayland
/// backend does not bind are already routed around: idling goes through
/// <c>org.freedesktop.portal.Inhibit</c> and raising the window goes through the activation token
/// the notification daemon hands back, neither of which is a windowing-backend call. Every other
/// desktop integration here is D-Bus and so identical either way.
/// </para>
/// <para>
/// X11 stays one variable away because the backend is marked experimental and its own README warns
/// that compositor crash-and-restart is expected. That escape hatch is what makes defaulting to
/// Wayland defensible, so it wins over every other signal.
/// </para>
/// </remarks>
public static class LinuxWindowingSelection
{
    /// <summary>
    /// Environment variable that forces X11.
    /// </summary>
    public const string X11VariableName = "SENDSPIN_X11";

    /// <summary>
    /// Environment variable that forces Wayland even when no session is detected.
    /// </summary>
    public const string WaylandVariableName = "SENDSPIN_WAYLAND";

    /// <summary>
    /// The compositor-set variable that says a Wayland session is there to connect to.
    /// </summary>
    public const string SessionVariableName = "WAYLAND_DISPLAY";

    /// <summary>
    /// Gets the backend this environment asks for.
    /// </summary>
    public static LinuxWindowingBackend Selected =>
        Select(
            Environment.GetEnvironmentVariable(X11VariableName),
            Environment.GetEnvironmentVariable(WaylandVariableName),
            Environment.GetEnvironmentVariable(SessionVariableName));

    /// <summary>
    /// Chooses a backend from the three variables that bear on it.
    /// </summary>
    /// <param name="x11Request">Value of <see cref="X11VariableName"/>.</param>
    /// <param name="waylandRequest">Value of <see cref="WaylandVariableName"/>.</param>
    /// <param name="waylandDisplay">Value of <see cref="SessionVariableName"/>.</param>
    /// <remarks>
    /// <para>
    /// In precedence order: the escape hatch, then an explicit override, then detection. Detection
    /// last means the answer on X11-only hardware — an X11 desktop, VNC, a forwarded
    /// <c>DISPLAY</c>, a CI container — is X11 by decision rather than by whatever
    /// <c>UseWayland()</c> happens to do with no compositor to talk to.
    /// </para>
    /// <para>
    /// <see cref="WaylandVariableName"/> still means something under that ordering: it forces the
    /// Wayland backend past a session this cannot see. Asking for it where no compositor is
    /// listening is a user override, and it fails the way any override does.
    /// </para>
    /// </remarks>
    public static LinuxWindowingBackend Select(string? x11Request, string? waylandRequest, string? waylandDisplay)
    {
        if (IsTruthy(x11Request))
        {
            return LinuxWindowingBackend.X11;
        }

        if (IsTruthy(waylandRequest))
        {
            return LinuxWindowingBackend.Wayland;
        }

        return string.IsNullOrEmpty(waylandDisplay)
            ? LinuxWindowingBackend.X11
            : LinuxWindowingBackend.Wayland;
    }

    /// <summary>
    /// Returns whether a variable's value reads as a request.
    /// </summary>
    /// <remarks>
    /// Accepts more than one spelling on purpose: a user setting this by hand is as likely to write
    /// <c>true</c> as <c>1</c>, and silently ignoring one of them looks like the flag not working.
    /// </remarks>
    public static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.Ordinal) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
