using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Client;

namespace Sendspin.Core.Configuration;

/// <summary>
/// The single authority for live settings: everything reads
/// <see cref="Current"/> and mutates through <see cref="Update"/>.
/// </summary>
/// <remarks>
/// One object owns the settings so that there is exactly one config file, one in-memory
/// copy, and one place a save can be triggered from. Components that persist through side
/// doors are how a repo ends up with a <c>config.json</c> on one platform and a
/// <c>settings.json</c> on another.
/// </remarks>
public sealed class SettingsService
{
    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsService> _logger;
    private readonly object _gate = new();

    private PlayerSettings _current;

    public SettingsService(ISettingsStore store, ILogger<SettingsService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
        _current = store.Load();

        // A first run has no client id. Mint one now and persist it immediately, so the
        // identity we advertise is the identity we will still advertise after a restart.
        if (ClientIdentity.EnsureIdentity(_current))
        {
            _logger.LogInformation("Generated client identity {ClientId} ({PlayerName})",
                _current.ClientId, _current.PlayerName);
            Persist(_current);
        }
    }

    /// <summary>
    /// Raised after settings change, on the thread that called <see cref="Update"/>.
    /// </summary>
    public event EventHandler<PlayerSettings>? Changed;

    /// <summary>
    /// Gets the current settings, as a stable snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Safe to hold. <see cref="Update"/> publishes a new instance rather than mutating this one, so
    /// a reference obtained here keeps the values it had: a reader can never see a half-applied
    /// change, and can iterate <see cref="PlayerSettings.Devices"/> without racing a write.
    /// </para>
    /// <para>
    /// Read-only <em>by contract</em>, not by enforcement. This is the live current snapshot, so
    /// mutating it is a bug — the change bypasses persistence and observers, and the next
    /// <see cref="Update"/> will copy it forward. Go through <see cref="Update"/>.
    /// </para>
    /// </remarks>
    public PlayerSettings Current => Volatile.Read(ref _current);

    /// <summary>
    /// Applies <paramref name="mutate"/> to the settings, persists the result, and raises
    /// <see cref="Changed"/>.
    /// </summary>
    /// <remarks>
    /// A failed write is logged and swallowed rather than propagated: the in-memory change
    /// has already taken effect, and throwing here would leave the app's behaviour and its
    /// UI disagreeing. The log line is the record that the change will not survive a restart.
    /// </remarks>
    public void Update(Action<PlayerSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        PlayerSettings snapshot;

        // Copy, mutate the copy, persist it, then publish it. Mutating the live instance in place
        // would let a reader observe one field from before the change and another from after, and
        // would let the serializer walk the Devices dictionary while another thread inserts into it.
        // The cost is one copy per change rather than per read, and changes are rare because the
        // call sites only write when a value actually moved.
        lock (_gate)
        {
            snapshot = _current.Clone();
            mutate(snapshot);
            Persist(snapshot);
            Volatile.Write(ref _current, snapshot);
        }

        Changed?.Invoke(this, snapshot);
    }

    private void Persist(PlayerSettings settings)
    {
        try
        {
            _store.Save(settings);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Settings could not be written; this change will not survive a restart");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Not permitted to write settings; this change will not survive a restart");
        }
        catch (JsonException ex)
        {
            // Serializing our own settings type should not fail. If it does, it must not escape into
            // whatever called Update — which includes SDK event handlers on background threads,
            // where an unexpected exception would take the process down.
            _logger.LogError(ex, "Settings could not be serialized; this change will not survive a restart");
        }
    }
}

/// <summary>
/// Persists the player's <c>static_delay_ms</c> for the SDK.
/// </summary>
/// <remarks>
/// <para>
/// The spec requires <c>static_delay_ms</c> to survive a reboot, and the SDK cannot choose a
/// storage location, so it delegates through <see cref="IStaticDelayStore"/>. This
/// implementation deliberately writes into the same <see cref="PlayerSettings"/> file as
/// everything else rather than a file of its own — a second config file is exactly the
/// defect this rebuild is removing.
/// </para>
/// <para>
/// The SDK documents that a value from a GroupSync calibration offset may be negative, and
/// that it should be round-tripped as given, so no clamping happens here.
/// </para>
/// </remarks>
public sealed class SettingsStaticDelayStore : IStaticDelayStore
{
    private readonly SettingsService _settings;
    private readonly ILogger<SettingsStaticDelayStore> _logger;

    public SettingsStaticDelayStore(SettingsService settings, ILogger<SettingsStaticDelayStore> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc/>
    public double? Load() => _settings.Current.StaticDelayMs;

    /// <inheritdoc/>
    public void Save(double staticDelayMs)
    {
        _logger.LogInformation("Persisting static delay {StaticDelayMs} ms", staticDelayMs);
        _settings.Update(s => s.StaticDelayMs = staticDelayMs);
    }
}
