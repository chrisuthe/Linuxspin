namespace Sendspin.Core.MediaSession;

/// <summary>
/// Where a track is now, projected from the last position the server reported and the time
/// that has passed since.
/// </summary>
/// <remarks>
/// The protocol reports position only when metadata changes, so the UI advances it between
/// reports. Advancing by a timer's nominal interval assumes the timer is on time, and on the
/// Wayland head it is not (see the clock table in <c>docs/ARCHITECTURE.md</c>): a 500 ms timer
/// fires on time or one quantum late, so the bar walked in uneven steps. Advancing by the
/// measured time since the anchor is right whatever the timer does.
/// </remarks>
public sealed class AnchoredPosition
{
    private TimeSpan _position;
    private TimeSpan _anchoredAt;

    /// <summary>Gets the position the projection starts from.</summary>
    public TimeSpan Position => _position;

    /// <summary>Gets the clock reading the position was taken at.</summary>
    public TimeSpan AnchoredAt => _anchoredAt;

    /// <summary>
    /// Records that the track was at <paramref name="position"/> when the clock read
    /// <paramref name="now"/>.
    /// </summary>
    public void Anchor(TimeSpan position, TimeSpan now)
    {
        _position = position;
        _anchoredAt = now;
    }

    /// <summary>
    /// Gets the position when the clock reads <paramref name="now"/>. A reading earlier than the
    /// anchor returns the anchor rather than a position before it.
    /// </summary>
    public TimeSpan At(TimeSpan now) => now <= _anchoredAt ? _position : _position + (now - _anchoredAt);
}
