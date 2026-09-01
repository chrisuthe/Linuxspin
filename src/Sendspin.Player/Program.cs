using Avalonia;

namespace Sendspin.Player;

/// <summary>
/// Entry point.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Starts the application.
    /// </summary>
    /// <remarks>
    /// <see cref="PlatformSelection.PreInitializeHost"/> runs before anything else, including
    /// before <see cref="AppBuilder.Configure{TApplication}()"/>: on macOS it has to, and
    /// putting it first unconditionally means the ordering cannot be broken by a later edit on
    /// a platform where it happens not to matter.
    /// </remarks>
    [STAThread]
    public static int Main(string[] args)
    {
        PlatformSelection.PreInitializeHost();

        var singleInstance = SingleInstanceGuard.TryAcquire();

        if (!singleInstance.IsPrimary && !singleInstance.AllowWithoutGuard)
        {
            // A second copy of an audio endpoint would advertise a duplicate client_id and
            // contend for the output device. Ask the running instance to show itself, then exit
            // quietly: the user launched the app, and the app appearing is the right outcome.
            singleInstance.SignalPrimaryToShow();
            singleInstance.Dispose();
            return 0;
        }

        try
        {
            App.SingleInstance = singleInstance;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            singleInstance.Dispose();
        }
    }

    /// <summary>
    /// Builds the Avalonia application.
    /// </summary>
    /// <remarks>
    /// Also called by the XAML designer, so it must not do application initialisation. The
    /// windowing backend is chosen per platform — see
    /// <see cref="PlatformSelection.ConfigureWindowing"/>, which is where the Linux head's
    /// Wayland-by-default choice and its X11 escape hatch live.
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp() =>
        PlatformSelection.ConfigureWindowing(AppBuilder.Configure<App>())
            .WithInterFont()
            .LogToTrace();
}
