using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sendspin.Core.Notifications;

namespace Sendspin.Platform.Windows.Notifications;

/// <summary>
/// Shows desktop notifications through the shell's <c>Shell_NotifyIcon</c> balloon path, which
/// Windows 10 and 11 render as ordinary toasts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this route.</strong> The predecessor used
/// <c>Microsoft.Toolkit.Uwp.Notifications</c>, whose repository is archived and whose successor is
/// formally deprecated. The modern replacement is <c>AppNotificationManager</c> from
/// <c>Microsoft.WindowsAppSDK.Foundation</c>, and it is the richer surface: adaptive layouts,
/// inline artwork, buttons, and entries that persist in the Action Center. It is not used here
/// because of a live blocker — WindowsAppSDK issue 6071, where <c>AppNotificationManager.Register</c>
/// throws for <strong>self-contained unpackaged</strong> applications, which is exactly how this
/// player is published. Taking that route would mean either no notifications on the shipping
/// build or a framework-dependent publish with the Windows App Runtime as an installer
/// prerequisite. <c>Shell_NotifyIcon</c> needs no package, no identity and no runtime, and works
/// self-contained.
/// </para>
/// <para>
/// <strong>What this costs.</strong> A notification-area icon must exist for the balloon to come
/// from, so one is registered for the app's lifetime; Windows 11 hides it by default, which does
/// not affect the notification. Title and body are capped by the shell at 63 and 255 characters.
/// Artwork is <em>not</em> shown: the balloon icon is an <c>HICON</c>, the server sends JPEG or
/// PNG, and converting between them needs an imaging library this backend does not have — so
/// <see cref="NotificationRequest.ArtworkFilePath"/> is ignored and the application icon is used,
/// which <see cref="INotificationService"/> explicitly permits. If the user has turned
/// notifications off for this app, or Focus Assist is on, the shell silently shows nothing and
/// reports success; that is not detectable from here.
/// </para>
/// <para>
/// Filtering by <see cref="NotificationKind"/> happens above this class, in the shared
/// dispatcher. This only renders.
/// </para>
/// </remarks>
public sealed partial class ShellBalloonNotificationService : INotificationService
{
    /// <summary>
    /// Identifies this process's icon within its window. Any constant will do; it only has to
    /// agree between add, modify and delete.
    /// </summary>
    private const uint IconId = 1;

    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;
    private const int NimSetVersion = 0x00000004;

    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NifShowTip = 0x00000080;

    private const uint NiifUser = 0x00000004;
    private const uint NiifLargeIcon = 0x00000020;

    private const uint NotifyIconVersion4 = 4;

    /// <summary>
    /// Shell limits, in characters including the terminator.
    /// </summary>
    private const int TipCapacity = 128;
    private const int InfoCapacity = 256;
    private const int InfoTitleCapacity = 64;

    private const int LoadIconApplication = 32512;

    private readonly Func<nint?> _windowHandleProvider;
    private readonly ILogger<ShellBalloonNotificationService> _logger;
    private readonly Lock _gate = new();

    private nint _windowHandle;
    private nint _smallIcon;
    private nint _largeIcon;
    private bool _iconsAreOurs;
    private bool _registered;
    private bool _disposed;

    /// <param name="windowHandleProvider">
    /// Returns the main window's handle, or null while there is no window. The shell needs a
    /// window to own the notification-area icon.
    /// </param>
    /// <param name="logger">Logger for diagnostics.</param>
    public ShellBalloonNotificationService(Func<nint?> windowHandleProvider, ILogger<ShellBalloonNotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(windowHandleProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _windowHandleProvider = windowHandleProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool IsAvailable
    {
        get { lock (_gate) return _registered; }
    }

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_disposed || _registered)
            {
                return Task.CompletedTask;
            }

            var handle = ResolveWindowHandle();
            if (handle is null)
            {
                _logger.LogWarning(
                    "No window handle is available, so no notification-area icon can be registered; " +
                    "notifications are disabled for this session");
                return Task.CompletedTask;
            }

            _windowHandle = handle.Value;
            LoadApplicationIcons();

            var data = CreateData(NifIcon | NifTip | NifShowTip);
            WriteTip(ref data, "Sendspin Player");

            if (!ShellNotifyIcon(NimAdd, ref data))
            {
                _logger.LogWarning(
                    "The shell refused a notification-area icon (Win32 error {Error}); notifications are " +
                    "disabled for this session",
                    Marshal.GetLastWin32Error());
                ReleaseIcons();
                return Task.CompletedTask;
            }

            // Version 4 behaviour: the shell places balloons itself and honours the modern
            // notification settings rather than the Windows 2000-era ones.
            var version = CreateData(0);
            version.TimeoutOrVersion = NotifyIconVersion4;
            if (!ShellNotifyIcon(NimSetVersion, ref version))
            {
                _logger.LogDebug(
                    "The notification-area icon stayed on legacy behaviour (Win32 error {Error})",
                    Marshal.GetLastWin32Error());
            }

            _registered = true;

            _logger.LogInformation(
                "Notifications use the Shell_NotifyIcon balloon path, which Windows renders as toasts. " +
                "AppNotificationManager is not used: its Register() throws for self-contained unpackaged " +
                "apps (WindowsAppSDK#6071), which is how this player is published");

            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public Task ShowAsync(NotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_registered)
            {
                return Task.CompletedTask;
            }

            var data = CreateData(NifInfo);
            WriteInfo(ref data, request.Title, request.Body);

            // NIIF_USER draws the icon supplied with the notification rather than a stock glyph.
            data.InfoFlags = NiifUser | (_largeIcon != 0 ? NiifLargeIcon : 0);
            data.BalloonIcon = _largeIcon != 0 ? _largeIcon : _smallIcon;

            if (!ShellNotifyIcon(NimModify, ref data))
            {
                _logger.LogWarning(
                    "Notification {Kind} was refused by the shell (Win32 error {Error})",
                    request.Kind, Marshal.GetLastWin32Error());
            }

            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public Task WithdrawAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_registered)
            {
                return Task.CompletedTask;
            }

