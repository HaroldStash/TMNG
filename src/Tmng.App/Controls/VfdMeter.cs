using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tmng.App.Theme;

namespace Tmng.App.Controls;

/// <summary>
/// Segmented vacuum-fluorescent style bar. A null value draws only unlit segments; any value
/// above zero lights at least one segment, and an exact zero lights none.
/// </summary>
public sealed class VfdMeter : Control
{
    public static readonly StyledProperty<double?> ValueProperty =
        AvaloniaProperty.Register<VfdMeter, double?>(nameof(Value));

    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<VfdMeter, Color>(nameof(Accent), Colors.LimeGreen);

    public static readonly StyledProperty<int> SegmentsProperty =
        AvaloniaProperty.Register<VfdMeter, int>(nameof(Segments), 24);

    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<VfdMeter, Orientation>(nameof(Orientation), Orientation.Vertical);

    /// <summary>Optional colour at the middle and end of the bar; segments blend from <see cref="Accent"/> through them.</summary>
    public static readonly StyledProperty<Color?> GradientMidProperty =
        AvaloniaProperty.Register<VfdMeter, Color?>(nameof(GradientMid));

    public static readonly StyledProperty<Color?> GradientEndProperty =
        AvaloniaProperty.Register<VfdMeter, Color?>(nameof(GradientEnd));

    static VfdMeter()
    {
        AffectsRender<VfdMeter>(ValueProperty, AccentProperty, SegmentsProperty, OrientationProperty, GradientMidProperty, GradientEndProperty);
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

    public Color? GradientMid
    {
        get => GetValue(GradientMidProperty);
        set => SetValue(GradientMidProperty, value);
    }

    public Color? GradientEnd
    {
        get => GetValue(GradientEndProperty);
        set => SetValue(GradientEndProperty, value);
    }

    private Color SegmentColor(int index, int count)
    {
        if (GradientEnd is not { } end) return Accent;
        double t = count <= 1 ? 0 : (double)index / (count - 1);
        var mid = GradientMid ?? Blend(Accent, end, 0.5);
        return t < 0.5 ? Blend(Accent, mid, t * 2) : Blend(mid, end, (t - 0.5) * 2);
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Fill fraction, 0 to 1.</summary>
    public double? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Color Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public int Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        const double gap = 3;
        int count = Math.Max(1, Segments);
        bool vertical = Orientation == Orientation.Vertical;
        double length = vertical ? Bounds.Height : Bounds.Width;
        double thickness = vertical ? Bounds.Width : Bounds.Height;
        double segment = (length - gap * (count - 1)) / count;
        if (segment < 1 || thickness < 1) return;

        int lit = Value is { } v ? (v <= 0 ? 0 : Math.Max(1, (int)Math.Round(Math.Clamp(v, 0, 1) * count))) : 0;

        for (int i = 0; i < count; i++)
        {
            var accent = SegmentColor(i, count);
            var unlit = new SolidColorBrush(accent, Value is null ? 0.06 : ThemeState.IsLight ? 0.16 : 0.13);
            var body = new SolidColorBrush(accent, ThemeState.Glow ? 0.92 : 1);

            var rect = vertical
                ? new Rect(0, length - (i + 1) * segment - i * gap, thickness, segment)
                : new Rect(i * (segment + gap), 0, segment, thickness);

            if (i < lit)
            {
                // Light and Dark: flat segments. Neon: a bloom that bleeds into neighbours and a lit core.
                if (ThemeState.Glow)
                {
                    context.FillRectangle(new SolidColorBrush(accent, 0.10), rect.Inflate(6), 6);
                    context.FillRectangle(new SolidColorBrush(accent, 0.26), rect.Inflate(3), 4);
                }
                context.FillRectangle(body, rect, 1.5f);
                if (ThemeState.Glow)
                {
                    var inset = vertical ? new Thickness(thickness * 0.18, segment * 0.3) : new Thickness(segment * 0.3, thickness * 0.18);
                    context.FillRectangle(new SolidColorBrush(Lighten(accent, 0.7)), rect.Deflate(inset), 1);
                }
            }
            else
            {
                context.FillRectangle(unlit, rect, 1.5f);
            }
        }
    }

    private static Color Lighten(Color c, double f) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));
}
