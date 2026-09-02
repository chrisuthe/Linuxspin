using System.Globalization;
using Avalonia.Data.Converters;
using Sendspin.Core.Configuration;

namespace Sendspin.Player.Converters;

/// <summary>
/// The backdrop styles as the settings row names them. The picker binds the enum straight
/// through to the setting; only what it shows goes through here.
/// </summary>
public sealed class BackdropModeLabel : IValueConverter
{
    /// <summary>The one instance, for <c>x:Static</c>.</summary>
    public static BackdropModeLabel Instance { get; } = new();

    /// <summary>The row's name for a style.</summary>
    public static string For(BackdropMode mode) => mode switch
    {
        BackdropMode.Off => "Off",
        BackdropMode.AmbientGlow => "Ambient Glow",
        BackdropMode.BreathingArt => "Breathing Art",
        _ => mode.ToString(),
    };

    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is BackdropMode mode ? For(mode) : value?.ToString();

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The label is display-only; the picker binds the value itself.");
}
