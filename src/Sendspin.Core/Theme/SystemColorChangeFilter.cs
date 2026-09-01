namespace Sendspin.Core.Theme;

/// <summary>
/// The two platform colour facts the app reacts to: the accent, and whether the variant is dark.
/// </summary>
public readonly record struct SystemColors(uint AccentArgb, bool IsDark);

/// <summary>
/// Drops platform colour reports that change nothing.
/// </summary>
/// <remarks>
/// Windows raises its colour-changed event as a storm: one accent change measured as about twenty
/// events in 600 ms, one of them with values identical to the last, and every variant flip raises
/// it twice (Windows 11 10.0.26200, Avalonia 12.1.1). Everything hung off that event — the
/// on-accent brush today, the backdrop palette in a later phase — goes through one of these, so
/// the work happens once per real change. Cheap on purpose: one struct compare per report.
/// </remarks>
public sealed class SystemColorChangeFilter
{
    private SystemColors? _last;

    /// <summary>
    /// Returns true when the values differ from the last accepted ones, and records them.
    /// </summary>
    public bool Accept(SystemColors colors)
    {
        if (_last == colors)
        {
            return false;
        }

        _last = colors;
        return true;
    }
}
