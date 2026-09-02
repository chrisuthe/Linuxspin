using System.Globalization;
using Avalonia.Data.Converters;
using Sendspin.SDK.Client;

namespace Sendspin.Player.Converters;

/// <summary>
/// The connection modes as the settings row names them. The picker binds the enum straight
/// through to the setting; only what it shows goes through here.
/// </summary>
public sealed class ConnectionModeLabel : IValueConverter
{
    /// <summary>The one instance, for <c>x:Static</c>.</summary>
    public static ConnectionModeLabel Instance { get; } = new();

    /// <summary>The row's name for a mode.</summary>
    /// <remarks>
    /// <c>ConnectionMode.Auto</c> is deliberately not named: it is obsolete, the picker does
    /// not offer it, and a persisted one is rewritten on load.
    /// </remarks>
    public static string For(ConnectionMode mode) => mode switch
    {
        ConnectionMode.AdvertiseOnly => "Advertise to servers, and let a server connect",
        ConnectionMode.DiscoverOnly => "Discover servers, and connect from here",
        _ => mode.ToString(),
    };

    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ConnectionMode mode ? For(mode) : value?.ToString();

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The label is display-only; the picker binds the value itself.");
}
