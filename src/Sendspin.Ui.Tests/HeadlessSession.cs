using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Sendspin.Ui.Tests;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Sendspin.Ui.Tests;

/// <summary>
/// The Avalonia application the headless tests run under.
/// </summary>
/// <remarks>
/// Deliberately not <c>Sendspin.Player.App</c>: that one builds the whole service graph on
/// framework-initialization, including the audio pipeline and the tray icon. These tests need a
/// styled visual tree and nothing else.
/// </remarks>
public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

/// <summary>Entry point <see cref="AvaloniaTestApplicationAttribute"/> points the session at.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>
/// Runs a test body on the headless Avalonia dispatcher thread.
/// </summary>
/// <remarks>
/// Every control, event and dispatcher call has to happen on the one UI thread, and xunit hands
/// each test whatever thread it likes. This is what <c>Avalonia.Headless.XUnit</c> would do behind
/// an <c>[AvaloniaFact]</c> attribute; that package is not referenced because it is built against
/// xunit.v3 and the rest of the solution is on xunit 2.
/// </remarks>
public sealed class HeadlessSession : IDisposable
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessSession).Assembly);

    /// <summary>Runs <paramref name="body"/> on the UI thread and rethrows anything it throws.</summary>
    public void Run(Action body) =>
        _session.Dispatch(body, CancellationToken.None).GetAwaiter().GetResult();

    public void Dispose() => _session.Dispose();
}

/// <summary>Shares one dispatcher thread across every UI test; starting one per test is slow.</summary>
[CollectionDefinition(Name)]
public sealed class HeadlessCollection : ICollectionFixture<HeadlessSession>
{
    public const string Name = "avalonia-headless";
}
