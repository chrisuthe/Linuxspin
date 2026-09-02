using Microsoft.Extensions.Logging;
using Sendspin.Core.MediaSession;
using Sendspin.Core.Platform;

namespace Sendspin.Platform.Shared.Media;

/// <summary>
/// Writes incoming artwork to disk so OS media surfaces can be handed a <c>file://</c> path.
/// </summary>
/// <remarks>
/// <para>
/// Artwork arrives over the protocol as bytes, but no shell will take bytes. A file is the
/// only universally workable answer, and on Linux it is the only workable answer at all: KDE's
/// lock screen installs a deny-all network factory so <c>http</c> and <c>data:</c> URLs are
/// both refused, and GNOME ships no <c>data:</c> GVfs backend. Rather than have each platform
/// discover that separately, every platform gets a file.
/// </para>
/// <para>
/// <strong>One file per picture, never one reused file.</strong> GNOME's texture cache is keyed
/// on the icon string for the lifetime of the shell, so writing every track's art to
/// <c>artwork.jpg</c> leaves the first track's picture on screen for the rest of the session.
/// Names come from <see cref="MediaSessionMapper.ArtworkFileName"/>, which hashes the image
/// bytes rather than the track's metadata; its remarks say why.
/// </para>
/// <para>
/// Files land in <see cref="IPlatformPaths.AlbumArtCacheDirectory"/>. Under Flatpak that
/// resolves inside <c>$XDG_RUNTIME_DIR/app/$FLATPAK_ID/</c> rather than <c>/tmp</c>, because
/// the sandbox's <c>/tmp</c> is not the host's and a path into it is one the shell cannot
/// follow.
/// </para>
/// </remarks>
public sealed class ArtworkCache
{
    /// <summary>
    /// How many artwork files to keep. Enough that going back a track still has its picture,
    /// small enough that a long shuffle session does not fill a runtime directory — which on
    /// Linux is commonly a size-limited tmpfs.
    /// </summary>
    private const int RetainedFileCount = 8;

    private readonly IPlatformPaths _paths;
    private readonly ILogger<ArtworkCache> _logger;
    private readonly Lock _gate = new();
    private readonly List<string> _written = [];

    public ArtworkCache(IPlatformPaths paths, ILogger<ArtworkCache> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Writes a picture and returns its absolute path, or null when it could not be written.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing: artwork is decoration, and failing to cache a
    /// picture must not interrupt playback. The reason is logged.
    /// </remarks>
    /// <param name="imageData">Encoded image bytes as received from the server.</param>
    public string? Write(ReadOnlySpan<byte> imageData)
    {
        if (imageData.IsEmpty)
        {
            return null;
        }

        var extension = DetectExtension(imageData);
        var fileName = MediaSessionMapper.ArtworkFileName(imageData, extension);

        try
        {
            var directory = _paths.AlbumArtCacheDirectory;
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, fileName);

            // Write to a temporary name and move into place: a shell that reads the path the
            // moment it is published must never see a half-written image.
            var temporaryPath = path + ".tmp";
            using (var stream = File.Create(temporaryPath))
            {
                stream.Write(imageData);
            }

            File.Move(temporaryPath, path, overwrite: true);

            Track(path);
            return path;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not write artwork {FileName}", fileName);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Not permitted to write artwork {FileName}", fileName);
            return null;
        }
    }

    /// <summary>
    /// Deletes every file this cache has written. Called on shutdown.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var path in _written)
            {
                TryDelete(path);
            }

            _written.Clear();
        }
    }

    /// <summary>
    /// Records a written file and prunes back to <see cref="RetainedFileCount"/>.
    /// </summary>
    private void Track(string path)
    {
        lock (_gate)
        {
            _written.Remove(path);
            _written.Add(path);

            while (_written.Count > RetainedFileCount)
            {
                var oldest = _written[0];
                _written.RemoveAt(0);
                TryDelete(oldest);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not delete cached artwork {Path}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Not permitted to delete cached artwork {Path}", path);
        }
    }

    /// <summary>
    /// Picks a file extension from the image's magic bytes.
    /// </summary>
    /// <remarks>
    /// The protocol says which format was negotiated, but the extension has to match the
    /// actual bytes: some shells sniff content and some trust the extension, and the two
    /// disagreeing produces a picture that renders in one place and not another. Sniffing the
    /// bytes is the only answer that satisfies both.
    /// </remarks>
    private static string DetectExtension(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "jpg";
        }

        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "png";
        }

        if (data.Length >= 12 &&
            data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
            data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
        {
            return "webp";
        }

        // Unrecognised. Serve it as .jpg, which is what the client advertises and what the
        // server therefore sends, rather than inventing an extension no shell will open.
        return "jpg";
    }
}
