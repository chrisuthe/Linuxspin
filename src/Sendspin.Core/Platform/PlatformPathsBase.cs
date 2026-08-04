namespace Sendspin.Core.Platform;

/// <summary>
/// The parts of <see cref="IPlatformPaths"/> that are the same everywhere.
/// </summary>
/// <remarks>
/// Only the three roots genuinely differ between platforms. Everything derived from them —
/// the log and artwork subdirectories, the settings filename, creating them — was duplicated
/// per platform and had already drifted, which is how the settings file ended up with two
/// different names.
/// </remarks>
public abstract class PlatformPathsBase : IPlatformPaths
{
    /// <summary>
    /// The one settings filename, on every platform.
    /// </summary>
    protected const string SettingsFileName = "settings.json";

    /// <inheritdoc/>
    public abstract string ConfigDirectory { get; }

    /// <inheritdoc/>
    public abstract string DataDirectory { get; }

    /// <inheritdoc/>
    public abstract string CacheDirectory { get; }

    /// <inheritdoc/>
    public virtual string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <inheritdoc/>
    public virtual string AlbumArtCacheDirectory => Path.Combine(CacheDirectory, "album-art");

    /// <inheritdoc/>
    public string ConfigFile => Path.Combine(ConfigDirectory, SettingsFileName);

    /// <inheritdoc/>
    public void EnsureDirectoriesExist()
    {
        foreach (var directory in new[]
                 {
                     ConfigDirectory,
                     DataDirectory,
                     CacheDirectory,
                     LogDirectory,
                     AlbumArtCacheDirectory
                 })
        {
            Directory.CreateDirectory(directory);
        }
    }
}
