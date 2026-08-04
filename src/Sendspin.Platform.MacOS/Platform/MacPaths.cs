using Sendspin.Core.Platform;

namespace Sendspin.Platform.MacOS.Platform;

/// <summary>
/// macOS application directories, following the <c>~/Library</c> layout.
/// </summary>
/// <remarks>
/// <para>
/// Configuration and data share <c>~/Library/Application Support/Sendspin</c>. macOS draws no
/// distinction between the two — that split is an XDG idea — so inventing separate directories here
/// would produce a layout no other Mac app has.
/// </para>
/// <para>
/// <c>~/Library/Logs</c> is a real platform convention rather than a preference: it is where
/// Console.app looks, so a log written anywhere else is a log a user cannot find. That is worth the
/// one override of <see cref="PlatformPathsBase.LogDirectory"/>.
/// </para>
/// </remarks>
public sealed class MacPaths : PlatformPathsBase
{
    private const string ApplicationName = "Sendspin";

    /// <inheritdoc/>
    public override string ConfigDirectory =>
        Path.Combine(Home, "Library", "Application Support", ApplicationName);

    /// <inheritdoc/>
    public override string DataDirectory => ConfigDirectory;

    /// <inheritdoc/>
    public override string CacheDirectory => Path.Combine(Home, "Library", "Caches", ApplicationName);

    /// <inheritdoc/>
    public override string LogDirectory => Path.Combine(Home, "Library", "Logs", ApplicationName);

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
