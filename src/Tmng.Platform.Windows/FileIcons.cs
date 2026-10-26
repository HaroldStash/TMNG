using Tmng.Platform.Windows.Interop;

namespace Tmng.Platform.Windows;

/// <summary>Straight-alpha BGRA pixels, top row first.</summary>
public sealed record IconPixels(int Width, int Height, byte[] Bgra);

public static unsafe class FileIcons
{
    /// <summary>The icon embedded in an executable, or null when the file carries none.</summary>
    public static IconPixels? Load(string path, int size)
    {
        nint icon = 0;
        Shell32.SHDefExtractIcon(path, 0, 0, &icon, null, (uint)size);
        return Take(icon);
    }

    /// <summary>The shell's generic application icon.</summary>
    public static IconPixels? Generic()
    {
        var stock = new Shell32.StockIconInfo { Size = (uint)sizeof(Shell32.StockIconInfo) };
        return Shell32.SHGetStockIconInfo(Shell32.StockIconApplication, Shell32.StockIconFlagIcon, ref stock) == 0
            ? Take(stock.Icon)
            : null;
    }

    private static IconPixels? Take(nint icon)
    {
        if (icon == 0) return null;
        try
        {
            return ToPixels(icon);
        }
        finally
        {
            User32.DestroyIcon(icon);
        }
    }

    private static IconPixels? ToPixels(nint icon)
    {
        if (User32.GetIconInfo(icon, out var info) == 0) return null;
        nint dc = User32.GetDC(0);
        try
        {
            // Monochrome icons have no colour bitmap; nothing current ships them.
            if (info.Color == 0) return null;
            Gdi32.Bitmap bitmap;
            if (Gdi32.GetObject(info.Color, sizeof(Gdi32.Bitmap), &bitmap) == 0) return null;
            int width = bitmap.Width, height = bitmap.Height;

            var pixels = ReadBits(dc, info.Color, width, height);
            if (pixels is null) return null;

            bool hasAlpha = false;
            for (int i = 3; i < pixels.Length && !hasAlpha; i += 4) hasAlpha = pixels[i] != 0;

            // Icons without an alpha channel mark transparent pixels in the AND mask instead.
            if (!hasAlpha && ReadBits(dc, info.Mask, width, height) is { } mask)
            {
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = mask[i - 3] == 0 ? (byte)255 : (byte)0;
            }
            return new IconPixels(width, height, pixels);
        }
        finally
        {
            User32.ReleaseDC(0, dc);
            if (info.Color != 0) Gdi32.DeleteObject(info.Color);
            if (info.Mask != 0) Gdi32.DeleteObject(info.Mask);
        }
    }

    private static byte[]? ReadBits(nint dc, nint bitmap, int width, int height)
    {
        // Header plus room for the colour table GetDIBits may write.
        byte* info = stackalloc byte[sizeof(Gdi32.BitmapInfoHeader) + 256 * 4];
        *(Gdi32.BitmapInfoHeader*)info = new Gdi32.BitmapInfoHeader
        {
            Size = (uint)sizeof(Gdi32.BitmapInfoHeader),
            Width = width,
            Height = -height, // negative: top-down rows
            Planes = 1,
            BitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        fixed (byte* p = pixels)
            return Gdi32.GetDIBits(dc, bitmap, 0, (uint)height, p, info, 0) == height ? pixels : null;
    }
}
