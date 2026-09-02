using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Sendspin.Player.Controls;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins the stepper row: a step per click, clamped to the range, a typed value taken on Enter
/// or on leaving the box, and a value that is never rewritten by the slider's own clamping.
/// </summary>
[Collection(HeadlessCollection.Name)]
public sealed class StepperRowTests(HeadlessSession headless)
{
    [Fact]
    public void TheButtons_StepByTheStep() => headless.Run(() =>
    {
        var (_, row) = Build(0, 2000, 100);

        Increment(row);
        Assert.Equal(110, row.Value);

        Decrement(row);
        Decrement(row);
        Assert.Equal(90, row.Value);

        Assert.Equal("90", Box(row).Text);
        Assert.Equal(90, Slider(row).Value);
    });

    [Fact]
    public void TheSteps_ClampToTheRange() => headless.Run(() =>
    {
        var (_, row) = Build(0, 2000, 1995);

        Increment(row);
        Assert.Equal(2000, row.Value);
        Assert.False(Button(row, "IncrementButton").IsEnabled);

        Increment(row);
        Assert.Equal(2000, row.Value);

        row.Value = 5;
        Dispatcher.UIThread.RunJobs();

        Decrement(row);
        Assert.Equal(0, row.Value);
        Assert.False(Button(row, "DecrementButton").IsEnabled);
        Assert.True(Button(row, "IncrementButton").IsEnabled);
    });

    [Fact]
    public void ANegativeRange_StepsBelowZero() => headless.Run(() =>
    {
        var (_, row) = Build(-200, 500, 0);

        Decrement(row);
        Assert.Equal(-10, row.Value);
        Assert.Equal("-10", Box(row).Text);
    });

    [Fact]
    public void ATypedValue_IsTakenOnEnter() => headless.Run(() =>
    {
        var (window, row) = Build(0, 2000, 100);
        var box = Box(row);

        box.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsFocused);

        box.Text = "150";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(150, row.Value);
        Assert.Equal(150, Slider(row).Value);
    });

    [Fact]
    public void ATypedValue_IsClampedAndTakenOnLeavingTheBox() => headless.Run(() =>
    {
        var (_, row, other) = BuildWithNeighbour(0, 2000, 100);
        var box = Box(row);

        box.Focus();
        Dispatcher.UIThread.RunJobs();

        box.Text = "9999";
        other.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2000, row.Value);
        Assert.Equal("2000", box.Text);
    });

    [Fact]
    public void TextThatIsNotANumber_PutsTheValueBack() => headless.Run(() =>
    {
        var (window, row) = Build(0, 2000, 100);
        var box = Box(row);

        box.Focus();
        Dispatcher.UIThread.RunJobs();

        box.Text = "lots";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(100, row.Value);
        Assert.Equal("100", box.Text);
    });

    [Fact]
    public void TheSlider_FollowsTheValueAndMovesIt() => headless.Run(() =>
    {
        var (_, row) = Build(0, 2000, 100);
        var slider = Slider(row);

        Assert.Equal(0, slider.Minimum);
        Assert.Equal(2000, slider.Maximum);

        row.Value = 300;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(300, slider.Value);

        // What a drag does: the slider's value changes and the row takes it.
        slider.Value = 250;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(250, row.Value);
        Assert.Equal("250", Box(row).Text);
    });

    /// <remarks>
    /// The case the control exists to survive: a value that arrives before its range. A slider
    /// bound both ways would have clamped 500 to its default 100 and written that back.
    /// </remarks>
    [Fact]
    public void AValueThatArrivesBeforeTheRange_IsNotRewritten() => headless.Run(() =>
    {
        PlayerResources.Merge();

        var row = new StepperRow { Value = 500 };
        var window = new Window { Width = 400, Height = 100, Content = row };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(500, row.Value);
        Assert.Equal(100, Slider(row).Value);
        Assert.Equal("500", Box(row).Text);

        row.Maximum = 2000;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(500, row.Value);
        Assert.Equal(500, Slider(row).Value);

        window.Close();
    });

    /// <remarks>
    /// A range change coerces the slider's value, and that coercion is not a move by the user.
    /// </remarks>
    [Fact]
    public void ARangeChange_DoesNotRewriteTheValue() => headless.Run(() =>
    {
        var (_, row) = Build(0, 2000, 50);

        row.Minimum = 100;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(50, row.Value);
        Assert.Equal(100, Slider(row).Value);
        Assert.Equal("50", Box(row).Text);
    });

    [Fact]
    public void ADrag_LandsOnAWholeUnit() => headless.Run(() =>
    {
        var (_, row) = Build(0, 2000, 100);

        Slider(row).Value = 123.45;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(123, row.Value);
        Assert.Equal("123", Box(row).Text);
    });

    [Fact]
    public void TheUnit_IsShownAfterTheValue() => headless.Run(() =>
    {
        var (_, row) = Build(0, 2000, 100);

        var unit = row.FindControl<TextBlock>("UnitText")!;
        Assert.Equal("ms", unit.Text);
        Assert.True(Box(row).Bounds.Right <= unit.Bounds.Left);

        row.Unit = "s";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("s", unit.Text);
    });

    private static (Window Window, StepperRow Row) Build(double minimum, double maximum, double value)
    {
        PlayerResources.Merge();

        var row = new StepperRow { Minimum = minimum, Maximum = maximum, Step = 10, Unit = "ms", Value = value };
        var window = new Window { Width = 400, Height = 100, Content = row };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (window, row);
    }

    private static (Window Window, StepperRow Row, TextBox Other) BuildWithNeighbour(double minimum, double maximum, double value)
    {
        PlayerResources.Merge();

        var row = new StepperRow { Minimum = minimum, Maximum = maximum, Step = 10, Unit = "ms", Value = value };
        var other = new TextBox();
        var panel = new StackPanel();
        panel.Children.Add(row);
        panel.Children.Add(other);

        var window = new Window { Width = 400, Height = 200, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (window, row, other);
    }

    private static void Increment(StepperRow row)
    {
        Button(row, "IncrementButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Decrement(StepperRow row)
    {
        Button(row, "DecrementButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    private static Button Button(StepperRow row, string name) => row.FindControl<Button>(name)!;

    private static Slider Slider(StepperRow row) => row.FindControl<Slider>("ValueSlider")!;

    private static TextBox Box(StepperRow row) => row.FindControl<TextBox>("ValueBox")!;
}
