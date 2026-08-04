using System.Text.Json;
using System.Text.Json.Serialization;
using Sendspin.SDK.Client;

namespace Sendspin.Core.Configuration;

/// <summary>
/// How this player finds, or is found by, a server.
/// </summary>
public enum AutoConnectPolicy
{
    /// <summary>Never connect without the user asking.</summary>
    Never,

    /// <summary>Connect to <see cref="PlayerSettings.LastServerId"/> once, then revert to <see cref="Never"/>.</summary>
    JustOnce,

    /// <summary>Always reconnect to <see cref="PlayerSettings.LastServerId"/> on startup.</summary>
    Always
}

/// <summary>
/// Which notifications the user wants. Off by default where a notification would
/// duplicate something the OS already shows.
/// </summary>
public sealed class NotificationSettings
{
    /// <summary>
    /// Gets or sets whether a toast is raised per track change.
    /// </summary>
    /// <remarks>
    /// Off by default and deliberately so: neither KDE nor GNOME deduplicates a
    /// now-playing toast against this app's own MPRIS entry, so enabling it double-notifies
    /// something already on screen.
    /// </remarks>
    public bool TrackChange { get; set; }

    /// <summary>Gets or sets whether play/pause transitions raise a toast.</summary>
    public bool PlaybackState { get; set; }

    /// <summary>Gets or sets whether connect and disconnect raise a toast.</summary>
    public bool ConnectionState { get; set; } = true;

    /// <summary>Gets or sets whether artwork is attached to notifications that support it.</summary>
    public bool IncludeArtwork { get; set; } = true;
}

/// <summary>
/// Per-device calibration. The measured device latency is never the whole story: the
/// analog, Bluetooth and AirPlay tail is not reported by any platform API, so it has to be
/// a number the user can dial in per output.
/// </summary>
public sealed class AudioDeviceSettings
{
    /// <summary>Gets or sets the manual offset in milliseconds, added to the measured latency.</summary>
    public double ManualLatencyOffsetMs { get; set; }
}

/// <summary>
/// Everything this player persists across restarts.
/// </summary>
/// <remarks>
/// Mutable with a parameterless constructor because it is round-tripped by
/// <see cref="System.Text.Json"/> and edited live by the settings UI. Defaults here are
/// the shipped defaults: an install with no config file must be usable.
/// </remarks>
public sealed class PlayerSettings
{
    /// <summary>
    /// Schema version, so a future change can migrate rather than silently discard.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Stable per-installation client identifier sent as <c>client_id</c>.
    /// </summary>
    /// <remarks>
    /// Persisted rather than derived. Deriving it from the machine name produced an
    /// identifier that was neither unique (two installs on one host collide) nor stable
    /// (renaming the host re-registers the player as a new endpoint and loses its group).
    /// Empty here means "not yet generated"; see
    /// <see cref="ClientIdentity.EnsureIdentity(PlayerSettings)"/>.
    /// </remarks>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Name shown for this player on servers and controllers.
    /// </summary>
    public string PlayerName { get; set; } = string.Empty;

    /// <summary>
    /// Whether we advertise ourselves for servers to connect to, discover servers
    /// ourselves, or both.
    /// </summary>
    public ConnectionMode ConnectionMode { get; set; } = ConnectionMode.Auto;

    /// <summary>Gets or sets the auto-connect behaviour on startup.</summary>
    public AutoConnectPolicy AutoConnect { get; set; } = AutoConnectPolicy.Never;

    /// <summary>Gets or sets the server id to auto-connect to.</summary>
    public string? LastServerId { get; set; }

    /// <summary>
    /// Gets or sets a manually entered server URL, remembered so it can be offered again.
    /// </summary>
    public string? ManualServerUrl { get; set; }

    /// <summary>Gets or sets the last volume, 0-100, reported as initial state on connect.</summary>
    public int Volume { get; set; } = 100;

    /// <summary>Gets or sets the last mute state, reported as initial state on connect.</summary>
    public bool Muted { get; set; }

    /// <summary>
    /// Gets or sets the user's fixed playback delay in milliseconds.
    /// </summary>
    /// <remarks>
    /// The spec requires this to survive a reboot: it is physical calibration for this
    /// room, not a preference, and losing it silently desynchronises the endpoint.
    /// </remarks>
    public double StaticDelayMs { get; set; }

    /// <summary>Gets or sets the output device id, or null for the system default.</summary>
    public string? AudioDeviceId { get; set; }

    /// <summary>Gets or sets the preferred codec, which must be one this build can decode.</summary>
    public string PreferredCodec { get; set; } = "flac";

    /// <summary>Gets or sets per-device calibration, keyed by device id.</summary>
    public Dictionary<string, AudioDeviceSettings> Devices { get; set; } = [];

    /// <summary>Gets or sets notification preferences.</summary>
    public NotificationSettings Notifications { get; set; } = new();

    /// <summary>Gets or sets whether the window starts hidden in the tray.</summary>
    public bool StartMinimizedToTray { get; set; }

    /// <summary>Gets or sets whether closing the window hides to tray instead of quitting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Gets or sets whether Discord Rich Presence is published. Off by default.</summary>
    public bool DiscordRichPresence { get; set; }

    /// <summary>Gets or sets whether the diagnostics view is shown on startup.</summary>
    public bool ShowDiagnostics { get; set; }

    /// <summary>
    /// Gets the manual latency offset for a device, or 0 when it has never been calibrated.
    /// </summary>
    public double GetManualLatencyOffsetMs(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return 0.0;
        }

        return Devices.TryGetValue(deviceId, out var device) ? device.ManualLatencyOffsetMs : 0.0;
    }

    /// <summary>
    /// Sets the manual latency offset for a device, creating its entry if needed.
    /// </summary>
    public void SetManualLatencyOffsetMs(string deviceId, double offsetMs)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);

        if (!Devices.TryGetValue(deviceId, out var device))
        {
            device = new AudioDeviceSettings();
            Devices[deviceId] = device;
        }

        device.ManualLatencyOffsetMs = offsetMs;
    }

    /// <summary>
    /// Returns an independent copy, sharing no mutable state with this instance.
    /// </summary>
    /// <remarks>
    /// This type is mutable and holds a dictionary, so handing the same instance to every reader
    /// means a reader can observe one field from before a multi-field change and another from after,
    /// and iterating <see cref="Devices"/> while another thread inserts throws. Copying on write
    /// instead — see <see cref="SettingsService.Update"/> — makes every published instance a stable
    /// snapshot, at the cost of one copy per change rather than one per read.
    /// <para>
    /// Round-tripped through the same source-generated contract used to persist it, so a field that
    /// is saved is also copied. A field added without a serializer entry would silently reset here,
    /// which is the same failure it would already have on disk.
    /// </para>
    /// </remarks>
    public PlayerSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, PlayerSettingsJsonContext.Default.PlayerSettings);
        return JsonSerializer.Deserialize(json, PlayerSettingsJsonContext.Default.PlayerSettings)
               ?? new PlayerSettings();
    }
}

/// <summary>
/// JSON contract for the settings file. Source-generated so the settings path stays
/// trim- and AOT-safe.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PlayerSettings))]
public sealed partial class PlayerSettingsJsonContext : JsonSerializerContext;
