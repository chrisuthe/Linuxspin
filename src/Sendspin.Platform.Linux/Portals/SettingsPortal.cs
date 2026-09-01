using Sendspin.Core.Platform;
using Tmds.DBus.Protocol;

namespace Sendspin.Platform.Linux.Portals;

/// <summary>
/// One-shot reads from <c>org.freedesktop.portal.Settings</c> that have to happen before Avalonia
/// exists.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia reads the portal itself for the colour scheme and the accent, live. It does not read
/// the interface font: <c>$Default</c> is fontconfig's answer to an empty pattern, which on the
/// host is usually the desktop font by coincidence and inside the Flatpak is DejaVu Sans. The
/// portal's <c>org.gnome.desktop.interface/font-name</c> is the desktop's actual setting, and the
/// KDE backend serves it as well as the GNOME one (measured on Plasma 6.7: <c>"Noto Sans  10"</c>).
/// </para>
/// <para>
/// Synchronous and bounded on purpose. <c>FontManagerOptions</c> is fixed when the app builder
/// runs, before the dispatcher or the service container exist, so this opens its own short-lived
/// connection rather than going through <see cref="DBus.SessionBus"/>, and gives up after the
/// timeout so a hung portal cannot hold the window back. No bus, no portal, no key: null, and the
/// caller leaves the default alone.
/// </para>
/// </remarks>
public static class SettingsPortal
{
    private const string SettingsInterface = "org.freedesktop.portal.Settings";
    private const string InterfaceNamespace = "org.gnome.desktop.interface";
    private const string FontNameKey = "font-name";

    /// <summary>
    /// Reads the desktop's interface font family, or null when the portal does not serve one.
    /// </summary>
    public static string? TryReadInterfaceFontFamily(TimeSpan timeout)
    {
        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address))
        {
            return null;
        }

        try
        {
            using var connection = new DBusConnection(address);
            var read = ReadFontNameAsync(connection);

            if (read.Wait(timeout))
            {
                return DesktopFontName.ParseFamily(read.Result);
            }

            // Disposing the connection faults the pending read; observe it so it does not
            // surface as an unobserved task exception later.
            read.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return null;
        }
        catch (Exception)
        {
            // Best effort, at start-up, before a logger exists: a portal that errors, a bus that
            // refuses, a variant that is not a string — none of them may stop the app starting.
            return null;
        }
    }

    private static async Task<string> ReadFontNameAsync(DBusConnection connection)
    {
        await connection.ConnectAsync().ConfigureAwait(false);

        return await connection.CallMethodAsync(
            CreateReadOneMessage(connection),
            static (Message message, object? state) => message.GetBodyReader().ReadVariantValue().GetString(),
            null).ConfigureAwait(false);
    }

    private static MessageBuffer CreateReadOneMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            PortalRequest.Destination, PortalRequest.ObjectPath, SettingsInterface,
            "ReadOne", "ss", MessageFlags.None);
        writer.WriteString(InterfaceNamespace);
        writer.WriteString(FontNameKey);

        return writer.CreateMessage();
    }
}
