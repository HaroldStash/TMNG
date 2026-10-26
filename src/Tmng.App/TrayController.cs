using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Tmng.App.Theme;
using Tmng.Core;

namespace Tmng.App;

/// <summary>
/// Notification-area icon that is itself a meter: three bars for CPU, GPU and memory, redrawn every
/// sample. The tooltip carries the numbers. Clicking it opens TMNG. The window's taskbar icon gets the
/// same live meter.
/// </summary>
public sealed class TrayController
{
    private static readonly Color Background = Color.Parse("#0D1015");

    private readonly TrayIcon _tray;
    private readonly Appearance _appearance;
    private Snapshot? _pending;

    private readonly Window _window;

    public TrayController(TelemetryHub hub, Appearance appearance, Window window, Action open, Action exit)
    {
        _appearance = appearance;
        _window = window;

        var openItem = new NativeMenuItem("Open TMNG");
        openItem.Click += (_, _) => open();
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => exit();

        _tray = new TrayIcon
        {
            ToolTipText = "TMNG",
            Icon = Icon(null, null, null),
            Menu = new NativeMenu { Items = { openItem, new NativeMenuItemSeparator(), exitItem } },
        };
        _tray.Clicked += (_, _) => open();
        TrayIcon.SetIcons(Application.Current!, [_tray]);

        SaveNotificationIcon();
        hub.Sampled += s =>
        {
            if (Interlocked.Exchange(ref _pending, s) is null) Dispatcher.UIThread.Post(Update);
        };
    }

    private void Update()
    {
        if (Interlocked.Exchange(ref _pending, null) is not { } s) return;

        double? cpu = s.Cpu.Utilization.Nullable;
        var gpuReading = s.PrimaryGpu()?.Utilization ?? default;
        double? gpu = gpuReading.Nullable;
        double? memory = s.Memory.UsedBytes.HasValue && s.Memory.TotalBytes.HasValue && s.Memory.TotalBytes.Value > 0
            ? s.Memory.UsedBytes.Value / s.Memory.TotalBytes.Value * 100
            : null;

        var icon = Icon(cpu / 100, gpu / 100, memory / 100);
        _tray.Icon = icon;
        _window.Icon = icon;

        // Windows truncates tray tooltips at 127 characters.
        string tip = $"TMNG\nCPU {Fmt.Percent(s.Cpu.Utilization, 0)} · GPU {Fmt.Percent(gpuReading, 0)} · Memory {(memory is { } m ? $"{m:0}%" : "—")}";
        if (s.Sensors.CpuTemperatureC.HasValue) tip += $"\nCPU {s.Sensors.CpuTemperatureC.Value:0} °C";
        if (s.Sensors.GpuTemperatureC.HasValue) tip += $"{(s.Sensors.CpuTemperatureC.HasValue ? " · " : "\n")}GPU {s.Sensors.GpuTemperatureC.Value:0} °C";
        _tray.ToolTipText = tip.Length > 127 ? tip[..127] : tip;
    }

    private WindowIcon Icon(double? cpu, double? gpu, double? memory)
    {
        var inks = _appearance.Inks;
        using var bitmap = Render(32, [(cpu, inks.Cpu.Accent), (gpu, inks.Gpu.Accent), (memory, inks.Memory.Accent)]);
        var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    /// <summary>A still of the meter for Windows to show beside notifications.</summary>
    private void SaveNotificationIcon()
    {
        try
        {
            var inks = InkSet.For(ColorScheme.FullColor);
            using var bitmap = Render(64, [(0.7, inks.Cpu.Accent), (0.45, inks.Gpu.Accent), (0.6, inks.Memory.Accent)]);
            Directory.CreateDirectory(AppSettings.Folder);
            bitmap.Save(Path.Combine(AppSettings.Folder, "tmng.png"));
        }
        catch (Exception)
        {
            // Notifications fall back to a generic icon.
        }
    }

    private static RenderTargetBitmap Render(int size, (double? Fill, Color Color)[] bars)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            double unit = size / 32.0;
            context.FillRectangle(new SolidColorBrush(Background), new Rect(0, 0, size, size), (float)(5 * unit));
            double gap = 2 * unit, margin = 3 * unit;
            double width = (size - 2 * margin - gap * (bars.Length - 1)) / bars.Length;
            double top = margin, bottom = size - margin, height = bottom - top;
            for (int i = 0; i < bars.Length; i++)
            {
                var (fill, color) = bars[i];
                double x = margin + i * (width + gap);
                context.FillRectangle(new SolidColorBrush(color, 0.22), new Rect(x, top, width, height), (float)unit);
                if (fill is not { } f || f <= 0) continue;
                // Any activity lights at least a sliver, so a busy-but-low reading never looks dead.
                double lit = Math.Max(2 * unit, Math.Clamp(f, 0, 1) * height);
                context.FillRectangle(new SolidColorBrush(color), new Rect(x, bottom - lit, width, lit), (float)unit);
            }
        }
        return bitmap;
    }
}
