using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Tmng.App.Theme;

namespace Tmng.App.Controls;

/// <summary>
/// <c>c:Glow.Text="True"</c> gives a TextBlock a halo in its own foreground colour under the Neon
/// theme, and nothing under the others. Usable from styles, so a class like <c>value</c> can opt in.
/// </summary>
public static class Glow
{
    public static readonly AttachedProperty<bool> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("Text", typeof(Glow));

    /// <summary>Halo size in pixels; large readings use the default, small labels less.</summary>
    public static readonly AttachedProperty<double> RadiusProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, double>("Radius", typeof(Glow), 14);

    public static bool GetText(TextBlock element) => element.GetValue(TextProperty);
    public static void SetText(TextBlock element, bool value) => element.SetValue(TextProperty, value);
    public static double GetRadius(TextBlock element) => element.GetValue(RadiusProperty);
    public static void SetRadius(TextBlock element, double value) => element.SetValue(RadiusProperty, value);

    static Glow()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((block, e) =>
        {
            if (e.GetNewValue<bool>())
            {
                block.AttachedToVisualTree += OnAttached;
                block.DetachedFromVisualTree += OnDetached;
                if (TopLevel.GetTopLevel(block) is not null) Track(block);
            }
            else
            {
                block.AttachedToVisualTree -= OnAttached;
                block.DetachedFromVisualTree -= OnDetached;
                Untrack(block);
                block.Effect = null;
            }
        });
        TextBlock.ForegroundProperty.Changed.AddClassHandler<TextBlock>((block, _) =>
        {
            if (GetText(block)) Update(block);
        });
        RadiusProperty.Changed.AddClassHandler<TextBlock>((block, _) =>
        {
            if (GetText(block)) Update(block);
        });
        ThemeState.Changed += () =>
        {
            foreach (var block in Tracked.ToList()) Update(block);
        };
    }

    private static readonly HashSet<TextBlock> Tracked = [];

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBlock block) Track(block);
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBlock block) Untrack(block);
    }

    private static void Track(TextBlock block)
    {
        Tracked.Add(block);
        Update(block);
    }

    private static void Untrack(TextBlock block) => Tracked.Remove(block);

    private static void Update(TextBlock block)
    {
        if (!ThemeState.Glow || block.Foreground is not ISolidColorBrush brush)
        {
            block.Effect = null;
            return;
        }
        var c = brush.Color;
        block.Effect = new DropShadowEffect
        {
            OffsetX = 0,
            OffsetY = 0,
            BlurRadius = GetRadius(block),
            Color = Color.FromArgb(255, c.R, c.G, c.B),
            Opacity = 0.9,
        };
    }
}
