using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Sendspin.Platform.Windows.MediaSession;

/// <summary>
/// <c>ITaskbarList</c>: the original taskbar interface. Declared only because its five slots
/// come first in <see cref="ITaskbarList3"/>'s vtable.
/// </summary>
[GeneratedComInterface]
[Guid("56FDF342-FD6D-11D0-958A-006097C9A090")]
internal partial interface ITaskbarList
{
    void HrInit();

    void AddTab(nint hwnd);

    void DeleteTab(nint hwnd);

    void ActivateTab(nint hwnd);

    void SetActiveAlt(nint hwnd);
}

/// <summary>
/// <c>ITaskbarList2</c>, which adds one slot to <see cref="ITaskbarList"/>.
/// </summary>
/// <remarks>
/// <c>fFullscreen</c> is a Win32 <c>BOOL</c>, declared as <see cref="int"/> so the slot is
/// exactly four bytes without a marshalling rule for a method this player never calls.
/// </remarks>
[GeneratedComInterface]
[Guid("602D4995-B13A-429B-A66E-1935E44F4317")]
internal partial interface ITaskbarList2 : ITaskbarList
{
    void MarkFullscreenWindow(nint hwnd, int fFullscreen);
}

/// <summary>
/// <c>ITaskbarList3</c>: overlay icons, progress, and thumbnail toolbar buttons.
/// </summary>
/// <remarks>
/// <para>
/// Every method is declared, in vtable order, because a COM vtable cannot be declared
/// partially — an omitted slot would silently shift every method after it. Only
/// <see cref="SetOverlayIcon"/> is used today.
/// </para>
/// <para>
/// The thumbbar methods take the button array as a raw pointer rather than a marshalled struct
/// array: nothing here calls them yet, and a marshalling rule for a struct that is not used
/// would be a guess that no test could catch.
/// </para>
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
internal partial interface ITaskbarList3 : ITaskbarList2
{
    void SetProgressValue(nint hwnd, ulong completed, ulong total);

    void SetProgressState(nint hwnd, int flags);

    void RegisterTab(nint hwndTab, nint hwndMdi);

    void UnregisterTab(nint hwndTab);

    void SetTabOrder(nint hwndTab, nint hwndInsertBefore);

    void SetTabActive(nint hwndTab, nint hwndMdi, uint reserved);

    void ThumbBarAddButtons(nint hwnd, uint buttonCount, nint buttons);

    void ThumbBarUpdateButtons(nint hwnd, uint buttonCount, nint buttons);

    void ThumbBarSetImageList(nint hwnd, nint imageList);

    void SetOverlayIcon(nint hwnd, nint icon, string? description);

    void SetThumbnailTooltip(nint hwnd, string? tip);

    void SetThumbnailClip(nint hwnd, nint clipRectangle);
}

/// <summary>
/// The native calls behind <see cref="TaskbarTransport"/>: activating the shell's taskbar object,
/// and building the small icons used as overlay badges.
/// </summary>
/// <remarks>
/// Icons are composed pixel by pixel and handed to <c>CreateIconIndirect</c> rather than loaded
/// from resources, so the badge needs no image assets, no <c>System.Drawing</c>, and no work from
/// the build.
/// </remarks>
internal static partial class TaskbarInterop
{
    /// <summary>
    /// Badge edge length in pixels. <c>SM_CXSMICON</c> is 16 at 100% scaling, and the shell
    /// scales the overlay itself, so a fixed 16 is legible without querying metrics.
    /// </summary>
    internal const int BadgeSize = 16;

    private const uint ClsctxInprocServer = 0x1;

    private static readonly Guid ClsidTaskbarList = new("56FDF344-FD6D-11D0-958A-006097C9A090");
    private static readonly Guid IidTaskbarList3 = new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");

    /// <summary>
    /// Glyphs the badge can carry.
    /// </summary>
    internal enum BadgeGlyph
    {
        Play,
        Pause
    }

