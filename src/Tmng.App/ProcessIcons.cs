using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Tmng.Platform.Windows;

namespace Tmng.App;

/// <summary>Executable icons, loaded once per path off the UI thread.</summary>
internal static class ProcessIcons
{
    // 48 px stays sharp at 32 DIP on a 150% display and scales down cleanly for list rows.
    private const int Size = 48;
    private static readonly ConcurrentDictionary<string, Lazy<Task<Bitmap?>>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<Task<Bitmap?>> GenericIcon = new(() => Task.Run(() => ToBitmap(FileIcons.Generic)));

    /// <summary>
    /// The executable's own icon. A process TMNG may not inspect (null path) or a file without an
    /// icon gets the shared generic application icon.
    /// </summary>
    public static Task<Bitmap?> Get(string? path) =>
        path is null
            ? GenericIcon.Value
            : Cache.GetOrAdd(path, key => new Lazy<Task<Bitmap?>>(() => Load(key))).Value;

    private static async Task<Bitmap?> Load(string path) =>
        await Task.Run(() => ToBitmap(() => FileIcons.Load(path, Size))) ?? await GenericIcon.Value;

    private static Bitmap? ToBitmap(Func<IconPixels?> load)
    {
        try
        {
            if (load() is not { } icon) return null;
            var bitmap = new WriteableBitmap(new PixelSize(icon.Width, icon.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using var buffer = bitmap.Lock();
            int stride = icon.Width * 4;
            for (int y = 0; y < icon.Height; y++)
                Marshal.Copy(icon.Bgra, y * stride, buffer.Address + y * buffer.RowBytes, stride);
            return bitmap;
        }
        catch (Exception)
        {
            // A missing icon only costs a blank square; never let it reach the sampling loop.
            return null;
        }
    }
}
