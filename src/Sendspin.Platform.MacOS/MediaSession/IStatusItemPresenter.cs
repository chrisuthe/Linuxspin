using Sendspin.Core.MediaSession;

namespace Sendspin.Platform.MacOS.MediaSession;

/// <summary>
/// The macOS menu bar status item.
/// </summary>
/// <remarks>
/// <para>
/// Declared here rather than in <c>Sendspin.Core</c> on purpose. A status item is an
/// <c>NSStatusBar</c>/<c>NSStatusItem</c>/<c>NSMenu</c> triple with no cross-platform analogue —
/// Windows 11 hides notification icons by default and documents that this cannot be controlled
/// programmatically, and Linux needs StatusNotifierItem over D-Bus with a shell extension that
/// half the distributions do not ship. A shared abstraction over those three would be a shape none
/// of them fits. Core stays free of AppKit; the app resolves this interface only when it is running
/// on macOS.
/// </para>
/// <para>
/// State arrives as the same <see cref="MediaSessionState"/> the media session gets, and commands
/// leave as the same <see cref="MediaSessionIntentEventArgs"/>, so the menu and Control Center
/// cannot disagree about what the player can do.
/// </para>
/// </remarks>
public interface IStatusItemPresenter : IAsyncDisposable
{
    /// <summary>
    /// Gets whether the item is currently in the menu bar.
    /// </summary>
    bool IsVisible { get; }

    /// <summary>
    /// Raised when a menu item is chosen. Arrives on the main thread.
    /// </summary>
    event EventHandler<MediaSessionIntentEventArgs>? IntentReceived;

    /// <summary>
    /// Puts the item in the menu bar, creating it on first use.
    /// </summary>
    void Show();

    /// <summary>
    /// Takes the item out of the menu bar, keeping it ready for a later <see cref="Show"/>.
    /// </summary>
    void Hide();

    /// <summary>
    /// Updates the menu to match the player's state.
    /// </summary>
    void Update(MediaSessionState state);
}
