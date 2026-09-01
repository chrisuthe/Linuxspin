using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Sendspin.Player.Threading;
using Sendspin.Player.ViewModels;

namespace Sendspin.Player.Views;

/// <summary>
/// Welcome: this player's name and mode, the server picker or the waiting card, and the
/// manual-address section.
/// </summary>
/// <remarks>
/// The one piece of behaviour here is the broadcasting dot's pulse. It is driven by a
/// <see cref="UiClock"/> at 2 Hz rather than an Avalonia <c>Animation</c>, because on the Wayland
/// head the animation clock spins a core (the clock table in <c>docs/ARCHITECTURE.md</c>), and it
/// runs only while there is something to pulse for: the view is attached, the player is
/// advertising, and nothing is connected — which is when the card is on screen.
/// </remarks>
public sealed partial class WelcomeView : UserControl
{
    /// <summary>The pulse's half-period: the dot alternates between bright and dim at this rate.</summary>
    internal static readonly TimeSpan PulsePeriod = TimeSpan.FromMilliseconds(500);

    /// <summary>The dot's opacity on the dim half of the pulse.</summary>
    internal const double PulseLowOpacity = 0.35;

    private UiClock? _pulse;
    private MainViewModel? _viewModel;

    public WelcomeView()
    {
        InitializeComponent();
    }

    /// <summary>Gets whether the broadcasting dot is pulsing.</summary>
    internal bool IsPulsing => _pulse?.IsRunning == true;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdatePulse();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _pulse = new UiClock(PulsePeriod);
        _pulse.Tick += OnPulseTick;
        UpdatePulse();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_pulse is { } pulse)
        {
            pulse.Tick -= OnPulseTick;
            pulse.Dispose();
            _pulse = null;
        }

        BroadcastDot.Opacity = 1.0;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsConnected))
        {
            UpdatePulse();
        }
    }

    private void UpdatePulse()
    {
        if (_pulse is null)
        {
            return;
        }

        if (_viewModel is { IsAdvertising: true, IsConnected: false })
        {
            _pulse.Start();
        }
        else
        {
            _pulse.Stop();
            BroadcastDot.Opacity = 1.0;
        }
    }

    private void OnPulseTick(object? sender, EventArgs e) =>
        BroadcastDot.Opacity = BroadcastDot.Opacity < 1.0 ? 1.0 : PulseLowOpacity;
}
