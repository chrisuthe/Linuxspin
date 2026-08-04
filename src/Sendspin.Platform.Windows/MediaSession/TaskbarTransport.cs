using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sendspin.Core.MediaSession;

namespace Sendspin.Platform.Windows.MediaSession;

/// <summary>
/// Shows a play or pause badge on the app's taskbar button, via
/// <c>ITaskbarList3::SetOverlayIcon</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the taskbar rather than the notification area.</strong> Windows 11 hides
/// notification-area icons by default and Microsoft documents that an application cannot change
/// that, so a tray icon is not a transport a user can rely on seeing. Microsoft's own stated
/// substitutes are the taskbar overlay icon and thumbnail toolbar buttons, both of which work
/// from a window handle in an unpackaged process.
/// </para>
/// <para>
/// <strong>What is here and what is not.</strong> This is the overlay-icon half: an at-a-glance
/// indication of whether the player is playing, always visible while the app has a taskbar
/// button. The other half — <c>ThumbBarAddButtons</c>, whose documentation cites media transport
/// controls as its example — is <em>not</em> implemented, and deliberately so rather than for
/// lack of interop. Thumbbar buttons report their clicks as <c>WM_COMMAND</c> messages with
/// <c>THBN_CLICKED</c> in the high word of <c>wParam</c>, delivered to the window's own message
/// procedure. This project holds no window and no UI framework, so it cannot subclass one; wiring
/// them up means the app forwarding <c>WM_COMMAND</c> back here, which is a contract across two
/// projects and belongs in a change that can be tested against a real window. Until then the
/// buttons would exist but do nothing, which is worse than not offering them. The vtable slots
/// are declared in <see cref="ITaskbarList3"/> ready for it.
/// </para>
/// <para>
/// <strong>Threading and lifetime.</strong> Call from the thread that owns the window: the
/// shell's taskbar object is apartment-threaded. The taskbar button must also exist before an
/// overlay will show, which is why activation is deferred to the first
/// <see cref="Publish"/> rather than done in a constructor.
/// </para>
/// </remarks>
public sealed class TaskbarTransport : IDisposable
{
    private readonly Func<nint?> _windowHandleProvider;
    private readonly ILogger<TaskbarTransport> _logger;

    private ITaskbarList3? _taskbarList;
    private nint _playIcon;
    private nint _pauseIcon;
    private MediaPlaybackStatus? _shownStatus;
    private bool _activationAttempted;
    private bool _disposed;

    /// <param name="windowHandleProvider">
    /// Returns the main window's handle, or null while there is no window yet.
    /// </param>
    /// <param name="logger">Logger for diagnostics.</param>
    public TaskbarTransport(Func<nint?> windowHandleProvider, ILogger<TaskbarTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(windowHandleProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _windowHandleProvider = windowHandleProvider;
        _logger = logger;
    }

    /// <summary>
    /// Gets whether the shell's taskbar object was obtained. False until the first
    /// <see cref="Publish"/>, and permanently false where there is no shell.
    /// </summary>
    public bool IsAvailable => _taskbarList is not null;

    /// <summary>
    /// Updates the badge to match the state. A no-op when the shell provides no taskbar object.
    /// </summary>
    public void Publish(MediaSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (_disposed)
        {
            return;
        }

        var handle = ResolveWindowHandle();
        if (handle is null)
        {
            return;
        }

        var taskbarList = EnsureTaskbarList();
        if (taskbarList is null)
        {
            return;
        }

        if (_shownStatus == state.Status)
        {
            return;
        }

        var icon = state.Status switch
        {
            MediaPlaybackStatus.Playing => EnsureIcon(ref _playIcon, TaskbarInterop.BadgeGlyph.Play),
            MediaPlaybackStatus.Paused => EnsureIcon(ref _pauseIcon, TaskbarInterop.BadgeGlyph.Pause),
            _ => 0
        };

        var description = state.Status switch
        {
            MediaPlaybackStatus.Playing => "Playing",
            MediaPlaybackStatus.Paused => "Paused",
            _ => null
        };

        try
        {
            taskbarList.SetOverlayIcon(handle.Value, icon, description);
            _shownStatus = state.Status;
        }
        catch (COMException ex)
        {
            _logger.LogDebug(ex, "The taskbar overlay icon could not be set");
        }
    }

    /// <summary>
    /// Removes the badge, leaving the taskbar button as the shell drew it.
    /// </summary>
    public void Clear()
    {
        var taskbarList = _taskbarList;
        var handle = ResolveWindowHandle();

        if (taskbarList is null || handle is null)
        {
            return;
        }

        try
        {
            taskbarList.SetOverlayIcon(handle.Value, 0, null);
            _shownStatus = null;
        }
        catch (COMException ex)
        {
            _logger.LogDebug(ex, "The taskbar overlay icon could not be cleared");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Clear();

        if (_playIcon != 0)
        {
            TaskbarInterop.DestroyIcon(_playIcon);
            _playIcon = 0;
        }

        if (_pauseIcon != 0)
        {
            TaskbarInterop.DestroyIcon(_pauseIcon);
            _pauseIcon = 0;
        }

        if (_taskbarList is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _taskbarList = null;
    }

    private nint? ResolveWindowHandle()
    {
        try
        {
            var handle = _windowHandleProvider();
            return handle is null || handle.Value == 0 ? null : handle;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "The main window handle could not be read for the taskbar transport");
            return null;
        }
    }

    private ITaskbarList3? EnsureTaskbarList()
    {
        if (_taskbarList is not null)
        {
            return _taskbarList;
        }

        if (_activationAttempted)
        {
            return null;
        }

        _activationAttempted = true;

        var taskbarList = TaskbarInterop.TryCreateTaskbarList(out var hresult);
        if (taskbarList is null)
        {
            _logger.LogInformation(
                "The shell provided no taskbar list object (HRESULT 0x{HResult:X8}); no playback badge will be shown",
                hresult);
            return null;
        }

        try
        {
            taskbarList.HrInit();
        }
        catch (COMException ex)
        {
            _logger.LogInformation(ex, "The taskbar list object would not initialise; no playback badge will be shown");
            (taskbarList as IDisposable)?.Dispose();
            return null;
        }

        _taskbarList = taskbarList;
        _logger.LogDebug("Taskbar overlay transport ready");

        return taskbarList;
    }

    private nint EnsureIcon(ref nint cached, TaskbarInterop.BadgeGlyph glyph)
    {
        if (cached != 0)
        {
            return cached;
        }

        cached = TaskbarInterop.CreateBadgeIcon(glyph);

        if (cached == 0)
        {
            _logger.LogDebug("GDI would not create the {Glyph} badge icon", glyph);
        }

        return cached;
    }
}
