using System.Buffers.Binary;
using Xunit;

namespace Sendspin.Ui.Tests;

/// <summary>
/// Pins the shape of the committed icon set under <c>packaging/icons/</c>.
/// </summary>
/// <remarks>
/// <para>
/// The set is generated from one SVG master and committed, so a build needs no rasterizer.
/// CI's <c>icons</c> job checks the committed files were regenerated when a master changed;
/// it cannot check that what was committed is well formed. These do, and the failure they
/// exist for is silent: a truncated or single-size container is still a valid file, and the
/// only symptom is the generic icon coming back on one platform.
/// </para>
/// <para>
/// Here rather than in a script for the same reason as <see cref="AxamlHygieneTests"/> — a
/// script is not run by CI and a test is.
/// </para>
/// </remarks>
public sealed class IconSetTests
{
    /// <summary>What Windows picks between, and what scripts/generate-icons.sh writes.</summary>
    private static readonly int[] IcoSizes = [16, 24, 32, 48, 64, 128, 256];

    /// <summary>The freedesktop theme sizes, as installed by every Linux packaging path.</summary>
    private static readonly int[] HicolorSizes = [16, 22, 24, 32, 48, 64, 128, 256, 512];

    /// <summary>
    /// The OSType and pixel size of every representation macOS looks for, in iconutil's order.
    /// </summary>
    private static readonly (string Type, int Size)[] IcnsEntries =
    [
        ("icp4", 16), ("icp5", 32), ("ic11", 32), ("ic12", 64), ("ic07", 128),
        ("ic13", 256), ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024),
    ];

    private static string IconsDirectory => Path.Combine(PlayerSource.Root(), "packaging", "icons");

    [Fact]
    public void Ico_CarriesEverySizeWindowsPicksBetween()
    {
        var bytes = File.ReadAllBytes(Path.Combine(IconsDirectory, "sendspin.ico"));

        Assert.Equal(0, Read16(bytes, 0));
        Assert.Equal(1, Read16(bytes, 2));
        var count = Read16(bytes, 4);
        Assert.Equal(IcoSizes.Length, count);

        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (16 * i);
            var expected = IcoSizes[i];

            // The format spells 256 as 0, because the field is one byte wide.
            Assert.Equal(expected % 256, bytes[entry]);
            Assert.Equal(expected % 256, bytes[entry + 1]);

            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 8));
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 12));
            Assert.InRange(offset + length, 0, bytes.Length);

            var image = bytes.AsSpan(offset, length);
            Assert.Equal(expected, PngWidth(image));
            Assert.Equal(expected, PngHeight(image));
        }
    }

    [Fact]
    public void Icns_CarriesEveryRepresentationMacOsLooksFor()
    {
        var bytes = File.ReadAllBytes(Path.Combine(IconsDirectory, "sendspin.icns"));

        Assert.Equal("icns", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(bytes.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)));

        var found = new List<(string Type, int Size)>();
        var cursor = 8;

        while (cursor < bytes.Length)
        {
            var type = System.Text.Encoding.ASCII.GetString(bytes, cursor, 4);
            // A chunk's declared length includes its own eight-byte header.
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor + 4));
            Assert.InRange(length, 9, bytes.Length - cursor);

            var payload = bytes.AsSpan(cursor + 8, length - 8);
            Assert.Equal(PngWidth(payload), PngHeight(payload));
            found.Add((type, PngWidth(payload)));

            cursor += length;
        }

        Assert.Equal(bytes.Length, cursor);
        Assert.Equal(IcnsEntries, found);
    }

    [Fact]
    public void HicolorTree_CarriesEverySizeAndTheScalableMaster()
    {
        foreach (var size in HicolorSizes)
        {
            var path = Path.Combine(
                IconsDirectory, "hicolor", $"{size}x{size}", "apps", "io.sendspin.client.png");

            Assert.True(File.Exists(path), $"{path} is missing");

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(size, PngWidth(bytes));
            Assert.Equal(size, PngHeight(bytes));
        }

        Assert.True(File.Exists(Path.Combine(
            IconsDirectory, "hicolor", "scalable", "apps", "io.sendspin.client.svg")));
    }

    /// <summary>
    /// The point of the centralization: one raster per size, not the three byte-identical
    /// copies of the mark this replaced.
    /// </summary>
    /// <remarks>
    /// Over <c>packaging/</c> and <c>src/</c>, which is where all three copies lived and is
    /// narrow enough to stay out of <c>artifacts/</c> — a local <c>build.sh</c> run installs
    /// the theme there, and those copies are the point rather than a defect.
    /// </remarks>
    [Fact]
    public void NoTwoPngs_InThePackagedTreeAreTheSameImage()
    {
        var root = PlayerSource.Root();
        var byContent = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var directory in new[] { "packaging", "src" })
        {
            foreach (var path in SourcePngs(Path.Combine(root, directory)))
            {
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

                if (!byContent.TryGetValue(hash, out var paths))
                {
                    byContent[hash] = paths = [];
                }

                paths.Add(Path.GetRelativePath(root, path));
            }
        }

        Assert.NotEmpty(byContent);

        var duplicated = byContent.Values.Where(p => p.Count > 1).ToList();

        Assert.True(
            duplicated.Count == 0,
            "The same image is committed more than once: "
                + string.Join("; ", duplicated.Select(p => string.Join(" == ", p))));
    }

    /// <summary>
    /// The menu bar pair, whose wrong size is the one failure nothing else here would show:
    /// AppKit scales a mismatched template image rather than refusing it, so the only symptom
    /// is a status item that looks subtly wrong next to the SF Symbols beside it.
    /// </summary>
    [Theory]
    [InlineData("sendspin-menubar.png", 22)]
    [InlineData("sendspin-menubar@2x.png", 44)]
    public void MenuBarTemplate_IsTheSizeAppKitExpects(string name, int size)
    {
        var bytes = File.ReadAllBytes(Path.Combine(IconsDirectory, name));

        Assert.Equal(size, PngWidth(bytes));
        Assert.Equal(size, PngHeight(bytes));
    }

    /// <summary>
    /// Every desktop file has to agree with <c>PlatformSelection.CreateX11Options</c>, or a
    /// desktop cannot match the running window to the entry that names the icon.
    /// </summary>
    [Fact]
    public void EveryDesktopFile_NamesTheSameApplicationIdentity()
    {
        var packaging = Path.Combine(PlayerSource.Root(), "packaging");
        var files = Directory.EnumerateFiles(packaging, "*.desktop", SearchOption.AllDirectories).ToList();

        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            Assert.Contains("Icon=io.sendspin.client", lines);
            Assert.Contains("StartupWMClass=io.sendspin.client", lines);
        }
    }

    /// <summary>Every PNG under a source directory, build output excluded.</summary>
    private static IEnumerable<string> SourcePngs(string directory) =>
        Directory.EnumerateFiles(directory, "*.png", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static ushort Read16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));

    /// <summary>Reads IHDR, which a PNG puts at a fixed offset right after the signature.</summary>
    private static int PngWidth(ReadOnlySpan<byte> png) =>
        (int)BinaryPrimitives.ReadUInt32BigEndian(png[16..]);

    private static int PngHeight(ReadOnlySpan<byte> png) =>
        (int)BinaryPrimitives.ReadUInt32BigEndian(png[20..]);
}
