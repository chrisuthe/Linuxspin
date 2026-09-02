using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.Platform.Shared.Media;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Retention in <see cref="ArtworkCache"/>: a bounded set of files, keyed by content.
/// </summary>
/// <remarks>
/// The cache lives in a runtime directory that is commonly a size-limited tmpfs, so it must prune;
/// and because files are named by their bytes, a picture sent twice must occupy one slot, not two.
/// </remarks>
public sealed class ArtworkCacheTests
{
    [Fact]
    public void Write_KeepsTheNewestEightPictures()
    {
        using var paths = new TemporaryPaths();
        var cache = new ArtworkCache(paths, NullLogger<ArtworkCache>.Instance);

        var written = Enumerable.Range(0, 9).Select(i => cache.Write(Jpeg((byte)i))).ToList();

        Assert.All(written, Assert.NotNull);
        Assert.False(File.Exists(written[0]));
        Assert.All(written.Skip(1), path => Assert.True(File.Exists(path)));
        Assert.Equal(8, Directory.GetFiles(paths.AlbumArtCacheDirectory).Length);
    }

    [Fact]
    public void Write_ThenTheSamePictureAgain_IsOneFileAtOnePath()
    {
        using var paths = new TemporaryPaths();
        var cache = new ArtworkCache(paths, NullLogger<ArtworkCache>.Instance);

        var first = cache.Write(Jpeg(0xA1));
        var again = cache.Write(Jpeg(0xA1));

        Assert.Equal(first, again);
        Assert.Single(Directory.GetFiles(paths.AlbumArtCacheDirectory));
    }

    /// <summary>
    /// Re-sending a picture refreshes its place in the retention order rather than adding to it,
    /// so it is the untouched oldest picture that goes when the cache is full.
    /// </summary>
    [Fact]
    public void Write_ResendingAPicture_MovesItToNewest()
    {
        using var paths = new TemporaryPaths();
        var cache = new ArtworkCache(paths, NullLogger<ArtworkCache>.Instance);

        var oldest = cache.Write(Jpeg(0));
        var second = cache.Write(Jpeg(1));
        for (byte i = 2; i < 8; i++)
        {
            cache.Write(Jpeg(i));
        }

        cache.Write(Jpeg(0));
        cache.Write(Jpeg(8));

        Assert.True(File.Exists(oldest));
        Assert.False(File.Exists(second));
        Assert.Equal(8, Directory.GetFiles(paths.AlbumArtCacheDirectory).Length);
    }

    [Fact]
    public void Clear_DeletesEverythingWritten()
    {
        using var paths = new TemporaryPaths();
        var cache = new ArtworkCache(paths, NullLogger<ArtworkCache>.Instance);
        var path = cache.Write(Jpeg(0xA1));

        cache.Clear();

        Assert.False(File.Exists(path));
    }

    /// <summary>A JPEG signature followed by one byte that makes this picture distinct.</summary>
    private static byte[] Jpeg(byte marker) => [0xFF, 0xD8, 0xFF, marker];
}
