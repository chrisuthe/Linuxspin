namespace Sendspin.Core.Configuration;

/// <summary>
/// Mints and normalises this installation's protocol identity.
/// </summary>
/// <remarks>
/// <para>
/// <c>client_id</c> must be stable and unique. Deriving it from the machine name, as this
/// app used to, is neither: two installations on one host collide, and renaming the host
/// re-registers the player as a brand new endpoint, losing whatever group and calibration
/// the server had associated with it. So it is generated once and persisted.
/// </para>
/// <para>
/// It is also platform-neutral. The old identifier was
/// <c>sendspin-linux-{MachineName}</c> on every platform including Windows, which is both
/// wrong and confusing in server logs.
/// </para>
/// </remarks>
public static class ClientIdentity
{
    /// <summary>
    /// Prefix for generated identifiers. Deliberately says "desktop", not an OS name: the
    /// identity persists across an OS reinstall or a move between platforms, and baking the
    /// current OS into it would make it a lie later.
    /// </summary>
    private const string IdPrefix = "sendspin-desktop-";

    /// <summary>
    /// Fills in <see cref="PlayerSettings.ClientId"/> and
    /// <see cref="PlayerSettings.PlayerName"/> when either is missing.
    /// </summary>
    /// <returns>
    /// True when something was generated and the settings therefore need persisting.
    /// </returns>
    public static bool EnsureIdentity(PlayerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var changed = false;

        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            settings.ClientId = NewClientId();
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.PlayerName))
        {
            settings.PlayerName = DefaultPlayerName();
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Generates a fresh identifier. Random rather than derived, because a derived value is
    /// only as stable as whatever it was derived from.
    /// </summary>
    public static string NewClientId() => IdPrefix + Guid.NewGuid().ToString("n");

    /// <summary>
    /// Suggests a display name for a new installation.
    /// </summary>
    /// <remarks>
    /// The machine name is fine here — unlike the identifier, a display name is meant to
    /// change when the user renames their machine, and it is what makes the player
    /// recognisable in a controller's list. It falls back to a plain label when the host name
    /// is unavailable or is a placeholder.
    /// </remarks>
    public static string DefaultPlayerName()
    {
        var host = Environment.MachineName;

        if (string.IsNullOrWhiteSpace(host) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "Sendspin Desktop";
        }

        return host;
    }

    /// <summary>
    /// Gets the OS label used in <c>device_info</c>, for logs and server-side display.
    /// </summary>
    /// <remarks>
    /// Resolved at runtime rather than from a compile-time constant, so a single build can
    /// never describe itself as the wrong platform.
    /// </remarks>
    public static string PlatformLabel
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return "Windows";
            }

            if (OperatingSystem.IsMacOS())
            {
                return "macOS";
            }

            if (OperatingSystem.IsLinux())
            {
                return "Linux";
            }

            return "Desktop";
        }
    }
}
