using System.Runtime.InteropServices;

namespace Tmng.Platform.Windows.Interop;

internal static unsafe partial class Shell32
{
    public const uint StockIconApplication = 2;
    public const uint StockIconFlagIcon = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    public struct StockIconInfo
    {
        public uint Size;
        public nint Icon;
        public int SysImageIndex;
        public int IconIndex;
        public fixed char Path[260];
    }

    /// <summary><paramref name="sizes"/> packs the large icon size in the low word and the small one in the high word.</summary>
    [LibraryImport("shell32.dll", EntryPoint = "SHDefExtractIconW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHDefExtractIcon(string iconFile, int index, uint flags, nint* large, nint* small, uint sizes);

    [LibraryImport("shell32.dll")]
    public static partial int SHGetStockIconInfo(uint id, uint flags, ref StockIconInfo info);
}

internal static unsafe partial class User32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX, HotspotY;
        public nint Mask, Color;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int GetIconInfo(nint icon, out IconInfo info);

    [LibraryImport("user32.dll")]
    public static partial int DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint window, nint dc);
}

internal static unsafe partial class Gdi32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Bitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static partial int GetObject(nint obj, int size, void* buffer);

    /// <summary><paramref name="info"/> must have room for a colour table after the header.</summary>
    [LibraryImport("gdi32.dll")]
    public static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, void* info, uint usage);

    [LibraryImport("gdi32.dll")]
    public static partial int DeleteObject(nint obj);
}
