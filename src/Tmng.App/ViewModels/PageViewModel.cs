using CommunityToolkit.Mvvm.ComponentModel;
using Tmng.App.Theme;
using Tmng.Core;

namespace Tmng.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(string title, string glyph, Appearance appearance)
    {
        Title = title;
        Glyph = glyph;
        Appearance = appearance;
        appearance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Appearance.Inks)) return;
            OnPropertyChanged(nameof(Inks));
            OnInksChanged();
        };
    }

    public string Title { get; }

    /// <summary>Segoe Fluent Icons code point.</summary>
    public string Glyph { get; }

    public Appearance Appearance { get; }

    public InkSet Inks => Appearance.Inks;

    /// <summary>Called on the UI thread with the newest snapshot while this page is visible.</summary>
    public virtual void Update(Snapshot snapshot) { }

    protected virtual void OnInksChanged() { }

    protected static double? Fraction(Reading reading, double max) =>
        reading.HasValue && max > 0 ? Math.Clamp(reading.Value / max, 0, 1) : null;
}

public sealed record KeyValue(string Key, string Value);

public sealed record InfoSection(string Title, IReadOnlyList<KeyValue> Items);

/// <summary>A destination not built yet; lists what it will do.</summary>
public sealed class PlaceholderViewModel(string title, string glyph, Appearance appearance, string summary, IReadOnlyList<string> plan)
    : PageViewModel(title, glyph, appearance)
{
    public string Summary { get; } = summary;
    public IReadOnlyList<string> Plan { get; } = plan;
}

public sealed class SystemInfoViewModel(Appearance appearance) : PageViewModel("System Info", "", appearance)
{
    private IReadOnlyList<InfoSection>? _sections;

    public IReadOnlyList<InfoSection> Sections => _sections ??= Platform.Windows.SystemInfo.Collect()
        .GroupBy(i => i.Section)
        .Select(g => new InfoSection(g.Key, g.Select(i => new KeyValue(i.Name, i.Value)).ToList()))
        .ToList();
}