    /// <summary>
    /// Activates the shell's taskbar list object, or returns null with the HRESULT when the shell
    /// will not provide one.
    /// </summary>
    /// <remarks>
    /// Fails on Windows Server core installations and in session 0, where there is no shell to
    /// talk to. That is a normal absence rather than an error.
    /// </remarks>
    internal static ITaskbarList3? TryCreateTaskbarList(out int hresult)
    {
        hresult = CoCreateInstance(in ClsidTaskbarList, 0, ClsctxInprocServer, in IidTaskbarList3, out var instance);

        if (hresult < 0 || instance == 0)
        {
            return null;
        }

        try
        {
            // The wrapper takes its own reference, so ours is released either way.
            return (ITaskbarList3)new StrategyBasedComWrappers()
                .GetOrCreateObjectForComInstance(instance, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    /// <summary>
    /// Builds a 16x16 badge icon, or returns 0 when GDI refused to allocate one.
    /// </summary>
    /// <remarks>
    /// The caller owns the result and must pass it to <see cref="DestroyIcon"/>.
    /// </remarks>
    internal static nint CreateBadgeIcon(BadgeGlyph glyph)
    {
        var pixels = RenderBadge(glyph);

        // A 32bpp colour bitmap carries its own alpha, so the mask is all zeroes: every pixel is
        // taken from the colour bitmap and transparency comes from the alpha channel.
        var maskBits = new byte[BadgeSize * 4];

        var colorBitmap = CreateBitmap(BadgeSize, BadgeSize, 1, 32, ref pixels[0]);
        if (colorBitmap == 0)
        {
            return 0;
        }

        var maskBitmap = CreateBitmap(BadgeSize, BadgeSize, 1, 1, ref maskBits[0]);
        if (maskBitmap == 0)
        {
            DeleteObject(colorBitmap);
            return 0;
        }

        var iconInfo = new IconInfo
        {
            IsIcon = 1,
            HotspotX = 0,
            HotspotY = 0,
            MaskBitmap = maskBitmap,
            ColorBitmap = colorBitmap
        };

        var icon = CreateIconIndirect(ref iconInfo);

        // CreateIconIndirect copies both bitmaps.
        DeleteObject(maskBitmap);
        DeleteObject(colorBitmap);

        return icon;
    }

    [LibraryImport("user32.dll")]
    internal static partial int DestroyIcon(nint icon);

    /// <summary>
    /// Paints the badge: an opaque dark disc with a white transport glyph, so it reads against
    /// both a light and a dark taskbar.
    /// </summary>
    /// <remarks>
    /// Pixels are <c>0xAARRGGBB</c> and either fully opaque or fully transparent, which keeps
    /// them valid as the premultiplied alpha the shell's compositor expects.
    /// </remarks>
    private static uint[] RenderBadge(BadgeGlyph glyph)
    {
        const uint Transparent = 0x00000000;
        const uint Disc = 0xFF1F1F1FU;
        const uint Glyph = 0xFFFFFFFFU;
        const int Centre = BadgeSize / 2;
        const int RadiusSquared = Centre * Centre;

        var pixels = new uint[BadgeSize * BadgeSize];

        for (var y = 0; y < BadgeSize; y++)
        {
            for (var x = 0; x < BadgeSize; x++)
            {
                var dx = x - Centre + 0.5;
                var dy = y - Centre + 0.5;
                var inside = (dx * dx) + (dy * dy) <= RadiusSquared;

                pixels[(y * BadgeSize) + x] = !inside
                    ? Transparent
                    : IsGlyphPixel(glyph, x, y) ? Glyph : Disc;
            }
        }

        return pixels;
    }

    private static bool IsGlyphPixel(BadgeGlyph glyph, int x, int y)
    {
        if (glyph == BadgeGlyph.Pause)
        {
            return y is >= 4 and <= 11 && (x is >= 5 and <= 6 || x is >= 9 and <= 10);
        }

        // A right-pointing triangle: full height at its base and a single pixel at its apex.
        if (x is < 5 or > 11)
        {
            return false;
        }

        var halfHeight = (11 - x) * 4 / 6;
        return Math.Abs(y - 8) <= halfHeight;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid classId,
        nint outer,
        uint context,
        in Guid interfaceId,
        out nint instance);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, ref byte bits);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, ref uint bits);

    [LibraryImport("gdi32.dll")]
    private static partial int DeleteObject(nint handle);

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconIndirect(ref IconInfo iconInfo);

    /// <summary>
    /// <c>ICONINFO</c>. <c>fIcon</c> is a Win32 <c>BOOL</c>, so it is four bytes rather than a
    /// marshalled <see cref="bool"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }
}
