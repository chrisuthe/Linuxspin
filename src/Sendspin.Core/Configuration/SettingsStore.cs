using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sendspin.Core.Platform;

namespace Sendspin.Core.Configuration;

/// <summary>
/// Loads and saves the player's settings.
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// Reads the settings file, returning defaults when it is absent or unreadable.
    /// Never throws: an unusable config file must not stop the app from starting.
    /// </summary>
    PlayerSettings Load();

    /// <summary>
    /// Writes the settings file, replacing it atomically.
    /// </summary>
    /// <exception cref="IOException">Thrown when the file cannot be written.</exception>
    void Save(PlayerSettings settings);
}

/// <summary>
/// A JSON settings file at <see cref="IPlatformPaths.ConfigFile"/>.
/// </summary>
/// <remarks>
/// <para>
/// Writes go to a sibling temporary file and are then moved over the target, so an
/// interrupted write leaves the previous settings intact rather than a truncated file. This
/// matters more than it looks: the file holds <c>static_delay_ms</c> and the persisted
/// <c>client_id</c>, and losing either silently changes how the endpoint behaves on a
/// network it is supposed to stay predictable on.
/// </para>
/// <para>
/// A corrupt or unreadable file is logged and replaced by defaults. That is a deliberate
/// asymmetry with <see cref="Save"/>, which does throw: failing to read is recoverable,
/// failing to write means the user's change did not stick and they need to know.
/// </para>
/// </remarks>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly IPlatformPaths _paths;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly object _fileGate = new();

    public JsonSettingsStore(IPlatformPaths paths, ILogger<JsonSettingsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _logger = logger;
    }

    /// <inheritdoc/>
    public PlayerSettings Load()
    {
        var path = _paths.ConfigFile;

        lock (_fileGate)
        {
            if (!File.Exists(path))
            {
                _logger.LogInformation("No settings file at {Path}; starting from defaults", path);
                return new PlayerSettings();
            }

            try
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize(json, PlayerSettingsJsonContext.Default.PlayerSettings);

                if (settings is null)
                {
                    _logger.LogWarning("Settings file {Path} deserialized to null; using defaults", path);
                    return new PlayerSettings();
                }

                if (settings.ApplyMigrations())
                {
                    _logger.LogInformation(
                        "Migrated settings from {Path}: connection mode Auto is no longer supported and is now {Mode}",
                        path, settings.ConnectionMode);
                }

                _logger.LogDebug("Loaded settings from {Path}", path);
                return settings;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Settings file {Path} is not valid JSON; using defaults", path);
                return new PlayerSettings();
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Could not read settings file {Path}; using defaults", path);
                return new PlayerSettings();
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Not permitted to read settings file {Path}; using defaults", path);
                return new PlayerSettings();
            }
        }
    }

    /// <inheritdoc/>
    public void Save(PlayerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var path = _paths.ConfigFile;
        var directory = Path.GetDirectoryName(path);

        lock (_fileGate)
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = path + ".tmp";
            var json = JsonSerializer.Serialize(settings, PlayerSettingsJsonContext.Default.PlayerSettings);

            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);

            _logger.LogDebug("Saved settings to {Path}", path);
        }
    }
}
