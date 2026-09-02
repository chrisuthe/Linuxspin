using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Input;

namespace Sendspin.Player.Controls;

/// <summary>
/// A slider with a step button either side and an editable value beside it, for a setting that
/// is a number with a unit: the settings card's static delay and latency offset.
/// </summary>
/// <remarks>
/// <para>
/// The buttons move <see cref="Value"/> by <see cref="Step"/> and clamp it to
/// [<see cref="Minimum"/>, <see cref="Maximum"/>]; a typed value is clamped the same way when
/// it is committed with Enter or by leaving the box. <see cref="Value"/> itself is never
/// coerced, so a persisted value outside the range is shown as it is rather than rewritten on
/// load; the first step brings it inside.
/// </para>
/// <para>
/// The slider is deliberately not bound to <see cref="Value"/> both ways. A slider clamps its
/// own value to its range, and a two-way binding would write that clamped value back — so a
/// value that arrived before the range did (attribute order in the XAML that uses this control)
/// would be silently rewritten into the setting. Instead the code-behind pushes Value into the
/// slider, ignoring what the slider says while it does, and takes the slider's value only when
/// the user moves it.
/// </para>
/// </remarks>
public sealed partial class StepperRow : UserControl
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<StepperRow, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<StepperRow, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<StepperRow, double>(nameof(Maximum), 100.0);

    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<StepperRow, double>(nameof(Step), 10.0);

    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<StepperRow, string>(nameof(Unit), "ms");

    private bool _isPushingToSlider;

    public StepperRow()
    {
        InitializeComponent();

        DecrementCommand = new RelayCommand(Decrement);
        IncrementCommand = new RelayCommand(Increment);
        DecrementButton.Command = DecrementCommand;
        IncrementButton.Command = IncrementCommand;

        ValueSlider.ValueChanged += OnSliderValueChanged;
        ValueBox.KeyDown += OnValueBoxKeyDown;
        ValueBox.LostFocus += OnValueBoxLostFocus;

        SyncRange();
        SyncValue();
        UnitText.Text = Unit;
    }

    /// <summary>Gets or sets the value. Two-way by default, like a slider's.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Gets or sets the lowest value the buttons and the box will produce.</summary>
    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>Gets or sets the highest value the buttons and the box will produce.</summary>
    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>Gets or sets how far one button press moves the value.</summary>
    public double Step
    {
        get => GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    /// <summary>Gets or sets the unit shown after the value.</summary>
    public string Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    /// <summary>Gets the command behind the step-down button.</summary>
    public IRelayCommand DecrementCommand { get; }

    /// <summary>Gets the command behind the step-up button.</summary>
    public IRelayCommand IncrementCommand { get; }

    /// <summary>Moves the value down one step, no lower than <see cref="Minimum"/>.</summary>
    public void Decrement() => Value = Clamp(Value - Step);

    /// <summary>Moves the value up one step, no higher than <see cref="Maximum"/>.</summary>
    public void Increment() => Value = Clamp(Value + Step);

    /// <summary>
    /// Takes what is typed in the box as the value, clamped to the range; text that is not a
    /// number puts the current value back.
    /// </summary>
    public void CommitTypedValue()
    {
        if (double.TryParse(ValueBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var typed))
        {
            Value = Clamp(typed);
        }

        SyncValue();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (ValueSlider is null)
        {
            // Raised for the defaults from the base constructor, before the XAML has loaded.
            return;
        }

        if (change.Property == ValueProperty)
        {
            SyncValue();
        }
        else if (change.Property == MinimumProperty || change.Property == MaximumProperty)
        {
            SyncRange();
            SyncValue();
        }
        else if (change.Property == StepProperty)
        {
            ValueSlider.TickFrequency = Step;
        }
        else if (change.Property == UnitProperty)
        {
            UnitText.Text = Unit;
        }
    }

    private double Clamp(double value) =>
        Maximum < Minimum ? value : Math.Clamp(value, Minimum, Maximum);

    private void SyncRange()
    {
        // A range change coerces the slider's own value, and that is not the user moving it.
        _isPushingToSlider = true;
        try
        {
            ValueSlider.Minimum = Minimum;
            ValueSlider.Maximum = Maximum;
            ValueSlider.TickFrequency = Step;
        }
        finally
        {
            _isPushingToSlider = false;
        }
    }

    private void SyncValue()
    {
        _isPushingToSlider = true;
        try
        {
            ValueSlider.Value = Value;
        }
        finally
        {
            _isPushingToSlider = false;
        }

        ValueBox.Text = Value.ToString("0", CultureInfo.CurrentCulture);
        DecrementButton.IsEnabled = Value > Minimum;
        IncrementButton.IsEnabled = Value < Maximum;
    }

    /// <remarks>
    /// Rounded to a whole unit: the box shows whole units, and a drag that stored 123.45 would
    /// have the row holding a number it does not show.
    /// </remarks>
    private void OnSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isPushingToSlider)
        {
            Value = Math.Round(e.NewValue);
        }
    }

    private void OnValueBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitTypedValue();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SyncValue();
            e.Handled = true;
        }
    }

    private void OnValueBoxLostFocus(object? sender, RoutedEventArgs e) => CommitTypedValue();
}