            // An empty body with NIF_INFO is the documented way to take a balloon back down.
            var data = CreateData(NifInfo);

            if (!ShellNotifyIcon(NimModify, ref data))
            {
                _logger.LogDebug(
                    "The current notification could not be withdrawn (Win32 error {Error})",
                    Marshal.GetLastWin32Error());
            }

            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;

            if (_registered)
            {
                var data = CreateData(0);
                if (!ShellNotifyIcon(NimDelete, ref data))
                {
                    _logger.LogDebug(
                        "The notification-area icon could not be removed (Win32 error {Error})",
                        Marshal.GetLastWin32Error());
                }

                _registered = false;
            }

            ReleaseIcons();
        }

        return ValueTask.CompletedTask;
    }

    private static unsafe void WriteFixed(char* destination, int capacity, string? value)
    {
        var span = new Span<char>(destination, capacity);
        span.Clear();

        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        // The shell truncates rather than rejecting, but it will read past a missing terminator.
        var length = Math.Min(value.Length, capacity - 1);
        value.AsSpan(0, length).CopyTo(span);
    }

    private static unsafe void WriteTip(ref NotifyIconData data, string tip)
    {
        fixed (char* destination = data.Tip)
        {
            WriteFixed(destination, TipCapacity, tip);
        }
    }

    private static unsafe void WriteInfo(ref NotifyIconData data, string title, string? body)
    {
        fixed (char* destination = data.InfoTitle)
        {
            WriteFixed(destination, InfoTitleCapacity, title);
        }

        fixed (char* destination = data.Info)
        {
            // A balloon with an empty body is treated as a withdrawal, so a title-only
            // notification needs something in the body to survive.
            WriteFixed(destination, InfoCapacity, string.IsNullOrEmpty(body) ? " " : body);
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIcon(int message, ref NotifyIconData data);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    private static partial nint LoadIcon(nint instance, nint iconName);

    [LibraryImport("user32.dll", EntryPoint = "DestroyIcon")]
    private static partial int DestroyIcon(nint icon);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconEx(string file, int iconIndex, out nint largeIcon, out nint smallIcon, uint iconCount);

    private NotifyIconData CreateData(uint flags) => new()
    {
        CbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        Hwnd = _windowHandle,
        Id = IconId,
        Flags = flags,
        Icon = _smallIcon
    };

    private nint? ResolveWindowHandle()
    {
        try
        {
            var handle = _windowHandleProvider();
            return handle is null || handle.Value == 0 ? null : handle;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "The main window handle could not be read for notifications");
            return null;
        }
    }

    /// <summary>
    /// Takes the icons out of the running executable, falling back to the stock application icon.
    /// </summary>
    private void LoadApplicationIcons()
    {
        var executablePath = Environment.ProcessPath;

        if (!string.IsNullOrEmpty(executablePath))
        {
            var extracted = ExtractIconEx(executablePath, 0, out var large, out var small, 1);

            if (extracted > 0 && (large != 0 || small != 0))
            {
                _largeIcon = large;
                _smallIcon = small != 0 ? small : large;
                _iconsAreOurs = true;
                return;
            }
        }

        // A shared system icon: it must not be destroyed, hence the ownership flag.
        _smallIcon = LoadIcon(0, LoadIconApplication);
        _largeIcon = 0;
        _iconsAreOurs = false;

        if (_smallIcon == 0)
        {
            _logger.LogDebug("No application icon could be loaded for notifications");
        }
    }

    private void ReleaseIcons()
    {
        if (_iconsAreOurs)
        {
            if (_smallIcon != 0)
            {
                DestroyIcon(_smallIcon);
            }

            if (_largeIcon != 0 && _largeIcon != _smallIcon)
            {
                DestroyIcon(_largeIcon);
            }
        }

        _smallIcon = 0;
        _largeIcon = 0;
        _iconsAreOurs = false;
    }

    /// <summary>
    /// <c>NOTIFYICONDATAW</c>. Blittable, with inline character buffers rather than marshalled
    /// strings, so the source-generated interop needs no custom marshaller.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NotifyIconData
    {
        public uint CbSize;
        public nint Hwnd;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[TipCapacity];
        public uint State;
        public uint StateMask;
        public fixed char Info[InfoCapacity];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[InfoTitleCapacity];
        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIcon;
    }
}
