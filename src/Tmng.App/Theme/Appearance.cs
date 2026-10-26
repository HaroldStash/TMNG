using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Tmng.App.Theme;

public enum AppTheme { Dark, Light, Neon }

/// <summary>Which hues the graphs and meters use. Independent of the theme.</summary>
public enum ColorScheme { FullColor, Green, Amber, Blue, Mono }

/// <summary>One channel's colour plus the brushes derived from it.</summary>
public sealed class Ink
{
    public Ink(Color accent)
    {
        Accent = accent;
        Brush = new SolidColorBrush(accent);
        Dim = new SolidColorBrush(accent, 0.55);
        Panel = new SolidColorBrush(Color.FromArgb(20, accent.R, accent.G, accent.B));
        Border = new SolidColorBrush(accent, 0.32);
    }

    public Color Accent { get; }
    public IBrush Brush { get; }
    public IBrush Dim { get; }
    public IBrush Panel { get; }
    public IBrush Border { get; }
}

public sealed class InkSet
{
    public required Ink Cpu { get; init; }
    public required Ink Efficiency { get; init; }
    public required Ink Clock { get; init; }
    public required Ink Temp { get; init; }
    public required Ink Kernel { get; init; }
    public required Ink Gpu { get; init; }
    public required Ink Npu { get; init; }
    public required Ink Memory { get; init; }
    public required Ink Disk { get; init; }
    public required Ink Network { get; init; }
    public required Ink Energy { get; init; }
    public required Ink Thermal { get; init; }

    /// <summary>Single-hue schemes: one hue, so colour gradients are turned off.</summary>
    public bool Monochrome { get; init; }

    /// <summary>
    /// Light backgrounds wash out the bright accents made for dark ones, so the light theme uses
    /// the same hues darkened enough to read on white.
    /// </summary>
    public static InkSet For(ColorScheme scheme, AppTheme theme = AppTheme.Dark)
    {
        Color Tone(string hex)
        {
            var c = Color.Parse(hex);
            return theme == AppTheme.Light ? Shade(c, 0.62) : c;
        }

        return scheme switch
        {
            ColorScheme.Green => Mono(Tone("#39FF5A")),
            ColorScheme.Amber => Mono(Tone("#FFB020")),
            ColorScheme.Blue => Mono(Tone("#4DA6FF")),
            ColorScheme.Mono => Mono(theme == AppTheme.Light ? Color.Parse("#3A4250") : Color.Parse("#D8DCE2")),
            _ => new InkSet
            {
                Cpu = new(Tone("#4CFF6A")),
                Efficiency = new(Tone("#2EE6D6")),
                Clock = new(Tone("#FF5A4A")),
                Temp = new(Tone("#FFD02E")),
                Kernel = new(Tone("#FF4A4A")),
                Gpu = new(Tone("#4C8DFF")),
                Npu = new(Tone("#FF4A5E")),
                Memory = new(Tone("#C455FF")),
                Disk = new(Tone("#4CFF6A")),
                Network = new(Tone("#4C8DFF")),
                Energy = new(Tone("#FFD02E")),
                Thermal = new(Tone("#FF9F2E")),
            },
        };
    }

    /// <summary>One hue. Secondary channels are dimmer so overlaid traces stay distinct.</summary>
    private static InkSet Mono(Color c)
    {
        var bright = new Ink(c);
        var dim = new Ink(Shade(c, 0.6));
        var mid = new Ink(Shade(c, 0.8));
        return new InkSet
        {
            Cpu = bright, Efficiency = mid, Clock = mid, Temp = mid, Kernel = dim, Gpu = bright, Npu = bright,
            Memory = bright, Disk = bright, Network = bright, Energy = bright, Thermal = mid,
            Monochrome = true,
        };
    }

    private static Color Shade(Color c, double f) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
}

/// <summary>
/// Theme facts the custom-drawn controls (graphs, meters, treemap) need while rendering. They are
/// drawn outside the resource system, so they read these instead of brushes.
/// </summary>
public static class ThemeState
{
    public static AppTheme Theme { get; private set; } = AppTheme.Dark;
    public static bool Glow => Theme == AppTheme.Neon;
    public static bool IsLight => Theme == AppTheme.Light;

    public static Color PlotBackground => Theme switch
    {
        AppTheme.Light => Color.Parse("#F4F6F9"),
        AppTheme.Neon => Color.FromArgb(150, 0, 0, 0),
        _ => Color.FromArgb(90, 0, 0, 0),
    };

    public static Color GridLine => Theme switch
    {
        AppTheme.Light => Color.FromArgb(30, 20, 30, 45),
        AppTheme.Neon => Color.FromArgb(26, 140, 200, 255),
        _ => Color.FromArgb(34, 255, 255, 255),
    };

    /// <summary>Live-edge marker and replay cursor.</summary>
    public static Color Marker => IsLight ? Color.Parse("#2B3340") : Colors.White;

    /// <summary>Gap drawn between treemap tiles.</summary>
    public static Color TileGap => IsLight ? Color.Parse("#FFFFFF") : Color.FromArgb(200, 13, 16, 21);

    /// <summary>Raised on the UI thread after the theme changes, so drawn controls can repaint.</summary>
    public static event Action? Changed;

    /// <summary>Neon inherits everything it does not override from Fluent's dark theme.</summary>
    public static readonly ThemeVariant NeonVariant = new("Neon", ThemeVariant.Dark);

    internal static void Apply(AppTheme theme)
    {
        Theme = theme;
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Neon => NeonVariant,
                _ => ThemeVariant.Dark,
            };
        }
        Changed?.Invoke();
    }
}

public sealed partial class Appearance : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SchemeName))]
    private ColorScheme _scheme;

    [ObservableProperty]
    private AppTheme _theme = AppTheme.Dark;

    [ObservableProperty]
    private InkSet _inks = InkSet.For(ColorScheme.FullColor);

    public string SchemeName => NameOf(Scheme);

    public static string NameOf(ColorScheme scheme) => scheme switch
    {
        ColorScheme.FullColor => "Full colour",
        ColorScheme.Green => "Green",
        ColorScheme.Amber => "Amber",
        ColorScheme.Blue => "Blue",
        _ => "Mono",
    };

    partial void OnSchemeChanged(ColorScheme value) => Inks = InkSet.For(value, Theme);

    partial void OnThemeChanged(AppTheme value)
    {
        ThemeState.Apply(value);
        Inks = InkSet.For(Scheme, value);
    }
}
