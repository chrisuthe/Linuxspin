namespace Sendspin.Core.MediaSession;

/// <summary>
/// What <see cref="ScheduledValue{T}.Offer"/> did with a value.
/// </summary>
public enum ScheduledOffer
{
    /// <summary>The value was due, so it became current at once.</summary>
    Applied,

    /// <summary>The value is not due yet and is being held as the pending one.</summary>
    Held
}

/// <summary>
/// A value that takes effect at a scheduled time: what is current now, plus at most one pending
/// value waiting for its moment.
/// </summary>
/// <remarks>
/// <para>
/// This is the spec's model for both scheduled artwork (<c>roles/artwork/v1.md</c>) and scheduled
/// metadata (<c>roles/metadata/v1.md</c>), which state the same rules in different words. A server
/// with a queue sends the next track's picture and metadata a few seconds before the audible track
/// change, stamped with the server-clock time they should take effect; applying them on arrival is
/// what made the cover flip early. The rules, with times already converted to this machine's clock:
/// </para>
/// <list type="bullet">
/// <item>A value whose time is past or present applies at once and discards any pending value.</item>
/// <item>A value whose time is in the future becomes the pending value, replacing the one held.</item>
/// <item>The pending value becomes current once its time is reached. It is never dropped for
/// lateness: a promotion that runs late still promotes.</item>
/// <item>A cancel discards the pending value and leaves the current one alone.</item>
/// <item>A clear is not a separate operation: the spec expresses it as an empty value that follows
/// the same timing, so a future-stamped <see cref="Offer"/> of null schedules the clear.</item>
/// </list>
/// <para>
/// Pure and clock-free on purpose: every method takes the local time as an argument, so the rules
/// are testable without a timer and the service owning an instance decides when to ask. It is not
/// thread-safe; the owner serialises access.
/// </para>
/// </remarks>
/// <typeparam name="T">The scheduled value. Null is the empty value.</typeparam>
public sealed class ScheduledValue<T>
    where T : class
{
    private T? _pending;
    private long _pendingDue;
    private bool _hasPending;

    /// <summary>Gets the value in effect now, or null when there is none.</summary>
    public T? Current { get; private set; }

    /// <summary>
    /// Gets the local time, in microseconds, at which <see cref="Current"/> took effect. Zero until
    /// something has been applied.
    /// </summary>
    /// <remarks>
    /// For metadata this is the origin the spec's progress formula runs from; the position is
    /// projected from here, not from when the message arrived.
    /// </remarks>
    public long CurrentSince { get; private set; }

    /// <summary>Gets whether a value is being held for a future time.</summary>
    public bool HasPending => _hasPending;

    /// <summary>Gets the held value, or null when nothing is pending.</summary>
    public T? Pending => _hasPending ? _pending : null;

    /// <summary>
    /// Gets the local time, in microseconds, at which the pending value is due, or null when nothing
    /// is pending. The owner arms its timer on this.
    /// </summary>
    public long? NextDue => _hasPending ? _pendingDue : null;

    /// <summary>
    /// Offers a value that should take effect at <paramref name="dueLocalMicros"/>.
    /// </summary>
    /// <param name="value">The value, or null for the empty value (a scheduled clear).</param>
    /// <param name="dueLocalMicros">When it takes effect, on the local clock.</param>
    /// <param name="nowLocalMicros">The local clock now.</param>
    /// <returns>Whether the value was applied at once or is being held.</returns>
    public ScheduledOffer Offer(T? value, long dueLocalMicros, long nowLocalMicros)
    {
        if (dueLocalMicros <= nowLocalMicros)
        {
            // Past or present: applies now and, per the spec, cancels whatever was scheduled. This
            // is also how a server cancels a scheduled update — by re-sending the current state.
            Apply(value, dueLocalMicros);
            return ScheduledOffer.Applied;
        }

        _pending = value;
        _pendingDue = dueLocalMicros;
        _hasPending = true;
        return ScheduledOffer.Held;
    }

    /// <summary>
    /// Makes the pending value current if its time has come.
    /// </summary>
    /// <returns>True when something was promoted.</returns>
    public bool Promote(long nowLocalMicros)
    {
        if (!_hasPending || _pendingDue > nowLocalMicros)
        {
            return false;
        }

        Apply(_pending, _pendingDue);
        return true;
    }

    /// <summary>Discards the pending value. The current value is unaffected.</summary>
    public void Cancel()
    {
        _pending = null;
        _hasPending = false;
    }

    /// <summary>
    /// Forgets both the current and the pending value, as on a stream end or a disconnect: there is
    /// no longer a stream for either to belong to.
    /// </summary>
    public void Reset()
    {
        Cancel();
        Current = null;
        CurrentSince = 0;
    }

    private void Apply(T? value, long since)
    {
        Current = value;
        CurrentSince = since;
        Cancel();
    }
}
