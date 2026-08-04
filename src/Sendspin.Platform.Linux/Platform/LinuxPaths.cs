using Sendspin.Core.Platform;

namespace Sendspin.Platform.Linux.Platform;

/// <summary>
/// XDG Base Directory locations for the player's configuration, data and cache.
/// </summary>
/// <remarks>
/// Only the three roots are platform-specific; the log directory, the artwork directory and the
/// settings filename all come from <see cref="PlatformPathsBase"/>. In particular
/// <see cref="PlatformPathsBase.ConfigFile"/> is deliberately not overridden: the settings file has
/// one name on every platform, and the two names this project used to have made support and
/// documentation needlessly per-platform.
/// </remarks>
public sealed class LinuxPaths : PlatformPathsBase
{
    private const string ApplicationDirectory = "sendspin";

    /// <summary>
    /// Set by Flatpak inside the sandbox, and the cheapest reliable way to detect it.
    /// </summary>
    private const string FlatpakIdVariable = "FLATPAK_ID";

    /// <summary>
    /// Present in every Flatpak sandbox, and the fallback when the id variable has been unset by an
    /// intervening process.
    /// </summary>
    private const string FlatpakInfoPath = "/.flatpak-info";

    /// <inheritdoc/>
    public override string ConfigDirectory => XdgDirectory("XDG_CONFIG_HOME", ".config");

    /// <inheritdoc/>
    public override string DataDirectory => XdgDirectory("XDG_DATA_HOME", ".local/share");

    /// <inheritdoc/>
    public override string CacheDirectory => XdgDirectory("XDG_CACHE_HOME", ".cache");

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Under Flatpak this must be <c>$XDG_RUNTIME_DIR/app/$FLATPAK_ID/</c> and not the sandbox's
    /// cache directory. Artwork is handed to the shell as a <c>file://</c> URL, and the shell runs
    /// on the host: a path inside the sandbox's private filesystem is one the host cannot follow, so
    /// the picture silently never appears. That directory is the sanctioned exception — it is
    /// visible on both sides.
    /// </para>
    /// <para>
    /// Outside Flatpak the base class's cache subdirectory is correct and is used unchanged.
    /// </para>
    /// </remarks>
    public override string AlbumArtCacheDirectory
    {
        get
        {
            var flatpakId = FlatpakId;
            var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

            if (flatpakId is null || string.IsNullOrEmpty(runtimeDirectory))
            {
                return base.AlbumArtCacheDirectory;
            }

            return Path.Combine(runtimeDirectory, "app", flatpakId);
        }
    }

    /// <summary>
    /// Gets the Flatpak application id, or null when not running in a sandbox.
    /// </summary>
    /// <remarks>
    /// <c>/.flatpak-info</c> is consulted when the environment variable is missing, which happens
    /// whenever an intervening process sanitises the environment. The file is the authoritative
    /// marker of a sandbox, and its <c>[Application] name</c> key is the same id.
    /// </remarks>
    private static string? FlatpakId
    {
        get
        {
            var id = Environment.GetEnvironmentVariable(FlatpakIdVariable);
            if (!string.IsNullOrEmpty(id))
            {
                return id;
            }

            return ReadFlatpakInfoName();
        }
    }

    private static string? ReadFlatpakInfoName()
    {
        if (!File.Exists(FlatpakInfoPath))
        {
            return null;
        }

        try
        {
            var inApplicationSection = false;

            foreach (var line in File.ReadLines(FlatpakInfoPath))
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith('['))
                {
                    inApplicationSection = trimmed.Equals("[Application]", StringComparison.Ordinal);
                    continue;
                }

                if (inApplicationSection && trimmed.StartsWith("name=", StringComparison.Ordinal))
                {
                    var name = trimmed["name=".Length..].Trim();
                    return name.Length == 0 ? null : name;
                }
            }
        }
        catch (IOException)
        {
            // Unreadable sandbox metadata: fall back to the in-sandbox cache directory, which costs
            // artwork in the shell but nothing else.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Resolves one XDG root, falling back to the specification's default when the variable is
    /// unset.
    /// </summary>
    private static string XdgDirectory(string variable, string fallbackRelativePath)
    {
        var root = Environment.GetEnvironmentVariable(variable);

        if (!string.IsNullOrEmpty(root))
        {
            return Path.Combine(root, ApplicationDirectory);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, fallbackRelativePath, ApplicationDirectory);
    }
}
