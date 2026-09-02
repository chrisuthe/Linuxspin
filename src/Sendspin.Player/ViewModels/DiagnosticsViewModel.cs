using CommunityToolkit.Mvvm.ComponentModel;
using Sendspin.Core.Audio;
using Sendspin.Core.Diagnostics;
using Sendspin.Player.Threading;

namespace Sendspin.Player.ViewModels;

/// <summary>
/// The "stats for nerds" view: the numbers needed to tell a real timing problem from a
/// correction artefact.
/// </summary>
/// <remarks>
/// Polls rather than subscribing, because the underlying values change on the audio path and
/// raising a UI notification per change would push work onto it. One refresh per half second is
/// faster than anyone can read and cheap enough to be invisible.
/// </remarks>
public sealed partial class DiagnosticsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

    private readonly IDiagnosticsProvider _provider;
    private readonly SyncCorrectionPolicy _policy;
    private readonly UiClock _clock = new(RefreshInterval);

    private bool _isDisposed;

    [ObservableProperty]
    private PlayerDiagnosticsSnapshot _snapshot = PlayerDiagnosticsSnapshot.Empty;

    [ObservableProperty]
    private bool _isVisible;

    public DiagnosticsViewModel(IDiagnosticsProvider provider, SyncCorrectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(policy);

        _provider = provider;
        _policy = policy;

        _clock.Tick += (_, _) => Refresh();
    }

    /// <summary>
    /// Gets the smoothed sync error in milliseconds, the headline number.
    /// </summary>
    public double SyncErrorMs => Snapshot.SmoothedSyncErrorMicroseconds / 1000.0;

    /// <summary>
    /// Gets the band the current error falls in, which explains what the correction machinery is
    /// doing and why.
    /// </summary>
    public string CorrectionBand => _policy.Classify(Snapshot.SmoothedSyncErrorMicroseconds) switch
    {
        SyncCorrectionBand.Deadband => "within deadband",
        SyncCorrectionBand.RateAdjust => "rate adjust",
        SyncCorrectionBand.DropInsert => "drop/insert",
        SyncCorrectionBand.HardSync => "hard sync",
        _ => "re-anchor"
    };

    /// <summary>
    /// Gets the rate deviation in parts per million, which is the figure to compare against the
    /// ±500 ppm ceiling.
    /// </summary>
    public double PlaybackRatePpm => (Snapshot.PlaybackRate - 1.0) * 1_000_000.0;

    /// <summary>
    /// Gets whether the timing source is the audio hardware clock.
    /// </summary>
    /// <remarks>
    /// The single most diagnostic field in the view. Anything else means the platform backend is
    /// not supplying a hardware clock, and every sync figure beside it is resting on the OS
    /// timer instead.
    /// </remarks>
    public bool IsUsingAudioClock =>
        string.Equals(Snapshot.TimingSource, "audio-clock", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the negotiated stream format as one line.
    /// </summary>
    public string FormatSummary
    {
        get
        {
            if (Snapshot.Codec is null || Snapshot.SampleRate == 0)
            {
                return "—";
            }

            var depth = Snapshot.BitDepth is { } bits ? $"/{bits}-bit" : string.Empty;
            return $"{Snapshot.Codec} {Snapshot.SampleRate} Hz{depth}, {Snapshot.Channels} ch";
        }
    }

    /// <summary>
    /// Gets the total output latency including the manual offset.
    /// </summary>
    public double TotalLatencyMs => Snapshot.OutputLatencyMs + Snapshot.ManualLatencyOffsetMs;

    /// <summary>Gets whether the refresh clock is ticking, which it should be only while the window is open.</summary>
    internal bool IsRefreshing => _clock.IsRunning;

    /// <summary>
    /// Starts or stops polling to match <paramref name="visible"/>.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (_isDisposed)
        {
            // Startup reopens the window through a dispatcher invoke that can land after shutdown
            // has disposed the clock; there is nothing left to poll for.
            return;
        }

        IsVisible = visible;

        if (visible)
        {
            Refresh();
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _isDisposed = true;
        _clock.Dispose();
    }

    /// <summary>
    /// Notifies the derived properties, which are all computed from
    /// <see cref="Snapshot"/> and so do not raise their own changes.
    /// </summary>
    partial void OnSnapshotChanged(PlayerDiagnosticsSnapshot value)
    {
        OnPropertyChanged(nameof(SyncErrorMs));
        OnPropertyChanged(nameof(CorrectionBand));
        OnPropertyChanged(nameof(PlaybackRatePpm));
        OnPropertyChanged(nameof(IsUsingAudioClock));
        OnPropertyChanged(nameof(FormatSummary));
        OnPropertyChanged(nameof(TotalLatencyMs));
    }

    private void Refresh() => Snapshot = _provider.Capture();
}
