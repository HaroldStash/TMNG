using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tmng.App.Theme;
using Tmng.Core;

namespace Tmng.App.ViewModels;

public sealed record CaptureOption(int Minutes)
{
    public override string ToString() => Minutes == 0 ? "Nothing after" : $"{Minutes} min after";
}

public sealed record MatchOption(MatchMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One fired alert in the history list.</summary>
public sealed partial class AlertRow(AlertRecord record, Func<string, Task> openTrace) : ObservableObject
{
    public AlertRecord Record { get; } = record;
    public string Time => Record.Time.Date == DateTime.Today ? Record.Time.ToString("T") : Record.Time.ToString("g");
    public string Rule => Record.Rule;
    public string Reason => Record.Reason;
    public string Top => Record.TopProcess ?? "—";
    public string Extremes => string.Join(" · ", Record.Extremes);

    public string Status =>
        Record.Path is not null ? "Trace saved"
        : Record.Error is { } error ? $"Not saved: {error}"
        : $"Capturing until {Record.CaptureUntil:T}";

    public bool CanOpen => Record.Path is not null;

    /// <summary>Re-reads the record after its trace has been saved.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Extremes));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(CanOpen));
    }

    [RelayCommand]
    private async Task Open()
    {
        if (Record.Path is { } path) await openTrace(path);
    }
}

/// <summary>One condition being edited: metric, above or below, and the value.</summary>
public sealed partial class ConditionRow : ObservableObject
{
    private readonly Action _changed;
    private readonly Action<ConditionRow> _remove;
    private MetricInfo _metric;

    public ConditionRow(AlertCondition condition, MetricInfo metric, Action changed, Action<ConditionRow> remove)
    {
        Condition = condition;
        _metric = metric;
        _changed = changed;
        _remove = remove;
    }

    public AlertCondition Condition { get; }

    public MetricInfo? Metric
    {
        get => _metric;
        set
        {
            // The picker sends null while its list is being replaced; that is not a choice.
            if (value is null || value == _metric) return;
            _metric = value;
            Condition.Metric = value.Key;
            Condition.Label = value.Label;
            Condition.Unit = value.Unit;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Unit));
            _changed();
        }
    }

    public string Unit => Condition.Unit;

    public Comparison Comparison
    {
        get => Condition.Comparison;
        set { Condition.Comparison = value; OnPropertyChanged(); _changed(); }
    }

    public decimal? Threshold
    {
        get => (decimal)Condition.Threshold;
        set { if (value is { } v) Condition.Threshold = (double)v; OnPropertyChanged(); _changed(); }
    }

    [ObservableProperty] private string _nowText = "";
    [ObservableProperty] private bool _canRemove;

    /// <summary>Points the picker at the current catalog entry without counting as an edit.</summary>
    public void Resolve(MetricInfo metric)
    {
        if (metric == _metric) return;
        _metric = metric;
        OnPropertyChanged(nameof(Metric));
    }

    public void ShowNow(IReadOnlyDictionary<string, double?> values) =>
        NowText = values.TryGetValue(Condition.Metric, out var v) && v is { } x
            ? $"now {x:0.#} {Condition.Unit}".TrimEnd()
            : "not measured now";

    [RelayCommand]
    private void Remove() => _remove(this);
}

/// <summary>One rule: its conditions, all or any, and how long they must hold.</summary>
public sealed partial class RuleRow : ObservableObject
{
    private readonly AlertsViewModel _owner;

    public RuleRow(AlertRule rule, AlertsViewModel owner)
    {
        Rule = rule;
        _owner = owner;
        foreach (var condition in rule.Conditions) Conditions.Add(NewRow(condition));
        UpdateRemovable();
    }

    public AlertRule Rule { get; }
    public ObservableCollection<ConditionRow> Conditions { get; } = [];
    public bool CanDelete => !Rule.BuiltIn;
    public string Title => Rule.Title;
    public string Summary => Rule.Describe();
    /// <summary>An unnamed rule's title already is its summary.</summary>
    public bool HasName => !string.IsNullOrWhiteSpace(Rule.Name);
    public string EditLabel => IsEditing ? "Done" : "Edit";

