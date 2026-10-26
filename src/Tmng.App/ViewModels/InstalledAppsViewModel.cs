using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tmng.App.Theme;
using Tmng.Core;
using Tmng.Platform.Windows;

namespace Tmng.App.ViewModels;

public sealed partial class AppRow : ObservableObject
{
    public AppRow(InstalledApp app)
    {
        App = app;
        if (app.EstimatedBytes is { } estimate)
        {
            SizeValue = estimate;
            SizeText = $"{Fmt.Bytes(estimate)} (est.)";
        }
    }

    public InstalledApp App { get; }
    public string Name => App.Name;
    public string Publisher => App.Publisher ?? "—";
    public string Version => App.Version ?? "—";
    public string Installed => App.InstalledOn?.ToString("d") ?? "—";
    public string Source => App.Source == AppSource.Store ? "Store / MSIX" : "Desktop";
    public IReadOnlyList<string> Programs { get; set; } = [];

    [ObservableProperty] private string _sizeText = "—";
    [ObservableProperty] private long _sizeValue = -1;
    [ObservableProperty] private string _lastUsed = "—";
    [ObservableProperty] private DateTime _lastUsedValue;
    [ObservableProperty] private string _upgrade = "";
    public string? WingetId { get; set; }
}

public sealed partial class InstalledAppsViewModel : ActionPageViewModel
{
    private readonly ObservableCollection<AppRow> _items = [];
    private bool _loaded;

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _showComponents;
    [ObservableProperty] private AppRow? _selected;
    [ObservableProperty] private string _summary = "Loading…";

    public InstalledAppsViewModel(Appearance appearance) : base("Installed Apps", "", appearance)
    {
        View = new DataGridCollectionView(_items) { Filter = o => Matches((AppRow)o) };
        View.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(AppRow.SizeValue), ListSortDirection.Descending));
    }

    public DataGridCollectionView View { get; }

    partial void OnFilterChanged(string value) => View.Refresh();
    partial void OnShowComponentsChanged(bool value) => View.Refresh();

    private bool Matches(AppRow row)
    {
        if (!ShowComponents && row.App.IsWindowsComponent) return false;
        if (string.IsNullOrWhiteSpace(Filter)) return true;
        var f = Filter.Trim();
        return row.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || row.Publisher.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    public override void Update(Snapshot snapshot)
    {
        if (_loaded) return;
        _loaded = true;
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task Reload()
    {
        _items.Clear();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Summary = "Reading installed apps…";
        var apps = await Task.Run(InstalledAppProvider.Read);
        var rows = apps.Select(a => new AppRow(a)).ToList();
        foreach (var row in rows) _items.Add(row);
        Summary = $"{apps.Count} apps · measuring install folders…";

        // Measure real folder sizes in the background; registry estimates show until then.
        var prefetch = await Task.Run(InstalledAppProvider.ReadPrefetch);
        await Parallel.ForEachAsync(rows.Where(r => r.App.InstallLocation is not null), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (row, _) =>
        {
            var measured = InstalledAppProvider.Measure(row.App.InstallLocation!);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (measured is { } m)
                {
                    row.SizeValue = m.Bytes;
                    row.SizeText = Fmt.Bytes(m.Bytes);
                    row.Programs = m.Programs;
                }
                ApplyLastUsed(row, prefetch);
            });
        });
        foreach (var row in rows.Where(r => r.App.InstallLocation is null)) ApplyLastUsed(row, prefetch);

        View.Refresh();
        int measuredCount = rows.Count(r => r.SizeValue >= 0);
        Summary = $"{apps.Count} apps · {Fmt.Bytes(rows.Where(r => r.SizeValue > 0).Sum(r => r.SizeValue))} across {measuredCount} with a known size"
            + (prefetch is null ? " · last-used dates need administrator rights" : "");
    }

    private static void ApplyLastUsed(AppRow row, Dictionary<string, DateTime>? prefetch)
    {
        if (prefetch is null)
        {
            row.LastUsed = "admin";
            return;
        }
        var times = row.Programs.Select(p => prefetch.TryGetValue(p, out var t) ? t : (DateTime?)null).Where(t => t is not null).ToList();
        if (times.Count == 0)
        {
            row.LastUsed = row.Programs.Count == 0 ? "—" : "Not in Prefetch";
            return;
        }
        var last = times.Max()!.Value;
        row.LastUsedValue = last;
        row.LastUsed = last.ToString("d");
    }

    [RelayCommand]
    private async Task CheckUpgrades()
    {
        Busy = true;
        ActionMessage = "Asking winget for available upgrades…";
        try
        {
            var upgrades = await Task.Run(InstalledAppProvider.ReadUpgrades);
            int matched = 0;
            foreach (var row in _items)
            {
                var match = upgrades.FirstOrDefault(u => u.Name.Length > 0 &&
                    (row.Name.Equals(u.Name, StringComparison.OrdinalIgnoreCase) || row.Name.StartsWith(u.Name, StringComparison.OrdinalIgnoreCase)));
                row.Upgrade = match is null ? "" : $"{match.Version} → {match.Available}";
                row.WingetId = match?.Id;
                if (match is not null) matched++;
            }
            ActionMessage = $"winget reports {upgrades.Count} upgrade(s); {matched} matched to apps in this list.";
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task Upgrade()
    {
        if (Selected is not { } row) return;
        if (row.WingetId is not { } id)
        {
            ActionMessage = "No winget upgrade is known for this app. Run Check for upgrades first.";
            return;
        }
        await RunAsync(() => InstalledAppProvider.Upgrade(id));
    }

    [RelayCommand]
    private async Task Uninstall()
    {
        if (Selected is not { } row) return;
        if (!await Confirm("Uninstall app", $"Uninstall {row.Name}?", "Uninstall")) return;
        Busy = true;
        ActionMessage = $"Running the {row.Name} uninstaller…";
        try
        {
            ActionMessage = (await InstalledAppProvider.UninstallAsync(row.App)).Message;
        }
        catch (Exception ex)
        {
            ActionMessage = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Reveal()
    {
        if (Selected is { } row) ActionMessage = ShellActions.Reveal(row.App.InstallLocation).Message;
    }
}
