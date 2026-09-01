using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Sendspin.Player.Behaviors;

/// <summary>
/// Stops a closed <see cref="ComboBox"/> from changing its selection when the wheel is scrolled
/// over it, while leaving the wheel free to scroll whatever is behind the control.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia's <see cref="ComboBox"/> overrides <c>OnPointerWheelChanged</c>: with the dropdown
/// closed and the control focused, one wheel notch steps <c>SelectedIndex</c> and marks the event
/// handled. In a settings panel that is a defect rather than a feature — every one of these
/// controls writes straight through to <c>settings.json</c>, so an incidental scroll past the
/// panel silently rewrites persisted state. The codec picker changes what is advertised in
/// <c>client/hello</c>; the device picker reopens the audio device mid-playback.
/// </para>
/// <para>
/// The guard runs in the <see cref="RoutingStrategies.Tunnel"/> phase, which is the only place it
/// can win: the ComboBox's own handler is a bubble-phase class handler, so a bubble handler here
/// would run after the selection had already moved. Marking the event handled on the way down
/// pre-empts it.
/// </para>
/// <para>
/// Marking it handled also suppresses the ancestor <see cref="ScrollViewer"/>, which would leave
/// the wheel dead over these controls, so an equivalent event is re-raised on the ComboBox's
/// visual parent. The parent is below the scroll viewer's presenter in the tree, so the fresh
/// event bubbles up into it and the panel scrolls exactly as it does over a label. Re-raising the
/// original args is not an option — they are already marked handled.
/// </para>
/// <para>
/// With the dropdown <em>open</em> the guard defers completely: the wheel then belongs to the
/// popup's list, which is the behaviour a user expects from an open dropdown.
/// </para>
/// </remarks>
public static class ComboBoxWheelGuard
{
    /// <summary>
    /// Set to <c>true</c> on a <see cref="ComboBox"/> to install the guard. Applied through a
    /// style rather than per control, so a ComboBox added later is covered by default.
    /// </summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(ComboBoxWheelGuard));

    static ComboBoxWheelGuard()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>(OnIsEnabledChanged);
    }

    /// <summary>Gets whether the guard is installed on <paramref name="control"/>.</summary>
    public static bool GetIsEnabled(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(IsEnabledProperty);
    }

    /// <summary>Installs or removes the guard on <paramref name="control"/>.</summary>
    public static void SetIsEnabled(Control control, bool value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(IsEnabledProperty, value);
    }

    private static void OnIsEnabledChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        // Styles apply and un-apply, so removing first keeps a re-application from stacking a
        // second handler on the same control.
        control.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged);

        if (args.GetNewValue<bool>())
        {
            control.AddHandler(
                InputElement.PointerWheelChangedEvent,
                OnPointerWheelChanged,
                RoutingStrategies.Tunnel);
        }
    }

    private static void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.IsDropDownOpen)
        {
            return;
        }

        e.Handled = true;

        if (comboBox.GetVisualParent() is not Interactive parent ||
            TopLevel.GetTopLevel(comboBox) is not { } topLevel)
        {
            return;
        }

        parent.RaiseEvent(new PointerWheelEventArgs(
            parent,
            e.Pointer,
            topLevel,
            e.GetPosition(topLevel),
            e.Timestamp,
            e.Properties,
            e.KeyModifiers,
            e.Delta)
        {
            RoutedEvent = InputElement.PointerWheelChangedEvent
        });
    }
}