    [ObservableProperty] private bool _isEditing;
    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(EditLabel));

    public bool Enabled
    {
        get => Rule.Enabled;
        set { Rule.Enabled = value; OnPropertyChanged(); Changed(); }
    }

    public string Name
    {
        get => Rule.Name;
        set { Rule.Name = value.Trim(); OnPropertyChanged(); Changed(); }
    }

    public decimal? Seconds
    {
        get => Rule.Seconds;
        set { if (value is { } v) Rule.Seconds = Math.Max(1, (int)v); OnPropertyChanged(); Changed(); }
    }

    public MatchOption Match
    {
        get => _owner.Matches.First(m => m.Mode == Rule.Match);
        set { if (value is null) return; Rule.Match = value.Mode; OnPropertyChanged(); Changed(); }
    }

    [RelayCommand]
    private void ToggleEdit() => IsEditing = !IsEditing;

    [RelayCommand]
    private void AddCondition()
    {
        var metric = AlertMetrics.System[0];
        var condition = new AlertCondition { Metric = metric.Key, Label = metric.Label, Unit = metric.Unit, Threshold = 90 };
        Conditions.Add(NewRow(condition));
        UpdateRemovable();
        Changed();
    }

    [RelayCommand]
    private void Delete() => _owner.Delete(this);

    public void ShowNow(IReadOnlyDictionary<string, double?> values)
    {
        foreach (var condition in Conditions) condition.ShowNow(values);
    }

    public void ResolveMetrics(AlertsViewModel owner)
    {
        foreach (var condition in Conditions) condition.Resolve(owner.MetricFor(condition.Condition));
    }

    private ConditionRow NewRow(AlertCondition condition) => new(condition, _owner.MetricFor(condition), Changed, row =>
    {
        Conditions.Remove(row);
        UpdateRemovable();
        Changed();
    });

    private void UpdateRemovable()
    {
        foreach (var condition in Conditions) condition.CanRemove = Conditions.Count > 1;
    }

    private void Changed()
    {
        // A new list, never an edit in place: the sampling thread may be reading the old one.
        Rule.Conditions = Conditions.Select(c => c.Condition).ToList();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasName));
        _owner.Save();
    }
}

/// <summary>Alert rules on any metric, how much to capture after one fires, and the history of fired alerts.</summary>
public sealed partial class AlertsViewModel : PageViewModel
{
    private const int HistoryLimit = 200;
    private static readonly string HistoryPath = Path.Combine(AppSettings.Folder, "alerts.json");

    private readonly FlightRecorder _recorder;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, MetricInfo> _catalog = AlertMetrics.System.ToDictionary(m => m.Key);

    [ObservableProperty] private CaptureOption _capture;
    [ObservableProperty] private IReadOnlyList<MetricInfo> _metricChoices = [];

    public AlertsViewModel(FlightRecorder recorder, AppSettings settings, Appearance appearance, Func<string, Task> openTrace)
        : base("Alerts", "", appearance)
    {
        _recorder = recorder;
        _settings = settings;
        _capture = CaptureOptions.FirstOrDefault(o => o.Minutes == settings.AlertCaptureMinutes) ?? CaptureOptions[1];
        recorder.CaptureAfter = TimeSpan.FromMinutes(_capture.Minutes);

        foreach (var rule in recorder.Rules) Rules.Add(new RuleRow(rule, this));
        RebuildChoices();

        foreach (var record in LoadHistory()) History.Add(new AlertRow(record, openTrace));
        recorder.AlertFired += record => Dispatcher.UIThread.Post(() =>
        {
            History.Insert(0, new AlertRow(record, openTrace));
            while (History.Count > HistoryLimit) History.RemoveAt(History.Count - 1);
            SaveHistory();
        });
        recorder.AlertSaved += record => Dispatcher.UIThread.Post(() =>
        {
            History.FirstOrDefault(r => r.Record == record)?.Refresh();
            SaveHistory();
        });
    }

