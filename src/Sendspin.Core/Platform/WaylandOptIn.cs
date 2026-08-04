namespace Sendspin.Core.Platform;

/// <summary>
/// Whether the user has asked for Avalonia's native Wayland backend instead of X11.
/// </summary>
/// <remarks>
/// <para>
/// The predicate lives here, not in the Linux <c>PlatformSelection</c> file, because that file is
/// one of three parallel per-TFM files and the compiler cannot tell you when one of them drifts.
/// Those files hold wiring — a type name and a backend call — and anything with a decision in it
/// belongs somewhere testable and singular.
/// </para>
/// <para>
/// X11 stays the default. <c>UsePlatformDetect()</c> never selects the Wayland backend, it is marked
/// experimental, and every desktop integration in this app is D-Bus and so identical either way.
/// What it buys is correct fractional HiDPI; what it does not bind is <c>xdg-activation-v1</c> or
/// <c>idle-inhibit</c>, so raising the window and inhibiting the screensaver are worse there.
/// </para>
/// </remarks>
public static class WaylandOptIn
{
    /// <summary>
    /// Environment variable that opts in.
    /// </summary>
    public const string VariableName = "SENDSPIN_WAYLAND";

    /// <summary>
    /// Gets whether the environment asks for the native Wayland backend.
    /// </summary>
    public static bool IsRequested =>
        IsTruthy(Environment.GetEnvironmentVariable(VariableName));

    /// <summary>
    /// Returns whether a variable's value reads as an opt-in.
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
