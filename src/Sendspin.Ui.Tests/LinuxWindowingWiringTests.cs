using Avalonia;
using Avalonia.X11;
using Sendspin.Core.Platform;
using Sendspin.Player;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Tests that every Linux windowing backend is wired well enough to start an app.
/// </summary>
/// <remarks>
/// <para>
/// The bug these exist for: the Wayland branch called <c>UseWayland()</c> alone, where the X11
/// branch went through <c>UsePlatformDetect()</c>, which quietly wires Skia and HarfBuzz too. So
/// the app aborted on startup with "No rendering system configured", and the test suite —
/// which pinned only the spelling of the environment variable — stayed green.
/// </para>
/// <para>
/// <c>AppBuilder.Setup()</c> is what raised that, and calling it here would need a live display,
/// so these assert the subsystem slots <c>Setup()</c> checks. Each backend must fill all of them,
/// so a branch that names a windowing backend and forgets the stack under it fails here rather
/// than at a user's startup.
/// </para>
/// </remarks>
public sealed class LinuxWindowingWiringTests
{
    [Theory]
    [InlineData(LinuxWindowingBackend.Wayland)]
    [InlineData(LinuxWindowingBackend.X11)]
    public void ConfigureWindowing_WiresEverySubsystemStartupRequires(LinuxWindowingBackend backend)
    {
        var builder = PlatformSelection.ConfigureWindowing(AppBuilder.Configure<TestApp>(), backend);

        Assert.NotNull(builder.WindowingSubsystemInitializer);
        Assert.NotNull(builder.RenderingSubsystemInitializer);
        Assert.NotNull(builder.TextShapingSubsystemInitializer);
    }

    /// <remarks>
    /// By the assembly the initializer came out of, because Avalonia 12 leaves
    /// <c>WindowingSubsystemName</c> empty for both of these backends.
    /// </remarks>
    [Theory]
    [InlineData(LinuxWindowingBackend.Wayland, "Avalonia.Wayland")]
    [InlineData(LinuxWindowingBackend.X11, "Avalonia.X11")]
    public void ConfigureWindowing_WiresTheBackendItWasAskedFor(LinuxWindowingBackend backend, string expected)
    {
        var builder = PlatformSelection.ConfigureWindowing(AppBuilder.Configure<TestApp>(), backend);

        Assert.Equal(expected, builder.WindowingSubsystemInitializer!.Method.DeclaringType!.Assembly.GetName().Name);
    }

    /// <remarks>
    /// Every member, so adding one to the enum without wiring it fails here rather than silently
    /// landing on whichever backend the catch-all arm names.
    /// </remarks>
    [Fact]
    public void ConfigureWindowing_HandlesEveryBackendTheEnumDeclares()
    {
        foreach (var backend in Enum.GetValues<LinuxWindowingBackend>())
        {
            Assert.NotNull(
                PlatformSelection.ConfigureWindowing(AppBuilder.Configure<TestApp>(), backend)
                    .WindowingSubsystemInitializer);
        }
    }

    /// <remarks>
    /// <para>
    /// The identity the X11 branch hands to Avalonia, asserted through the factory rather than
    /// through the builder: <c>With</c> defers the binding to <c>Setup()</c>, which wants a
    /// display, so there is nothing to read back from a headless test.
    /// </para>
    /// <para>
    /// The string is the one every desktop file names in <c>StartupWMClass</c>. The two are a
    /// pair, and <see cref="IconSetTests.EveryDesktopFile_NamesTheSameApplicationIdentity"/>
    /// pins the other half.
    /// </para>
    /// </remarks>
    [Fact]
    public void CreateX11Options_NamesTheDesktopEntryAsTheWindowClass()
    {
        Assert.Equal("io.sendspin.client", PlatformSelection.CreateX11Options().WmClass);
    }

    /// <remarks>
    /// Wiring the options must not cost the backend its rendering stack, which is the failure
    /// the tests above exist for; the X11 arm now has an extra call in it.
    /// </remarks>
    [Fact]
    public void ConfigureWindowing_StillWiresX11AfterTheOptionsAreApplied()
    {
        var builder = PlatformSelection.ConfigureWindowing(
            AppBuilder.Configure<TestApp>(), LinuxWindowingBackend.X11);

        Assert.Equal(
            "Avalonia.X11",
            builder.WindowingSubsystemInitializer!.Method.DeclaringType!.Assembly.GetName().Name);
        Assert.NotNull(builder.RenderingSubsystemInitializer);
        Assert.NotNull(builder.TextShapingSubsystemInitializer);
    }

    /// <remarks>
    /// The rendering stack is chained onto the choice rather than repeated per branch, so both
    /// backends must land on the same one. A branch that grew its own is the drift this guards.
    /// </remarks>
    [Fact]
    public void ConfigureWindowing_GivesEveryBackendTheSameRenderingStack()
    {
        var wayland = PlatformSelection.ConfigureWindowing(AppBuilder.Configure<TestApp>(), LinuxWindowingBackend.Wayland);
        var x11 = PlatformSelection.ConfigureWindowing(AppBuilder.Configure<TestApp>(), LinuxWindowingBackend.X11);

        Assert.Equal(wayland.RenderingSubsystemName, x11.RenderingSubsystemName);
        Assert.Equal(wayland.TextShapingSubsystemName, x11.TextShapingSubsystemName);
    }
}
