namespace Sendspin.Core.Visualization;

/// <summary>
/// Reads which graphics stack the process actually loaded, the way the shell spike's probe did.
/// </summary>
/// <remarks>
/// <para>
/// On Linux the loader is the honest witness: what was <em>asked for</em> says nothing about
/// what happened, since the Wayland backend has no software switch and falls through to its
/// <c>wl_shm</c> surface when EGL fails. So this reads <c>/proc/self/maps</c> for the GL driver
/// libraries and <c>/proc/self/fd</c> for an open DRM render node, and calls it a GPU when a
/// driver is mapped or a node is open, and neither <c>llvmpipe</c> nor <c>swrast</c> is. The
/// spike's evidence lines for the shm case read "only <c>libEGL.so.1</c> and
/// <c>libSkiaSharp.so</c> mapped and no DRM node open", which is why a bare <c>libEGL</c> does
/// not count: the vendor loader is mapped even when it found no vendor.
/// </para>
/// <para>
/// Windows and macOS are assumed to have a GPU: neither exposes the loader this way, and both
/// platforms' default backends are hardware-accelerated. <c>SENDSPIN_ASSUME_GPU=1</c> makes Linux
/// say the same, for a box whose GL the user trusts more than this heuristic, and for measuring
/// the backdrop's cost on the shm surface on purpose.
/// </para>
/// </remarks>
public sealed class MappedGraphicsProbe : IGraphicsContextProbe
{
    /// <summary>The environment variable that makes the probe assume a GPU.</summary>
    public const string AssumeGpuVariable = "SENDSPIN_ASSUME_GPU";

    private static readonly string[] DriverMarkers = ["libGLX", "libGL.", "_dri", "gallium", "nvidia"];
    private static readonly string[] SoftwareMarkers = ["llvmpipe", "swrast"];

    /// <inheritdoc/>
    public bool HasGpuContext()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable(AssumeGpuVariable) == "1")
        {
            return true;
        }

        return Classify(MappedLibraries(), OpenDrmNodes());
    }

    /// <summary>
    /// The decision, on its own so it can be tested against recorded evidence: a GPU when a GL
    /// driver is mapped or a DRM render node is open, unless a software rasteriser is mapped.
    /// </summary>
    /// <param name="mappedLibraries">File names of the graphics libraries mapped into the process.</param>
    /// <param name="openDrmNodes">Paths under <c>/dev/dri/</c> the process holds open.</param>
    public static bool Classify(IEnumerable<string> mappedLibraries, IEnumerable<string> openDrmNodes)
    {
        ArgumentNullException.ThrowIfNull(mappedLibraries);
        ArgumentNullException.ThrowIfNull(openDrmNodes);

        var libraries = mappedLibraries.ToList();

        if (libraries.Any(l => SoftwareMarkers.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        return libraries.Any(l => DriverMarkers.Any(m => l.Contains(m, StringComparison.Ordinal)))
               || openDrmNodes.Any(n => n.StartsWith("/dev/dri/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The graphics libraries mapped into this process, as file names, for the log line and the
    /// decision. Empty when the loader cannot be read.
    /// </summary>
    public static IReadOnlyList<string> MappedLibraries()
    {
        try
        {
            return
            [
                .. File.ReadAllLines("/proc/self/maps")
                    .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty)
                    .Where(path => path.Contains("libEGL", StringComparison.Ordinal)
                        || path.Contains("libSkiaSharp", StringComparison.Ordinal)
                        || DriverMarkers.Any(m => path.Contains(m, StringComparison.Ordinal))
                        || SoftwareMarkers.Any(m => path.Contains(m, StringComparison.OrdinalIgnoreCase)))
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
            ];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The DRM nodes this process holds open. Empty when the descriptor table cannot be read.</summary>
    public static IReadOnlyList<string> OpenDrmNodes()
    {
        try
        {
            return
            [
                .. Directory.EnumerateFiles("/proc/self/fd")
                    .Select(LinkTarget)
                    .Where(target => target.StartsWith("/dev/dri/", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
            ];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        static string LinkTarget(string descriptor)
        {
            try
            {
                return new FileInfo(descriptor).LinkTarget ?? string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }
    }
}
