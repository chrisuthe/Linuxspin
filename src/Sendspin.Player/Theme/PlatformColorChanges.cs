using Avalonia.Platform;
using Sendspin.Core.Theme;

namespace Sendspin.Player.Theme;

/// <summary>
/// The platform's colour-changed event, reported once per real change.
/// </summary>
/// <remarks>
/// The one subscription to <see cref="IPlatformSettings.ColorValuesChanged"/> in the app.
/// Anything that recomputes from the accent or the variant — the on-accent brush, and the
/// backdrop palette when it arrives — listens to <see cref="Changed"/> instead, behind the
/// <see cref="SystemColorChangeFilter"/> that swallows Windows' duplicate reports.
/// </remarks>
internal sealed class PlatformColorChanges : IDisposable
{
    private readonly IPlatformSettings _settings;
    private readonly SystemColorChangeFilter _filter = new();

    public PlatformColorChanges(IPlatformSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _settings.ColorValuesChanged += OnColorValuesChanged;
    }

    /// <summary>
    /// Raised with the platform's colour values when they differ from the last ones raised.
    /// </summary>
    public event Action<PlatformColorValues>? Changed;

    /// <summary>
    /// Raises <see cref="Changed"/> with the current values, so a subscriber starts in step.
    /// </summary>
    public void Publish() => Report(_settings.GetColorValues());

    /// <inheritdoc/>
    public void Dispose() => _settings.ColorValuesChanged -= OnColorValuesChanged;

    private static SystemColors ToSystemColors(PlatformColorValues values) =>
        new(values.AccentColor1.ToUInt32(), values.ThemeVariant == PlatformThemeVariant.Dark);

    private void OnColorValuesChanged(object? sender, PlatformColorValues values) => Report(values);

    private void Report(PlatformColorValues values)
    {
        if (_filter.Accept(ToSystemColors(values)))
        {
            Changed?.Invoke(values);
        }
    }
}