    public ObservableCollection<RuleRow> Rules { get; } = [];
    public ObservableCollection<AlertRow> History { get; } = [];
    public IReadOnlyList<CaptureOption> CaptureOptions { get; } = [new(0), new(1), new(2), new(5)];
    public IReadOnlyList<MatchOption> Matches { get; } = [new(MatchMode.All, "all"), new(MatchMode.Any, "any")];
    public IReadOnlyList<Comparison> Comparisons { get; } = Enum.GetValues<Comparison>();

    public string BufferText => $"{_recorder.Window.TotalMinutes:0} minutes";

    public bool Notifications
    {
        get => _settings.Notifications;
        set { _settings.Notifications = value; _settings.Save(); OnPropertyChanged(); }
    }

    partial void OnCaptureChanged(CaptureOption value)
    {
        _recorder.CaptureAfter = TimeSpan.FromMinutes(value.Minutes);
        _settings.AlertCaptureMinutes = value.Minutes;
        _settings.Save();
    }

    public override void Update(Snapshot snapshot)
    {
        bool changed = false;
        foreach (var metric in AlertMetrics.Sensors(snapshot))
        {
            if (_catalog.TryGetValue(metric.Key, out var known) && known == metric) continue;
            // New, or a placeholder for a saved condition whose sensor has started reporting.
            _catalog[metric.Key] = metric;
            changed = true;
        }
        if (changed)
        {
            RebuildChoices();
            foreach (var rule in Rules) rule.ResolveMetrics(this);
        }

        var values = AlertMetrics.Read(snapshot);
        foreach (var rule in Rules) rule.ShowNow(values);
    }

    /// <summary>
    /// The catalog entry for a saved condition. A sensor not reporting right now (not elevated,
    /// device unplugged) still gets an entry, so the condition shows what it watches.
    /// </summary>
    internal MetricInfo MetricFor(AlertCondition condition)
    {
        if (_catalog.TryGetValue(condition.Metric, out var known)) return known;
        var missing = new MetricInfo(condition.Metric, condition.Label, "Not reporting now", condition.Unit);
        _catalog[condition.Metric] = missing;
        return missing;
    }

    private void RebuildChoices() =>
        MetricChoices = _catalog.Values
            .OrderBy(m => m.Group == "System" ? 0 : 1)
            .ThenBy(m => m.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    [RelayCommand]
    private void AddRule()
    {
        var metric = AlertMetrics.System[0];
        var rule = new AlertRule
        {
            Enabled = true,
            Conditions = [new AlertCondition { Metric = metric.Key, Label = metric.Label, Unit = metric.Unit, Threshold = 90 }],
        };
        Rules.Add(new RuleRow(rule, this) { IsEditing = true });
        Save();
    }

    internal void Delete(RuleRow row)
    {
        if (row.Rule.BuiltIn) return;
        Rules.Remove(row);
        Save();
    }

    /// <summary>Hands the edited rules to the recorder as a new list and saves them.</summary>
    internal void Save()
    {
        var rules = Rules.Select(r => r.Rule).ToList();
        _recorder.Rules = rules;
        _settings.SaveAlertRules(rules);
    }

    [RelayCommand]
    private void ClearHistory()
    {
        // Alerts still capturing stay, so their saved trace still has a row to land on.
        foreach (var row in History.Where(r => !r.Record.Capturing).ToList()) History.Remove(row);
        SaveHistory();
    }

    private static List<AlertRecord> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
                return JsonSerializer.Deserialize<List<AlertRecord>>(File.ReadAllText(HistoryPath))?
                    .Where(r => !r.Capturing) // a capture cut off by a crash will never finish
                    .ToList() ?? [];
        }
        catch (Exception)
        {
            // A damaged history starts empty rather than blocking startup.
        }
        return [];
    }

    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(History.Select(r => r.Record).ToList()));
        }
        catch (Exception)
        {
            // History is a convenience; the traces themselves are already saved.
        }
    }
}
