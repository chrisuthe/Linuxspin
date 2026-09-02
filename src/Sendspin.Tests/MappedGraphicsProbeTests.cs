using Sendspin.Core.Visualization;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Pins the GPU-or-not decision against the evidence lines the shell spike recorded on this box,
/// so the rule is tied to what the loader was actually seen to map in each case.
/// </summary>
public sealed class MappedGraphicsProbeTests
{
    /// <summary>The Wayland head on a GPU: the vendor loader, Mesa's driver, a render node open.</summary>
    [Fact]
    public void AMesaDriverWithARenderNode_IsAGpu() =>
        Assert.True(MappedGraphicsProbe.Classify(
            ["libEGL.so.1", "libEGL_mesa.so.0", "libgallium-25.1.so", "radeonsi_dri.so", "libSkiaSharp.so"],
            ["/dev/dri/renderD128"]));

    /// <summary>The shm fallback: EGL found no vendor, nothing else loaded, no node open.</summary>
    [Fact]
    public void OnlyTheVendorLoaderAndSkia_IsNotAGpu() =>
        Assert.False(MappedGraphicsProbe.Classify(["libEGL.so.1", "libSkiaSharp.so"], []));

    [Fact]
    public void NothingMappedAtAll_IsNotAGpu() =>
        Assert.False(MappedGraphicsProbe.Classify([], []));

    /// <summary>A driver library without a node still counts: NVIDIA's EGL path opens none it reports here.</summary>
    [Fact]
    public void ADriverWithoutANode_IsAGpu() =>
        Assert.True(MappedGraphicsProbe.Classify(["libEGL.so.1", "libEGL_nvidia.so.0", "libnvidia-glcore.so"], []));

    [Fact]
    public void ANodeWithoutADriverName_IsAGpu() =>
        Assert.True(MappedGraphicsProbe.Classify(["libEGL.so.1"], ["/dev/dri/renderD129"]));

    /// <summary>Mesa's software rasteriser maps the same driver library, and must lose to its own name.</summary>
    [Theory]
    [InlineData("swrast_dri.so")]
    [InlineData("libllvmpipe.so")]
    [InlineData("LLVMPIPE")]
    public void ASoftwareRasteriser_IsNotAGpuWhateverElseIsMapped(string rasteriser) =>
        Assert.False(MappedGraphicsProbe.Classify(
            ["libEGL.so.1", "libgallium-25.1.so", rasteriser],
            ["/dev/dri/renderD128"]));

    [Fact]
    public void GlxOnTheX11Head_IsAGpu() =>
        Assert.True(MappedGraphicsProbe.Classify(["libGLX.so.0", "libGL.so.1", "libGLX_mesa.so.0"], []));

    /// <remarks>
    /// Reads the real loader. On Windows and macOS the answer is the platform assumption; on
    /// Linux it is whatever this test host is, which is not asserted, only that the read works.
    /// </remarks>
    [Fact]
    public void HasGpuContext_ReadsWithoutThrowing()
    {
        var probe = new MappedGraphicsProbe();

        _ = probe.HasGpuContext();
        _ = MappedGraphicsProbe.MappedLibraries();
        _ = MappedGraphicsProbe.OpenDrmNodes();
    }
}
