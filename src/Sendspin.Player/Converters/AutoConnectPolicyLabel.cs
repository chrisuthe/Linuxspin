using System.Globalization;
using Avalonia.Data.Converters;
using Sendspin.Core.Configuration;

namespace Sendspin.Player.Converters;

/// <summary>
/// The auto-connect policies as the settings row names them. The picker binds the enum straight
/// through to the setting; only what it shows goes through here.
/// </summary>
public sealed class AutoConnectPolicyLabel : IValueConverter
{
    /// <summary>The one instance, for <c>x:Static</c>.</summary>
    public static AutoConnectPolicyLabel Instance { get; } = new();

    /// <summary>The row's name for a policy.</summary>
    public static string For(AutoConnectPolicy policy) => policy switch
    {
        AutoConnectPolicy.Never => "Never",
        AutoConnectPolicy.JustOnce => "Just once",
        AutoConnectPolicy.Always => "Always",
        _ => policy.ToString(),
    };

    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AutoConnectPolicy policy ? For(policy) : value?.ToString();

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The label is display-only; the picker binds the value itself.");
}
