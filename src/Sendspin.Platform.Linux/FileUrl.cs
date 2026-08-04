namespace Sendspin.Platform.Linux;

/// <summary>
/// Converts filesystem paths to <c>file://</c> URLs for handing to a shell.
/// </summary>
/// <remarks>
/// Both the MPRIS <c>mpris:artUrl</c> field and the notification <c>image-path</c> hint are
/// specified as URIs, and both shells are stricter about that than the folklore suggests: KDE's
/// lock screen refuses anything but a local file, and GNOME resolves the value through GVfs, which
/// wants a real URI. Percent-escaping matters too — a track called "Say What?" produces a filename
/// a bare path cannot express.
/// </remarks>
internal static class FileUrl
{
    /// <summary>
    /// Builds a percent-escaped <c>file://</c> URL for an absolute path.
    /// </summary>
    /// <returns>False for a null, relative, or unrepresentable path.</returns>
    public static bool TryCreate(string? path, out string url)
    {
        url = string.Empty;

        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
        {
            return false;
        }

        try
        {
            url = new Uri(path, UriKind.Absolute).AbsoluteUri;
            return true;
        }
        catch (UriFormatException)
        {
            // A path a URI cannot express is not worth failing an update over: the shell simply
            // shows no picture.
            return false;
        }
    }
}
