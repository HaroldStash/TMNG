using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Tmng.App.Theme;
using Tmng.Core;

namespace Tmng.App.Controls;

/// <summary>One trace on a <see cref="TraceGraph"/>, scaled from Min..Max to the plot height.</summary>
public sealed class TraceLine(MetricSeries series, double min, double max, bool fill = false)
{
    public MetricSeries Series { get; } = series;
    public double Min { get; set; } = min;
    public double Max { get; set; } = max;
    public bool Fill { get; } = fill;
    public Color Color { get; set; } = Colors.LimeGreen;
}

/// <summary>
/// Phosphor-style history plot. Each run of consecutive samples is its own figure, so a
/// missing sample breaks the trace instead of being bridged by a straight line.
/// </summary>
public sealed class TraceGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<TraceLine>?> LinesProperty =
        AvaloniaProperty.Register<TraceGraph, IReadOnlyList<TraceLine>?>(nameof(Lines));

    public static readonly StyledProperty<long> GenerationProperty =
        AvaloniaProperty.Register<TraceGraph, long>(nameof(Generation));

    private double[] _buffer = [];

    static TraceGraph()
    {
        AffectsRender<TraceGraph>(LinesProperty, GenerationProperty, CursorProperty);
    }

    public TraceGraph()
    {
        ClipToBounds = true;
    }

    public IReadOnlyList<TraceLine>? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    /// <summary>Bump to redraw after new samples arrive.</summary>
    public long Generation
    {
        get => GetValue(GenerationProperty);
        set => SetValue(GenerationProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeState.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeState.Changed -= InvalidateVisual;
    }

    /// <summary>Live graphs mark the newest sample; replay graphs do not.</summary>
    public bool ShowEdgeMarker { get; set; } = true;

    public override void Render(DrawingContext context)
    {
        var area = new Rect(Bounds.Size);
        if (area.Width < 4 || area.Height < 4) return;

        context.FillRectangle(new SolidColorBrush(ThemeState.PlotBackground), area);
        var gridPen = new Pen(new SolidColorBrush(ThemeState.GridLine), 1);
        for (int i = 1; i < 12; i++)
        {
            double x = Math.Round(area.Width * i / 12) + 0.5;
            context.DrawLine(gridPen, new Point(x, 0), new Point(x, area.Height));
        }
        for (int i = 1; i < 4; i++)
        {
            double y = Math.Round(area.Height * i / 4) + 0.5;
            context.DrawLine(gridPen, new Point(0, y), new Point(area.Width, y));
        }

        if (Lines is { } lines)
            foreach (var line in lines) DrawTrace(context, line, area);

        if (Cursor is { } cursor)
        {
            double x = Math.Round(Math.Clamp(cursor, 0, 1) * (area.Width - 1)) + 0.5;
            context.DrawLine(new Pen(new SolidColorBrush(ThemeState.Marker, 0.85), 1), new Point(x, 0), new Point(x, area.Height));
        }

        context.DrawRectangle(null, gridPen, area.Deflate(0.5));
    }

    public static readonly StyledProperty<double?> CursorProperty =
        AvaloniaProperty.Register<TraceGraph, double?>(nameof(Cursor));

    /// <summary>Replay position as a fraction of the width; null hides the cursor.</summary>
    public double? Cursor
    {
        get => GetValue(CursorProperty);
        set => SetValue(CursorProperty, value);
    }

    private void DrawTrace(DrawingContext context, TraceLine line, Rect area)
    {
        int n = line.Series.Capacity;
        double span = line.Max - line.Min;
        if (n < 2 || span <= 0) return;
        if (_buffer.Length != n) _buffer = new double[n];
        line.Series.CopyTo(_buffer);

        double step = area.Width / (n - 1);
        double plotHeight = area.Height - 3;
        Point Map(int i) => new(i * step, area.Height - 1.5 - Math.Clamp((_buffer[i] - line.Min) / span, 0, 1) * plotHeight);

        var stroke = new StreamGeometry();
        var fill = line.Fill ? new StreamGeometry() : null;
        Point? last = null;
        using (var s = stroke.Open())
        using (var f = fill?.Open())
        {
            bool open = false;
            for (int i = 0; i <= n; i++)
            {
                bool gap = i == n || double.IsNaN(_buffer[i]);
                if (!gap)
                {
                    var p = Map(i);
                    if (!open)
                    {
                        s.BeginFigure(p, false);
                        f?.BeginFigure(new Point(p.X, area.Height), true);
                        f?.LineTo(p);
                        open = true;
                    }
                    else
                    {
                        s.LineTo(p);
                        f?.LineTo(p);
                    }
                    last = p;
                }
                else if (open)
                {
                    s.EndFigure(false);
                    f?.LineTo(new Point(last!.Value.X, area.Height));
                    f?.EndFigure(true);
                    open = false;
                }
            }
        }

        var c = line.Color;
        if (fill is not null)
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            };
            byte top = ThemeState.Glow ? (byte)110 : ThemeState.IsLight ? (byte)60 : (byte)80;
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(top, c.R, c.G, c.B), 0));
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(8, c.R, c.G, c.B), 1));
            context.DrawGeometry(gradient, null, fill);
        }

        if (ThemeState.Glow)
        {
            // Bloom: widening, fading strokes under a core lightened toward white, like a lit phosphor trace.
            context.DrawGeometry(null, new Pen(new SolidColorBrush(c, 0.06), 14, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), stroke);
            context.DrawGeometry(null, new Pen(new SolidColorBrush(c, 0.13), 8, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), stroke);
            context.DrawGeometry(null, new Pen(new SolidColorBrush(c, 0.32), 4, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), stroke);
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Lighten(c, 0.35)), 1.8, lineJoin: PenLineJoin.Round), stroke);
        }
        else
        {
            // Light and Dark are flat: one clean stroke, no halo.
            context.DrawGeometry(null, new Pen(new SolidColorBrush(c), 1.8, lineJoin: PenLineJoin.Round), stroke);
        }

        // Live-edge marker only when the newest sample exists.
        if (ShowEdgeMarker && !double.IsNaN(_buffer[n - 1]))
        {
            var tip = Map(n - 1);
            var marker = new StreamGeometry();
            using (var m = marker.Open())
            {
                m.BeginFigure(new Point(area.Width, tip.Y - 5), true);
                m.LineTo(new Point(area.Width - 7, tip.Y));
                m.LineTo(new Point(area.Width, tip.Y + 5));
                m.EndFigure(true);
            }
            context.DrawGeometry(new SolidColorBrush(ThemeState.Marker, 0.9), null, marker);
        }
    }

    private static Color Lighten(Color c, double f) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));
}
