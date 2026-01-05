using System;
using Avalonia;

namespace SendspinClient.Linux;

/// <summary>
/// Entry point for the Sendspin Linux client.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Application entry point. Initialization code must not use any Avalonia,
    /// third-party APIs or any SynchronizationContext-reliant code before
    /// AppMain is called.
    /// </summary>
    /// <param name="args">Command line arguments.</param>
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Builds the Avalonia application with platform-specific configuration.
    /// </summary>
    /// <returns>The configured <see cref="AppBuilder"/> instance.</returns>
    /// <remarks>
    /// This method is called by the visual designer and the entry point.
    /// Do not perform any application initialization here as it may be called
    /// multiple times by the designer.
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
