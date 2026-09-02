using Avalonia;
using Avalonia.Media;

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
    /// <para>
    /// Also called by the XAML designer, so it must not do application initialisation. The
    /// windowing backend is chosen per platform — see
    /// <see cref="PlatformSelection.ConfigureWindowing"/>, which is where the Linux head's
    /// Wayland-by-default choice and its X11 escape hatch live.
    /// </para>
    /// <para>
    /// <strong>The UI font is the platform's, with Inter as glyph fallback.</strong>
    /// <c>WithInterFont()</c> only registers the embedded collection; what made Inter the face of
    /// every control was Fluent's <c>ContentControlThemeFontFamily</c> resource naming it first,
    /// and App.axaml overrides that resource to <c>$Default</c>. <c>DefaultFamilyName</c> is what
    /// <c>$Default</c> resolves to: on Linux it is the desktop's interface font read from the
    /// Settings portal (fontconfig's own default is DejaVu Sans inside the Flatpak, which is not
    /// the desktop's font); on Windows it is left null, which means "ask the platform", and that
    /// answer is measured: plain Segoe UI, the face the WPF reference app uses. On macOS the
    /// platform's answer is Helvetica, so the head names the system face,
    /// <c>.AppleSystemUIFont</c>, guarded by a resolve check because an unresolvable name kills
    /// the process before a window appears — see
    /// <see cref="PlatformSelection.ReadDesktopFontFamily"/> on each head. All three were
    /// measured in the "System font" section of docs/ARCHITECTURE.md; macOS also confirmed that
    /// the embedded <c>fonts:Inter#Inter</c> is the only way Inter resolves on a box without it
    /// installed, which is why <c>WithInterFont()</c> stays.
    /// </para>
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp() =>
        PlatformSelection.ConfigureWindowing(AppBuilder.Configure<App>())
            .WithInterFont()
            .With(new FontManagerOptions
            {
                DefaultFamilyName = PlatformSelection.ReadDesktopFontFamily(),
                FontFallbacks = [new FontFallback { FontFamily = new FontFamily("fonts:Inter#Inter") }],
            })
            .LogToTrace();
}
