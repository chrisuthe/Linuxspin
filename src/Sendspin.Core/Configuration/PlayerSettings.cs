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
/// What the window does with the music: nothing, the Ambient Glow blobs, or the art breathing.
/// </summary>
public enum BackdropMode
{
    /// <summary>A still window: the blurred art alone, and the art tile at rest.</summary>
    Off,

    /// <summary>Drifting colour blobs from the server's palette, driven by loudness and beats.</summary>
    AmbientGlow,

    /// <summary>The art tile scales and glows with the music; no blobs.</summary>
    BreathingArt
}

/// <summary>
/// The living backdrop: which style, and how strongly it reacts.
/// </summary>
public sealed class BackdropSettings
{
    /// <summary>The intensity slider's ceiling; 1 is the tuned default.</summary>
    public const double MaxIntensity = 2.0;

    /// <summary>Gets or sets the backdrop style. Ambient Glow by default.</summary>
    public BackdropMode Mode { get; set; } = BackdropMode.AmbientGlow;

    /// <summary>
    /// Gets or sets how strongly the backdrop reacts, glows and moves: 0 to
    /// <see cref="MaxIntensity"/>, with 1 the tuned default. The renderer floors 0 to a faint
    /// minimum rather than going dark; Off is a style, not an intensity.
    /// </summary>
    public double Intensity { get; set; } = 1.0;
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
    /// Whether we advertise ourselves for servers to connect to, or discover servers ourselves.
    /// </summary>
    /// <remarks>
    /// Never both. connection.md requires exactly one connection method at a time, so
    /// <c>ConnectionMode.Auto</c> — which ran discovery and advertising together — is a spec
    /// violation the SDK removes in 10.0.0. It is neither offered nor stored here; see
    /// <see cref="ApplyMigrations"/> for what happens to a file that still says it.
    /// </remarks>
    public ConnectionMode ConnectionMode { get; set; } = ConnectionMode.AdvertiseOnly;

    /// <summary>Gets or sets the auto-connect behaviour on startup.</summary>
    public AutoConnectPolicy AutoConnect { get; set; } = AutoConnectPolicy.Never;

    /// <summary>Gets or sets the server id to auto-connect to.</summary>
    public string? LastServerId { get; set; }

    /// <summary>
    /// Gets or sets the server the auto-connect question has already been asked for.
    /// </summary>
    /// <remarks>
    /// The question is asked once per server, whatever the answer, so "not now" has to leave a
    /// record too: without one the prompt would come back on every reconnect to the same server.
    /// </remarks>
    public string? AutoConnectPromptedServerId { get; set; }

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

    /// <summary>
    /// Gets or sets whether the footer shows the Switch Group button. On by default; off is for a
    /// single-room setup with nothing to switch to.
    /// </summary>
    public bool ShowSwitchGroupButton { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the Stats window was open when the player last exited, so the next
    /// start reopens it.
    /// </summary>
    /// <remarks>
    /// Written on every open and close of the window rather than at exit, so a crash leaves the
    /// same answer a clean exit would.
    /// </remarks>
    public bool ShowDiagnostics { get; set; }

    /// <summary>Gets or sets the living backdrop's style and intensity.</summary>
    public BackdropSettings Backdrop { get; set; } = new();

    /// <summary>
    /// Brings a just-loaded settings object forward to what this build supports.
    /// </summary>
    /// <remarks>
    /// Applied on load only, not in <see cref="Clone"/>: a snapshot copied from the live settings
    /// has already been migrated, and re-running the rewrite on every copy would hide a field that
    /// somehow got set back to a retired value.
    /// <para>
    /// The one migration so far is <c>ConnectionMode.Auto</c> to
    /// <see cref="ConnectionMode.AdvertiseOnly"/>. Advertising is the closer half of what Auto did:
    /// a server that was reaching this player by connecting to it keeps working, where
    /// <see cref="ConnectionMode.DiscoverOnly"/> would silently stop answering.
    /// </para>
    /// </remarks>
    /// <returns>True when something was rewritten, which the caller may want to log.</returns>
    public bool ApplyMigrations()
    {
        // Reading the obsolete member is the point: this is the code that retires it, and it has to
        // name the value it is retiring. Suppressed here only, so every other use stays an error.
#pragma warning disable CS0618 // ConnectionMode.Auto is obsolete and removed in SDK 10.0.0
        if (ConnectionMode != ConnectionMode.Auto)
#pragma warning restore CS0618
        {
            return false;
        }

        ConnectionMode = ConnectionMode.AdvertiseOnly;
        return true;
    }

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
