using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Sendspin.Player.ViewModels;

namespace Sendspin.Player;

/// <summary>
/// The tray / status-area icon and its menu.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The tooltip is always set, and that is load-bearing rather than cosmetic.</strong>
/// Avalonia's <c>SetTitleAndTooltip</c> early-outs when the tooltip is null, and in doing so
/// ships the StatusNotifierItem with an empty <c>Category</c>. <c>Category</c> is mandatory in
/// the specification, so the host discards the item and the icon silently never appears. This is
/// the source-level cause of Avalonia#16650, where reporters found that adding any tooltip fixed
/// it.
/// </para>
/// <para>
/// <strong>Per-platform reality, which the UI must not pretend away.</strong>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <em>Linux/KDE</em> works: Avalonia's implementation is real StatusNotifierItem over D-Bus.
/// </description></item>
/// <item><description>
/// <em>Linux/GNOME</em> needs the AppIndicator extension on every version in range. GNOME Shell
/// has no <c>StatusNotifierWatcher</c>, the "re-introduce a system tray" issue was closed as out
/// of scope the day it was opened, and no tray portal exists. Avalonia's X11 fallback is a
/// 47-line stub that logs "not implemented", and it is not even reached, because
/// <c>IsActive</c> goes true as soon as a session bus exists — before any watcher check. So on
/// vanilla GNOME the icon simply does not appear, with no error. The extension also owns the bus
/// name itself, so it vanishes on every shell restart.
/// </description></item>
/// <item><description>
/// <em>Windows</em> hides notification-area icons by default and Microsoft documents that this
/// cannot be controlled programmatically, so the tray is not a dependable transport surface
/// there. The Windows backend carries a taskbar overlay badge for that reason.
/// </description></item>
/// </list>
/// <para>
/// Two Avalonia defects also constrain the menu: <c>NativeMenuItem.IsChecked</c> does not work on
/// Windows (#8751), so mute, shuffle and repeat cannot carry checkmarks, and <c>NativeMenu.Opening</c>
/// never fires (#8076), so the menu cannot be rebuilt to show the current track. The menu is
/// therefore static, with state shown in labels that are updated on change instead: Play / Pause
/// and Mute / Unmute follow the view model, and the volume is a disabled readout.
/// </para>
/// <para>
/// The items are the Sendspin for Windows tray menu's, in its order, under this app's status
/// line. Switch Group is here whether or not the footer shows its button: the menu is where a
/// hidden button's function still lives.
/// </para>
/// </remarks>
public sealed class TrayIconController
{
    private readonly ILogger<TrayIconController> _logger;
    private readonly NativeMenuItem _statusItem;
    private readonly NativeMenuItem _playPauseItem;
    private readonly NativeMenuItem _nextItem;
    private readonly NativeMenuItem _previousItem;
    private readonly NativeMenuItem _switchGroupItem;
    private readonly NativeMenuItem _muteItem;
    private readonly NativeMenuItem _volumeItem;
    private readonly NativeMenuItem _showItem;
    private readonly NativeMenuItem _quitItem;

    private TrayIcon? _icon;
    private MainViewModel? _viewModel;

    public TrayIconController(ILogger<TrayIconController> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        _statusItem = new NativeMenuItem("Not connected") { IsEnabled = false };
        _playPauseItem = new NativeMenuItem("Play");
        _nextItem = new NativeMenuItem("Next");
        _previousItem = new NativeMenuItem("Previous");
        _switchGroupItem = new NativeMenuItem("Switch Group");
        _muteItem = new NativeMenuItem("Mute");
        _volumeItem = new NativeMenuItem("Volume: 100 %") { IsEnabled = false };
        _showItem = new NativeMenuItem("Show Sendspin Player");
        _quitItem = new NativeMenuItem("Quit");

        Menu = new NativeMenu
        {
            _statusItem,
            new NativeMenuItemSeparator(),
            _playPauseItem,
            _nextItem,
            _previousItem,
            _switchGroupItem,
            new NativeMenuItemSeparator(),
            _muteItem,
            _volumeItem,
            new NativeMenuItemSeparator(),
            _showItem,
            _quitItem,
        };
    }

    /// <summary>
    /// The menu, built once; the icon shows it and the tests read it.
    /// </summary>
    internal NativeMenu Menu { get; }

    /// <summary>
    /// Creates the icon and binds it to the view model.
    /// </summary>
    public void Attach(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        if (_icon is not null)
        {
            return;
        }

        _viewModel = viewModel;

        // Commands rather than click handlers where the view model has one: NativeMenuItem
        // follows CanExecute into IsEnabled, so every transport item greys out together while
        // there is no connection.
        _playPauseItem.Command = viewModel.PlayPauseCommand;
        _nextItem.Command = viewModel.NextCommand;
        _previousItem.Command = viewModel.PreviousCommand;
        _switchGroupItem.Command = viewModel.SwitchGroupCommand;
        _muteItem.Command = viewModel.ToggleMuteCommand;
        _showItem.Click += OnShow;
        _quitItem.Click += OnQuit;

        _icon = new TrayIcon
        {
            // Never null. See the class remarks: a null tooltip ships a mandatory-empty
            // Category and the icon silently never appears.
            ToolTipText = "Sendspin Player",
            Icon = LoadIcon(),
            Menu = Menu,
            IsVisible = true
        };

        _icon.Clicked += OnShow;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateLabels();

        _logger.LogInformation("Tray icon created");
    }

    /// <summary>
    /// Removes the icon. Called during shutdown, before services are disposed.
    /// </summary>
    public void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }

        if (_icon is null)
        {
            return;
        }

        _icon.Clicked -= OnShow;
        _showItem.Click -= OnShow;
        _quitItem.Click -= OnQuit;
        _playPauseItem.Command = null;
        _nextItem.Command = null;
        _previousItem.Command = null;
        _switchGroupItem.Command = null;
        _muteItem.Command = null;

        _icon.IsVisible = false;
        _icon.Dispose();
        _icon = null;
    }

    /// <summary>
    /// Loads the tray icon from application resources.
    /// </summary>
    /// <remarks>
    /// Returns null when the asset is missing, which leaves Avalonia to use its default rather
    /// than throwing: a missing icon should not stop the app starting.
    /// </remarks>
    private WindowIcon? LoadIcon()
    {
        var uri = new Uri("avares://Sendspin.Player/Assets/sendspin.png");

        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            return new WindowIcon(stream);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "Tray icon asset {Uri} is missing", uri);
            return null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsPlaying)
            or nameof(MainViewModel.State)
            or nameof(MainViewModel.ConnectionStatus)
            or nameof(MainViewModel.IsMuted)
            or nameof(MainViewModel.Volume))
        {
            UpdateLabels();
        }
    }

    /// <summary>
    /// Refreshes the menu labels, which is how state is shown given that checkmarks and
    /// menu-opening rebuilds are both unavailable.
    /// </summary>
    private void UpdateLabels()
    {
        if (_viewModel is null)
        {
            return;
        }

        _playPauseItem.Header = _viewModel.PlayPauseLabel;
        _muteItem.Header = _viewModel.IsMuted ? "Unmute" : "Mute";
        _volumeItem.Header = $"Volume: {_viewModel.Volume} %";

        var title = _viewModel.State.Title;
        _statusItem.Header = title is null
            ? _viewModel.ConnectionStatus
            : $"{title} — {_viewModel.State.Artist ?? "Unknown artist"}";
    }

    private void OnShow(object? sender, EventArgs e) => (Application.Current as App)?.ShowMainWindow();

    private void OnQuit(object? sender, EventArgs e) => (Application.Current as App)?.RequestShutdown();
}
