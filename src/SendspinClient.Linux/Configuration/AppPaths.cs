using Microsoft.Extensions.Logging;

namespace SendspinClient.Linux.Configuration;

/// <summary>
/// Manages XDG Base Directory specification paths for the Sendspin Linux client.
/// Provides standardized locations for configuration, data, and cache files.
/// </summary>
/// <remarks>
/// Follows the XDG Base Directory Specification:
/// - Config: ~/.config/sendspin or $XDG_CONFIG_HOME/sendspin
/// - Data: ~/.local/share/sendspin or $XDG_DATA_HOME/sendspin
/// - Cache: ~/.cache/sendspin or $XDG_CACHE_HOME/sendspin
/// </remarks>
public class AppPaths
{
    private const string AppName = "sendspin";
    private readonly ILogger<AppPaths>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppPaths"/> class.
    /// </summary>
    public AppPaths()
    {
        // Design-time or test constructor
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AppPaths"/> class with logging.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public AppPaths(ILogger<AppPaths> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the base configuration directory path.
    /// Uses $XDG_CONFIG_HOME/sendspin or defaults to ~/.config/sendspin.
    /// </summary>
    public string ConfigDirectory => GetXdgPath("XDG_CONFIG_HOME", ".config");

    /// <summary>
    /// Gets the base data directory path.
    /// Uses $XDG_DATA_HOME/sendspin or defaults to ~/.local/share/sendspin.
    /// </summary>
    public string DataDirectory => GetXdgPath("XDG_DATA_HOME", ".local/share");

    /// <summary>
    /// Gets the base cache directory path.
    /// Uses $XDG_CACHE_HOME/sendspin or defaults to ~/.cache/sendspin.
    /// </summary>
    public string CacheDirectory => GetXdgPath("XDG_CACHE_HOME", ".cache");

    /// <summary>
    /// Gets the path to the main configuration file.
    /// </summary>
    public string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>
    /// Gets the path to the server bookmarks file.
    /// </summary>
    public string BookmarksFile => Path.Combine(ConfigDirectory, "servers.json");

    /// <summary>
    /// Gets the path to the log files directory.
    /// </summary>
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Gets the path to the album art cache directory.
    /// </summary>
    public string AlbumArtCacheDirectory => Path.Combine(CacheDirectory, "album-art");

    /// <summary>
    /// Ensures all required directories exist, creating them if necessary.
    /// </summary>
    public void EnsureDirectoriesExist()
    {
        var directories = new[]
        {
            ConfigDirectory,
            DataDirectory,
            CacheDirectory,
            LogDirectory,
            AlbumArtCacheDirectory
        };

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                _logger?.LogDebug("Created directory: {Directory}", directory);
            }
        }

        _logger?.LogInformation("XDG directories initialized");
    }

    /// <summary>
    /// Gets the XDG-compliant path for the specified environment variable.
    /// </summary>
    /// <param name="envVariable">The XDG environment variable name.</param>
    /// <param name="defaultRelativePath">The default path relative to HOME if env var is not set.</param>
    /// <returns>The full path to the application's directory.</returns>
    private string GetXdgPath(string envVariable, string defaultRelativePath)
    {
        var xdgPath = Environment.GetEnvironmentVariable(envVariable);

        if (!string.IsNullOrEmpty(xdgPath))
        {
            return Path.Combine(xdgPath, AppName);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, defaultRelativePath, AppName);
    }
}
