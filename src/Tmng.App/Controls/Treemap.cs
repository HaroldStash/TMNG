using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Tmng.App.Theme;
using Tmng.Core;

namespace Tmng.App.Controls;

public sealed record TreemapItem(string Label, long Size, bool IsDirectory, object? Tag);

/// <summary>Squarified treemap: each rectangle's area is proportional to its item's size. Clicking an item runs <see cref="ItemCommand"/>.</summary>
public sealed class Treemap : Control
{
    public static readonly StyledProperty<IReadOnlyList<TreemapItem>?> ItemsProperty =
        AvaloniaProperty.Register<Treemap, IReadOnlyList<TreemapItem>?>(nameof(Items));

    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<Treemap, Color>(nameof(Accent), Colors.MediumPurple);

    public static readonly StyledProperty<ICommand?> ItemCommandProperty =
        AvaloniaProperty.Register<Treemap, ICommand?>(nameof(ItemCommand));

    private readonly List<(Rect Rect, TreemapItem Item)> _layout = [];

    static Treemap()
    {
        AffectsRender<Treemap>(ItemsProperty, AccentProperty);
    }

    public Treemap()
    {
        ClipToBounds = true;
    }

    public IReadOnlyList<TreemapItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public Color Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public ICommand? ItemCommand
    {
        get => GetValue(ItemCommandProperty);
        set => SetValue(ItemCommandProperty, value);
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

    public override void Render(DrawingContext context)
    {
        _layout.Clear();
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(ThemeState.PlotBackground), bounds);
        if (Items is not { Count: > 0 } items || bounds.Width < 4 || bounds.Height < 4) return;

        var positive = items.Where(i => i.Size > 0).ToList();
        double total = positive.Sum(i => (double)i.Size);
        if (total <= 0) return;
        double scale = bounds.Width * bounds.Height / total;
        Squarify(positive.Select(i => (i.Size * scale, i)).ToList(), bounds, _layout);

        var accent = Accent;
        var border = new Pen(new SolidColorBrush(ThemeState.TileGap), 1.5);
        var text = ThemeState.IsLight ? Brushes.Black : Brushes.White;
        var typeface = new Typeface("Inter");
        for (int n = 0; n < _layout.Count; n++)
        {
            var (rect, item) = _layout[n];
            double strength = item.IsDirectory ? 0.85 - Math.Min(0.5, n * 0.02) : 0.35;
            var fill = new SolidColorBrush(accent, strength);
            context.FillRectangle(fill, rect);
            context.DrawRectangle(null, border, rect);

            if (rect.Width < 56 || rect.Height < 18) continue;
            using (context.PushClip(rect.Deflate(3)))
            {
                var label = new FormattedText($"{item.Label}  {Fmt.Bytes(item.Size)}", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, 11.5, text);
                context.DrawText(label, rect.TopLeft + new Point(5, 3));
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hit = HitTest(e.GetPosition(this));
        ToolTip.SetTip(this, hit is null ? null : $"{hit.Label}\n{Fmt.Bytes(hit.Size)}{(hit.IsDirectory ? " · click to open" : "")}");
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (HitTest(e.GetPosition(this)) is { Tag: { } tag } && ItemCommand?.CanExecute(tag) == true)
            ItemCommand.Execute(tag);
    }

    private TreemapItem? HitTest(Point p) => _layout.FirstOrDefault(l => l.Rect.Contains(p)).Item;

    private static void Squarify(List<(double Area, TreemapItem Item)> items, Rect rect, List<(Rect, TreemapItem)> output)
    {
        int i = 0;
        while (i < items.Count && rect.Width > 0.5 && rect.Height > 0.5)
        {
            double side = Math.Min(rect.Width, rect.Height);
            double rowArea = 0, min = double.MaxValue, max = 0, worst = double.MaxValue;
            int j = i;
            while (j < items.Count)
            {
                double a = items[j].Area;
                double nextArea = rowArea + a, nextMin = Math.Min(min, a), nextMax = Math.Max(max, a);
                double ratio = Math.Max(side * side * nextMax / (nextArea * nextArea), nextArea * nextArea / (side * side * nextMin));
                if (j > i && ratio > worst) break;
                rowArea = nextArea; min = nextMin; max = nextMax; worst = ratio;
                j++;
            }

            if (rect.Width >= rect.Height)
            {
                double width = rowArea / rect.Height, y = rect.Y;
                for (int k = i; k < j; k++)
                {
                    double h = items[k].Area / width;
                    output.Add((new Rect(rect.X, y, width, h), items[k].Item));
                    y += h;
                }
                rect = new Rect(rect.X + width, rect.Y, Math.Max(0, rect.Width - width), rect.Height);
            }
            else
            {
                double height = rowArea / rect.Width, x = rect.X;
                for (int k = i; k < j; k++)
                {
                    double w = items[k].Area / height;
                    output.Add((new Rect(x, rect.Y, w, height), items[k].Item));
                    x += w;
                }
                rect = new Rect(rect.X, rect.Y + height, rect.Width, Math.Max(0, rect.Height - height));
            }
            i = j;
        }
    }
}
