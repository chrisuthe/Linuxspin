using AppKit;
using CoreFoundation;
using Microsoft.Extensions.Logging;
using Sendspin.Core.MediaSession;

namespace Sendspin.Platform.MacOS.MediaSession;

/// <summary>
/// A menu bar status item with transport controls, built directly on AppKit.
/// </summary>
/// <remarks>
/// <para>
/// AppKit rather than Avalonia's <c>TrayIcon</c>, and the reasons are concrete: on
/// <c>TrayIcon</c>'s native menu, <c>NativeMenuItem.IsChecked</c> does not work (Avalonia#8751)
/// and <c>NativeMenu.Opening</c> never fires (Avalonia#8076), so a menu that has to show the
/// current track and reflect shuffle or repeat cannot be built on it. Talking to
/// <c>NSStatusBar</c> here also keeps AppKit types out of the Core contracts.
/// </para>
/// <para>
/// This does not fight Avalonia for ownership. Avalonia bootstraps <c>NSApplication</c> and owns
/// the application menu, but <c>NSStatusBar.SystemStatusBar</c> is a separate system-wide object
/// that hands out independent items; Avalonia's own macOS tray implementation asks it for one the
/// same way. Two requirements come with that, both on the app head and both recorded in
/// <c>MacHostingNotes.md</c>: <c>NSApplication.Init()</c> must have run, and every call here has
/// to be on the main thread, which is why each entry point hops to the main dispatch queue.
/// </para>
/// </remarks>
public sealed class StatusItemPresenter : IStatusItemPresenter
{
    private readonly ILogger<StatusItemPresenter> _logger;

    private NSStatusItem? _statusItem;
    private NSMenuItem? _nowPlayingItem;
    private NSMenuItem? _playPauseItem;
    private NSMenuItem? _nextItem;
    private NSMenuItem? _previousItem;
    private bool _isDisposed;

    public StatusItemPresenter(ILogger<StatusItemPresenter> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public event EventHandler<MediaSessionIntentEventArgs>? IntentReceived;

    /// <inheritdoc/>
    public bool IsVisible => _statusItem?.Visible ?? false;

    /// <inheritdoc/>
    public void Show()
    {
        if (_isDisposed)
        {
            return;
        }

        OnMainThread(() =>
        {
            var item = _statusItem ??= Build();
            item.Visible = true;
        });
    }

    /// <inheritdoc/>
    public void Hide()
    {
        if (_isDisposed)
        {
            return;
        }

        OnMainThread(() =>
        {
            if (_statusItem is not null)
            {
                _statusItem.Visible = false;
            }
        });
    }

    /// <inheritdoc/>
    public void Update(MediaSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (_isDisposed)
        {
            return;
        }

        OnMainThread(() =>
        {
            if (_statusItem is null)
            {
                return;
            }

            if (_nowPlayingItem is not null)
            {
                _nowPlayingItem.Title = DescribeTrack(state);
            }

            if (_playPauseItem is not null)
            {
                _playPauseItem.Title = state.Status == MediaPlaybackStatus.Playing ? "Pause" : "Play";
            }

            if (_nextItem is not null)
            {
                _nextItem.Enabled = state.CanGoNext;
            }

            if (_previousItem is not null)
            {
                _previousItem.Enabled = state.CanGoPrevious;
            }

            if (_statusItem.Button is { } button)
            {
                button.ToolTip = DescribeTrack(state);
            }
        });
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return ValueTask.CompletedTask;
        }

        _isDisposed = true;

        OnMainThread(() =>
        {
            if (_statusItem is not null)
            {
                NSStatusBar.SystemStatusBar.RemoveStatusItem(_statusItem);
                _statusItem.Dispose();
                _statusItem = null;
            }

            _nowPlayingItem = null;
            _playPauseItem = null;
            _nextItem = null;
            _previousItem = null;
        });

        return ValueTask.CompletedTask;
    }

    private static string DescribeTrack(MediaSessionState state)
    {
        if (state.Title is null)
        {
            return "Sendspin";
        }

        return state.Artist is null ? state.Title : $"{state.Title} — {state.Artist}";
    }

    /// <summary>
    /// Runs an AppKit action on the main thread.
    /// </summary>
    /// <remarks>
    /// Dispatched rather than asserted, because <see cref="Update"/> is driven by server state
    /// that arrives on a socket thread. Already being on the main queue is the common case and is
    /// handled inline so a menu click's own handler is not deferred a turn.
    /// </remarks>
    private static void OnMainThread(Action action)
    {
        if (NSThread.IsMain)
        {
            action();
            return;
        }

        DispatchQueue.MainQueue.DispatchAsync(action);
    }

    private NSStatusItem Build()
    {
        var item = NSStatusBar.SystemStatusBar.CreateStatusItem(NSStatusItemLength.Square);

        if (item.Button is { } button)
        {
            // A template image is what lets the menu bar recolour it for light, dark and the
            // highlighted state; a plain image stays one colour and looks wrong in two of the
            // three. Title is the fallback for a system with no such symbol.
            var symbol = NSImage.GetSystemSymbol("music.note", "Sendspin");
            if (symbol is not null)
            {
                symbol.Template = true;
                button.Image = symbol;
            }
            else
            {
                button.Title = "Sendspin";
            }

            button.ToolTip = "Sendspin";
        }

        var menu = new NSMenu();

        _nowPlayingItem = new NSMenuItem("Sendspin")
        {
            // A label, not a command. Disabling it is what makes it read as one.
            Enabled = false
        };
        menu.AddItem(_nowPlayingItem);
        menu.AddItem(NSMenuItem.SeparatorItem);

        _playPauseItem = AddCommand(menu, "Play", MediaSessionIntent.TogglePlayPause);
        _nextItem = AddCommand(menu, "Next", MediaSessionIntent.Next);
        _previousItem = AddCommand(menu, "Previous", MediaSessionIntent.Previous);

        menu.AddItem(NSMenuItem.SeparatorItem);

        AddCommand(menu, "Show Sendspin", MediaSessionIntent.Raise);
        AddCommand(menu, "Quit Sendspin", MediaSessionIntent.Quit);

        item.Menu = menu;

        _logger.LogInformation("Menu bar status item created");
        return item;
    }

    private NSMenuItem AddCommand(NSMenu menu, string title, MediaSessionIntent intent)
    {
        // The handler raises an intent and nothing more: the same path a click in the window
        // takes, so the server stays the authority on transport state.
        var item = new NSMenuItem(title, (_, _) =>
            IntentReceived?.Invoke(this, new MediaSessionIntentEventArgs(intent)));

        menu.AddItem(item);
        return item;
    }
}
