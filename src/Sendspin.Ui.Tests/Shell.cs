using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Sendspin.Core.Configuration;
using Sendspin.Player.ViewModels;
using Sendspin.Player.Views;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>A shown main window over a real view model, closed and disposed with the test.</summary>
internal sealed class Shell : IDisposable
{
    private Shell(MainWindow window, ShellGraph graph)
    {
        Window = window;
        ViewModel = graph.ViewModel;
        Settings = graph.Settings;
    }

    public MainWindow Window { get; }

    public MainViewModel ViewModel { get; }

    public SettingsService Settings { get; }

    /// <param name="configure">Edits the settings the shell starts from; see <see cref="ShellViewModels.CreateMain"/>.</param>
    public static Shell Show(Action<PlayerSettings>? configure = null)
    {
        PlayerResources.Merge();

        var graph = ShellViewModels.CreateMain(configure);
        var window = new MainWindow { DataContext = graph.ViewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return new Shell(window, graph);
    }

    /// <summary>A control named in the main window itself.</summary>
    public T Find<T>(string name)
        where T : Control
    {
        var control = Window.FindControl<T>(name);
        Assert.True(control is not null, $"no {typeof(T).Name} named {name}");
        return control!;
    }

    /// <summary>A control named inside one of the window's views, which has its own name scope.</summary>
    public T FindIn<T>(Control view, string name)
        where T : Control
    {
        var control = view.FindControl<T>(name);
        Assert.True(control is not null, $"no {typeof(T).Name} named {name} in {view.GetType().Name}");
        return control!;
    }

    public T Resolve<T>(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, Window.ActualThemeVariant, out var value), key);
        return Assert.IsAssignableFrom<T>(value);
    }

    /// <summary>The top of a control in <paramref name="root"/>'s coordinates, for "above" and "below".</summary>
    public static double TopIn(Visual control, Visual root) =>
        control.TranslatePoint(new Point(0, 0), root)?.Y ?? double.NaN;

    public void Dispose()
    {
        Window.Close();
        Dispatcher.UIThread.RunJobs();
        ViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
