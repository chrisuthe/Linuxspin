namespace Sendspin.Core.Platform;

/// <summary>
/// Platform-specific application directories.
/// </summary>
/// <remarks>
/// Implementations follow their platform's conventions: XDG on Linux,
/// <c>%LocalAppData%</c> on Windows, <c>~/Library</c> on macOS.
/// </remarks>
public interface IPlatformPaths
{
    /// <summary>Configuration directory for user settings.</summary>
    string ConfigDirectory { get; }

    /// <summary>Directory for persistent application data.</summary>
    string DataDirectory { get; }

    /// <summary>Directory for temporary or clearable data.</summary>
    string CacheDirectory { get; }

    /// <summary>Directory for log files.</summary>
    string LogDirectory { get; }

    /// <summary>
    /// Directory album art is written to, for handing to OS media surfaces as a
    /// <c>file://</c> URL.
    /// </summary>
    /// <remarks>
    /// A real directory the shell can read, not a temp path: under Flatpak this must be
    /// <c>$XDG_RUNTIME_DIR/app/$FLATPAK_ID/</c> rather than <c>/tmp</c>, because the
    /// sandbox's <c>/tmp</c> is not the host's and the shell cannot follow a path into it.
    /// </remarks>
    string AlbumArtCacheDirectory { get; }

    /// <summary>
    /// Path to the one settings file.
    /// </summary>
    /// <remarks>
    /// Every platform uses the same filename. The platforms used to disagree —
    /// <c>config.json</c> on Linux, <c>settings.json</c> on Windows — which makes support and
    /// documentation needlessly per-platform for no benefit.
    /// </remarks>
    string ConfigFile { get; }

    /// <summary>
    /// Creates any of the above that do not exist.
    /// </summary>
    /// <exception cref="IOException">Thrown when a directory cannot be created.</exception>
    void EnsureDirectoriesExist();
}
