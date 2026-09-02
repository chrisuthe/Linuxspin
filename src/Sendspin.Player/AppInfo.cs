using System.Reflection;

namespace Sendspin.Player;

/// <summary>
/// Facts about this build, read from the assembly in this one place.
/// </summary>
internal static class AppInfo
{
    /// <summary>
    /// Gets the informational version, which is what the protocol's <c>device_info</c> sends.
    /// </summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString()
        ?? "1.0.0";

    /// <summary>
    /// Gets the version as the settings card shows it: <see cref="Version"/> without the
    /// <c>+commit</c> build metadata the SDK appends, which is for a bug report, not a footer.
    /// </summary>
    public static string DisplayVersion { get; } = Version.Split('+', 2)[0];
}
