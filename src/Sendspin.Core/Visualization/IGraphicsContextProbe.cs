namespace Sendspin.Core.Visualization;

/// <summary>
/// Answers whether the process is drawing through a GPU, which decides whether the living
/// backdrop can afford to run.
/// </summary>
/// <remarks>
/// The three-ellipse backdrop costs 0.1 ms of CPU per frame on a GPU and 7 to 7.6 ms on a
/// software rasteriser at the default window size (the effect table in
/// <c>docs/ARCHITECTURE.md</c>), and it scales with window area. An interface so the headless
/// tests can drive both answers; <see cref="MappedGraphicsProbe"/> is the real one.
/// </remarks>
public interface IGraphicsContextProbe
{
    /// <summary>
    /// Whether a GPU context is drawing this process. Meaningful only after the first frame, when
    /// the renderer has loaded whatever it is going to load.
    /// </summary>
    bool HasGpuContext();
}
