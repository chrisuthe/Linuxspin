using Sendspin.Core.Platform;

namespace Sendspin.Platform.Windows.Platform;

/// <summary>
/// Windows application directories, all under <c>%LocalAppData%\Sendspin</c>.
/// </summary>
/// <remarks>
/// Local rather than roaming <c>AppData</c>: everything persisted here is specific to this
/// machine — the audio device id, the per-device latency calibration, the artwork cache — and
/// roaming one room's speaker delay to a laptop somewhere else would be actively wrong.
/// </remarks>
public sealed class WindowsPaths : PlatformPathsBase
{
    private const string ApplicationFolderName = "Sendspin";

    private readonly string _root;

    public WindowsPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _root = Path.Combine(localAppData, ApplicationFolderName);
    }

    /// <inheritdoc/>
    public override string ConfigDirectory => Path.Combine(_root, "config");

    /// <inheritdoc/>
    public override string DataDirectory => Path.Combine(_root, "data");

    /// <inheritdoc/>
    public override string CacheDirectory => Path.Combine(_root, "cache");
}
