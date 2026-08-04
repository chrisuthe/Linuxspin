using Sendspin.Core.MediaSession;

namespace Sendspin.Core.Presence;

/// <summary>
/// Publishes what is playing to something that is not an OS media surface.
/// </summary>
/// <remarks>
/// <para>
/// Discord Rich Presence is the only implementation today. It is kept behind its own contract
/// rather than folded in with <see cref="IMediaSession"/> because it is a *feature*, not a
/// platform integration: it is off by default, it is not what makes the app feel native, and
/// it must not be on the critical path to playing audio.
/// </para>
/// <para>
/// Publish-only. Presence surfaces do not send transport commands here, so there is no intent
/// channel to reason about.
/// </para>
/// </remarks>
public interface IPresenceService : IAsyncDisposable
{
    /// <summary>
    /// Gets whether presence is currently being published.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Enables or disables publishing. Called when the user changes the setting, so it must
    /// be safe to call repeatedly and in either direction.
    /// </summary>
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes the current state, or clears presence when nothing is playing. A no-op while
    /// disabled.
    /// </summary>
    void Publish(MediaSessionState state, string? serverName);
}

/// <summary>
/// The presence service used when no presence backend is compiled in or configured.
/// </summary>
public sealed class NullPresenceService : IPresenceService
{
    /// <inheritdoc/>
    public bool IsConnected => false;

    /// <inheritdoc/>
    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc/>
    public void Publish(MediaSessionState state, string? serverName)
    {
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
