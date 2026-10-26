using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tmng.App.Theme;
using Tmng.Core;
using Tmng.Platform.Windows;

namespace Tmng.App.ViewModels;

/// <summary>A row in the process list: an app group or one of the processes inside it.</summary>
public abstract partial class ProcessListRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private Bitmap? _icon;
    [ObservableProperty] private string _user = "—";
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private double _cpuValue;
    [ObservableProperty] private string _memoryText = "";
    [ObservableProperty] private long _memoryValue;
    [ObservableProperty] private string _gpuText = "—";
    [ObservableProperty] private double _gpuValue;
    [ObservableProperty] private string _npuText = "—";
    [ObservableProperty] private double _npuValue;
    [ObservableProperty] private string _ioText = "—";
    [ObservableProperty] private double _ioValue;
    [ObservableProperty] private string _diskText = "—";
    [ObservableProperty] private double _diskValue;
    [ObservableProperty] private string _networkText = "—";
    [ObservableProperty] private double _networkValue;
    [ObservableProperty] private int _threads;

    public abstract int Pid { get; }

    /// <summary>The group this row sorts under. A group row is its own group.</summary>
    public abstract ProcessGroupRow? Group { get; }

    /// <summary>Space before the icon, so processes sit one step in from their group.</summary>
    public abstract double Indent { get; }

    /// <summary>Set once the row has left the list, so a stale selection shows nothing.</summary>
    public bool Removed { get; set; }

    protected void Show(Reading cpu, long memory, Reading gpu, Reading npu, Reading io, Reading disk, Reading network, int threads)
    {
        // Percentages sort at the precision shown, so rows that read the same keep a steady order
        // instead of shuffling on differences nobody can see.
        CpuText = Fmt.Percent(cpu);
        CpuValue = SortKey(cpu.Map(v => Math.Round(v, 1)));
        MemoryText = Fmt.Bytes(memory);
        MemoryValue = memory;
        GpuText = Fmt.Percent(gpu, 0);
        GpuValue = SortKey(gpu.Map(Math.Round));
        NpuText = Fmt.Percent(npu, 0);
        NpuValue = SortKey(npu.Map(Math.Round));
        IoText = Fmt.Rate(io);
        IoValue = SortKey(io);
        DiskText = Fmt.Rate(disk);
        DiskValue = SortKey(disk);
        NetworkText = Fmt.Rate(network);
        NetworkValue = SortKey(network);
        Threads = threads;
    }

    /// <summary>Missing readings sort below a measured zero.</summary>
    private static double SortKey(Reading r) => r.HasValue ? r.Value : -1;
}

public sealed partial class ProcessRow : ProcessListRow
{
    public ProcessRow(ProcessSample sample)
    {
        Identity = sample.Identity;
        ExeName = sample.Name;
        Name = sample.DisplayName;
        Sample = sample;
        Update(sample);
        _ = LoadIcon(sample.ImagePath);
    }

    public ProcessIdentity Identity { get; }
    public override int Pid => Identity.Pid;
    public string ExeName { get; }
    public ProcessSample Sample { get; private set; }
    public ProcessGroupRow? Owner { get; set; }
    public override ProcessGroupRow? Group => Owner;
    public override double Indent => 20;

    public Reading Io { get; private set; }
    public Reading Disk { get; private set; }
    public Reading Network { get; private set; }

    public void Update(ProcessSample s)
    {
        Sample = s;
        User = s.UserName ?? "—";
        Io = s.IoReadBytesPerSec.HasValue && s.IoWriteBytesPerSec.HasValue
            ? Reading.Of(s.IoReadBytesPerSec.Value + s.IoWriteBytesPerSec.Value)
            : default;
        Disk = Sum(s.DiskReadBytesPerSec, s.DiskWriteBytesPerSec);
        Network = Sum(s.NetReceiveBytesPerSec, s.NetSendBytesPerSec);
        Show(s.Cpu, s.PrivateWorkingSet, s.Gpu, s.Npu, Io, Disk, Network, s.Threads);
    }

    private async Task LoadIcon(string? path) => Icon = await ProcessIcons.Get(path);

    private static Reading Sum(Reading a, Reading b) =>
        a.HasValue && b.HasValue ? Reading.Of(a.Value + b.Value) : a.HasValue ? b : a;
}

/// <summary>
/// An app and every process grouped under it (see <see cref="ProcessGroups"/>). A group of one
/// shows that process; a larger group shows totals and expands to list its processes.
/// </summary>
public sealed partial class ProcessGroupRow : ProcessListRow
{
    private readonly Action _expandedChanged;

    public ProcessGroupRow(ProcessRow root, Action expandedChanged)
    {
        Root = root;
        Members = [root];
        _expandedChanged = expandedChanged;
    }

    public ProcessRow Root { get; }
    public IReadOnlyList<ProcessRow> Members { get; private set; }
    public override int Pid => Root.Pid;
    public override ProcessGroupRow Group => this;
    public override double Indent => 0;

    [ObservableProperty] private bool _isExpandable;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _countText = "";

    partial void OnIsExpandedChanged(bool value) => _expandedChanged();

    /// <summary>Recomputes the totals. Returns true when the set of member processes changed.</summary>
    public bool SetMembers(List<ProcessRow> members)
    {
        bool changed = members.Count != Members.Count || members.Except(Members).Any();
        Members = members;
        IsExpandable = members.Count > 1;
        CountText = IsExpandable ? $"({members.Count})" : "";
        Name = Root.Name;
        Icon = Root.Icon;
        User = Root.User;
        Show(
            Total(r => r.Sample.Cpu, cap: 100),
            members.Sum(r => r.Sample.PrivateWorkingSet),
            Total(r => r.Sample.Gpu, cap: 100),
            Total(r => r.Sample.Npu, cap: 100),
            Total(r => r.Io),
            Total(r => r.Disk),
            Total(r => r.Network),
            members.Sum(r => r.Sample.Threads));
        return changed;
    }

    /// <summary>A total is only as known as its least-known part: one missing member makes it missing.</summary>
    private Reading Total(Func<ProcessRow, Reading> pick, double cap = double.MaxValue)
    {
        double sum = 0;
        foreach (var member in Members)
        {
            var reading = pick(member);
            if (!reading.HasValue) return reading;
            sum += reading.Value;
        }
        return Reading.Of(Math.Min(sum, cap));
    }
}

/// <summary>
/// Sorts groups by the chosen column and keeps each group's processes directly under it, sorted
/// the same way. The DataGrid's own path sort would scatter them through the list.
/// </summary>
internal sealed class GroupedSort(string path, ListSortDirection direction, Func<ProcessListRow, IComparable> key)
    : DataGridSortDescription
{
    public override string PropertyPath => path;
    public override ListSortDirection Direction => direction;
    public override IComparer<object> Comparer { get; } = new RowComparer(key, direction == ListSortDirection.Descending);

    public override DataGridSortDescription SwitchSortDirection() =>
        new GroupedSort(path, direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending, key);

    private sealed class RowComparer(Func<ProcessListRow, IComparable> key, bool descending) : IComparer<object>
    {
        public int Compare(object? a, object? b)
        {
            // The collection view compares against null while finding where a new row goes.
            if (a is not ProcessListRow x) return b is null ? 0 : 1;
            if (b is not ProcessListRow y) return -1;
            if (x.Group != y.Group) return Order(x.Group ?? x, y.Group ?? y);
            if (x is ProcessGroupRow != y is ProcessGroupRow) return x is ProcessGroupRow ? -1 : 1;
            return Order(x, y);
        }

        private int Order(ProcessListRow x, ProcessListRow y)
        {
            int c = key(x).CompareTo(key(y));
            if (descending) c = -c;
            if (c == 0) c = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : x.Pid.CompareTo(y.Pid);
        }
    }
}

/// <summary>
/// Applies a tick's removals and additions as one Reset. DataGridCollectionView's one-at-a-time
/// removal mis-tracks the current item when the removed row is hidden by the filter (a collapsed
/// group's process), and throws.
/// </summary>
internal sealed class RowCollection : ObservableCollection<ProcessListRow>
{
    public void Replace(IReadOnlyCollection<ProcessListRow> removed, IReadOnlyCollection<ProcessListRow> added)
    {
        if (removed.Count == 0 && added.Count == 0) return;
        var gone = removed.ToHashSet();
        var kept = Items.Where(r => !gone.Contains(r)).Concat(added).ToList();
        Items.Clear();
        foreach (var row in kept) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>How often the process list takes a new snapshot. Sampling itself stays at the hub's rate.</summary>
public sealed record RefreshRate(int Seconds)
{
    public override string ToString() => $"Every {Seconds} s";
}

public sealed partial class ProcessesViewModel : PageViewModel
{
    private readonly AppSettings _settings;
    private Snapshot? _latest;
    private DateTime? _lastApplied;

    private static readonly string SystemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static readonly Dictionary<string, Func<ProcessListRow, IComparable>> SortKeys = new()
    {
        [nameof(ProcessListRow.Name)] = r => r.Name,
        [nameof(ProcessListRow.Pid)] = r => r.Pid,
        [nameof(ProcessListRow.User)] = r => r.User,
        [nameof(ProcessListRow.CpuValue)] = r => r.CpuValue,
        [nameof(ProcessListRow.MemoryValue)] = r => r.MemoryValue,
        [nameof(ProcessListRow.GpuValue)] = r => r.GpuValue,
        [nameof(ProcessListRow.NpuValue)] = r => r.NpuValue,
        [nameof(ProcessListRow.DiskValue)] = r => r.DiskValue,
        [nameof(ProcessListRow.NetworkValue)] = r => r.NetworkValue,
        [nameof(ProcessListRow.IoValue)] = r => r.IoValue,
        [nameof(ProcessListRow.Threads)] = r => r.Threads,
    };

    private readonly Dictionary<ProcessIdentity, ProcessRow> _rows = [];
    private readonly Dictionary<ProcessIdentity, ProcessGroupRow> _groups = [];
    private readonly RowCollection _items = [];
    private Dictionary<int, ProcessSample> _byPid = [];

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private ProcessListRow? _selected;
    [ObservableProperty] private string _selectedCaption = "";
    [ObservableProperty] private string _endLabel = "End process";
    [ObservableProperty] private IReadOnlyList<InfoSection> _details = [];
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string? _actionMessage;
    [ObservableProperty] private string _launchChain = "";
    [ObservableProperty] private RefreshRate _refresh;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _pauseLabel = "Pause";

    public ProcessesViewModel(Appearance appearance, AppSettings settings) : base("Processes", "", appearance)
    {
        _settings = settings;
        _refresh = RefreshRates.FirstOrDefault(r => r.Seconds == settings.ProcessRefreshSeconds) ?? RefreshRates[0];
        View = new DataGridCollectionView(_items) { Filter = o => IsShown((ProcessListRow)o) };
        SortBy(nameof(ProcessListRow.CpuValue));
    }

    public DataGridCollectionView View { get; }

    public IReadOnlyList<RefreshRate> RefreshRates { get; } = [new(1), new(2), new(5), new(10)];

    partial void OnRefreshChanged(RefreshRate value)
    {
        _settings.ProcessRefreshSeconds = value.Seconds;
        _settings.Save();
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        PauseLabel = IsPaused ? "Resume" : "Pause";
        if (IsPaused)
        {
            CountText += " · paused";
        }
        else if (_latest is { } latest)
        {
            _lastApplied = null;
            Update(latest);
        }
    }

    /// <summary>Called for a column header click. The same column again reverses the order.</summary>
    public void SortBy(string path)
    {
        if (!SortKeys.TryGetValue(path, out var key)) return;
        var current = View.SortDescriptions.FirstOrDefault();
        var direction = current?.PropertyPath == path
            ? current.Direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending
            : path is nameof(ProcessListRow.Name) or nameof(ProcessListRow.Pid) or nameof(ProcessListRow.User)
                ? ListSortDirection.Ascending
                : ListSortDirection.Descending;
        View.SortDescriptions.Clear();
        View.SortDescriptions.Add(new GroupedSort(path, direction, key));
    }

    partial void OnFilterChanged(string value) => View.Refresh();

    partial void OnSelectedChanged(ProcessListRow? value)
    {
        ActionMessage = null;
        ShowSelection();
    }

    private bool IsFiltering => !string.IsNullOrWhiteSpace(Filter);

    private bool IsShown(ProcessListRow row)
    {
        if (row is ProcessGroupRow group)
            return !IsFiltering || group.Members.Any(Matches);

        var process = (ProcessRow)row;
        if (process.Owner is not { IsExpandable: true } owner) return false;
        // Filtering shows the processes that matched inside a group whose own name did not,
        // such as steamwebhelper under Steam, without exploding every matching group.
        return owner.IsExpanded || (IsFiltering && Matches(process) && !Matches(owner.Root));
    }

    private bool Matches(ProcessRow row)
    {
        var f = Filter.Trim();
        return f.Length == 0
            || row.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || row.ExeName.Contains(f, StringComparison.OrdinalIgnoreCase)
            || row.User.Contains(f, StringComparison.OrdinalIgnoreCase)
            || row.Pid.ToString().StartsWith(f, StringComparison.Ordinal);
    }

    public override void Update(Snapshot s)
    {
        _latest = s;
        if (IsPaused) return;
        // A little slack so a tick that lands a hair early is not pushed a whole interval back.
        if (_lastApplied is { } last && s.Timestamp - last < TimeSpan.FromSeconds(Refresh.Seconds) - TimeSpan.FromMilliseconds(300)) return;
        _lastApplied = s.Timestamp;

        var roots = ProcessGroups.Assign(s.Processes, SystemRoot);
        _byPid = ProcessGroups.ByPid(s.Processes);

        var seen = new HashSet<ProcessIdentity>();
        var added = new List<ProcessRow>();
        foreach (var sample in s.Processes)
        {
            seen.Add(sample.Identity);
            if (_rows.TryGetValue(sample.Identity, out var row))
            {
                row.Update(sample);
            }
            else
            {
                row = new ProcessRow(sample);
                _rows[sample.Identity] = row;
                added.Add(row);
            }
        }

        var removed = new List<ProcessListRow>();
        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            removed.Add(_rows[gone]);
            _rows.Remove(gone);
        }

        var members = new Dictionary<ProcessIdentity, List<ProcessRow>>();
        foreach (var row in _rows.Values)
        {
            var root = roots[row.Identity];
            if (!members.TryGetValue(root, out var list)) members[root] = list = [];
            list.Add(row);
        }

        // The list needs a full pass when rows that are already showing (or hidden) change place.
        bool refresh = IsFiltering;
        var newGroups = new List<ProcessGroupRow>();
        foreach (var (rootId, list) in members)
        {
            if (!_groups.TryGetValue(rootId, out var group))
            {
                group = new ProcessGroupRow(_rows[rootId], () => View.Refresh());
                _groups[rootId] = group;
                newGroups.Add(group);
            }
            bool wasExpandable = group.IsExpandable;
            if (group.SetMembers(list) && group.IsExpanded) refresh = true;
            if (wasExpandable != group.IsExpandable && group.IsExpanded) refresh = true;
            foreach (var row in list)
            {
                if (row.Owner is { } old && old != group) refresh = true;
                row.Owner = group;
            }
        }

        foreach (var gone in _groups.Keys.Where(k => !members.ContainsKey(k)).ToList())
        {
            removed.Add(_groups[gone]);
            _groups.Remove(gone);
        }

        foreach (var row in removed) row.Removed = true;
        var arrivals = newGroups.Concat<ProcessListRow>(added).ToList();
        // A Reset re-filters and re-sorts everything, so it covers the cases below too.
        if (removed.Count > 0 || arrivals.Count > 0)
            _items.Replace(removed, arrivals);
        // Live values drift rows out of order between changes, so re-sort when they have.
        else if (refresh || !IsInOrder())
            View.Refresh();

        CountText = $"{_rows.Count} processes in {_groups.Count} groups";
        ShowSelection();
    }

    private bool IsInOrder()
    {
        if (View.SortDescriptions.FirstOrDefault() is not { } sort) return true;
        object? previous = null;
        foreach (var item in View)
        {
            if (previous is not null && sort.Comparer.Compare(previous, item) > 0) return false;
            previous = item;
        }
        return true;
    }

    private void ShowSelection()
    {
        if (Selected is not { Removed: false } row)
        {
            Details = [];
            SelectedCaption = "";
            LaunchChain = "";
            return;
        }

        var group = row as ProcessGroupRow;
        var process = group?.Root ?? (ProcessRow)row;
        int count = group?.Members.Count ?? 1;
        SelectedCaption = count > 1
            ? $"{process.ExeName} · PID {process.Pid} · {count} processes"
            : $"{process.ExeName} · PID {process.Pid}";
        EndLabel = count > 1 ? $"End all {count}" : "End process";
        Details = BuildDetails(process.Sample, count > 1 ? group : null);
        LaunchChain = BuildLaunchChain(process.Sample);
    }

    /// <summary>
    /// Who started this process, oldest first: explorer.exe → steam.exe → steamwebhelper.exe.
    /// Windows keeps only the parent's PID, so a parent that has exited is shown as gone, not guessed.
    /// </summary>
    private string BuildLaunchChain(ProcessSample process)
    {
        var chain = new List<string> { Describe(process) };
        var current = process;
        for (int depth = 0; depth < 32 && current.ParentPid != 0 && current.ParentPid != current.Identity.Pid; depth++)
        {
            if (ProcessGroups.ParentOf(current, _byPid) is not { } parent)
            {
                chain.Add($"exited (PID {current.ParentPid})");
                break;
            }
            chain.Add(Describe(parent));
            current = parent;
        }
        chain.Reverse();
        return string.Join("  →  ", chain);

        static string Describe(ProcessSample p) =>
            p.StartTime is { } t
                ? $"{p.Name} ({p.Identity.Pid}, {(t.Date == DateTime.Today ? t.ToString("t") : t.ToString("g"))})"
                : $"{p.Name} ({p.Identity.Pid})";
    }

    /// <summary>A group acts on all its processes; a process row acts on that process alone.</summary>
    private static IReadOnlyList<ProcessRow> Targets(ProcessListRow row) =>
        row is ProcessGroupRow group ? group.Members : [(ProcessRow)row];

    [RelayCommand]
    private async Task EndProcess()
    {
        if (Selected is not { } row) return;
        var targets = Targets(row);
        if (targets.Count == 1)
        {
            var only = targets[0];
            if (!await ConfirmDialog.Shared.AskAsync("End process", $"End {only.Name} ({only.Pid})? Unsaved work in it will be lost.", "End process")) return;

            ActionMessage = ProcessActions.Terminate(only.Identity) switch
            {
                TerminateResult.Ended => $"Ended {only.Name} ({only.Pid}).",
                TerminateResult.Exited => $"{only.Name} had already exited.",
                TerminateResult.IdentityMismatch => $"PID {only.Pid} now belongs to a different process. Nothing was ended.",
                TerminateResult.AccessDenied => "Access denied. Run as administrator to end this process.",
                _ => "Could not verify the process. Nothing was ended.",
            };
            return;
        }

        if (!await ConfirmDialog.Shared.AskAsync("End app",
                $"End {row.Name} and all {targets.Count} of its processes? Unsaved work in them will be lost.", $"End all {targets.Count}")) return;

        // Newest first, so helpers go before the process that would restart them.
        var results = targets.OrderByDescending(t => t.Identity.CreateTime)
            .Select(t => ProcessActions.Terminate(t.Identity))
            .ToList();
        int ended = results.Count(r => r is TerminateResult.Ended or TerminateResult.Exited);
        int denied = results.Count(r => r == TerminateResult.AccessDenied);
        ActionMessage = denied > 0
            ? $"Ended {ended} of {targets.Count} processes. {denied} need administrator rights."
            : $"Ended {ended} of {targets.Count} processes.";
    }

    public IReadOnlyList<ProcessPriority> Priorities { get; } = Enum.GetValues<ProcessPriority>();

    [ObservableProperty] private ProcessPriority _newPriority = ProcessPriority.Normal;

    [RelayCommand]
    private async Task ApplyPriority()
    {
        if (Selected is not { } row) return;
        var targets = Targets(row);
        var priority = NewPriority;
        var results = await Task.Run(() => targets.Select(t => ProcessActions.SetPriority(t.Identity, t.Name, priority)).ToList());
        ActionMessage = results.Count == 1
            ? results[0].Message
            : $"{row.Name}: priority set to {priority} on {results.Count(r => r.Succeeded)} of {results.Count} processes.";
    }

    [RelayCommand]
    private void Reveal()
    {
        if (Selected is { } row) ActionMessage = ShellActions.Reveal(Targets(row)[0].Sample.ImagePath).Message;
    }

    private IReadOnlyList<InfoSection> BuildDetails(ProcessSample p, ProcessGroupRow? group)
    {
        string started = p.StartTime is { } t ? t.ToString("g") : "—";
        string uptime = p.StartTime is { } st ? Fmt.Duration(DateTime.Now - st) : "—";
        string parent = p.ParentPid == 0 ? "—"
            : ProcessGroups.ParentOf(p, _byPid) is { } live ? $"{live.Name} ({live.Identity.Pid})"
            : $"exited ({p.ParentPid})";
        var sections = new List<InfoSection>();
        if (group is not null)
        {
            sections.Add(new("App totals",
            [
                new("Processes", group.Members.Count.ToString()),
                new("CPU", group.CpuText),
                new("Memory", group.MemoryText),
                new("GPU", group.GpuText),
                new("Disk", group.DiskText),
                new("Network", group.NetworkText),
                new("Threads", group.Threads.ToString("N0")),
            ]));
        }
        sections.AddRange(
        [
            new("Identity",
            [
                new("Path", p.ImagePath ?? "—"),
                new("Publisher", p.Company ?? "—"),
                new("Parent", parent),
                new("User", p.UserName ?? "—"),
                new("Session", p.SessionId.ToString()),
            ]),
            new("Lifetime",
            [
                new("Started", started),
                new("Uptime", uptime),
                new("Threads", p.Threads.ToString()),
                new("Handles", p.Handles.ToString()),
            ]),
            new("Processor",
            [
                new("CPU", Fmt.Percent(p.Cpu)),
                new("CPU time", p.CpuTime.ToString(@"h\:mm\:ss")),
                new("GPU", Fmt.Percent(p.Gpu)),
                new("NPU", Fmt.Percent(p.Npu)),
                new("Base priority", p.BasePriority.ToString()),
            ]),
            new("Memory and I/O",
            [
                new("Private working set", Fmt.Bytes(p.PrivateWorkingSet)),
                new("Commit", Fmt.Bytes(p.PrivateBytes)),
                new("Working set", Fmt.Bytes(p.WorkingSet)),
                new("Page faults", p.PageFaults.ToString("N0")),
                new("All I/O read", Fmt.Rate(p.IoReadBytesPerSec)),
                new("All I/O write", Fmt.Rate(p.IoWriteBytesPerSec)),
                new("Disk read", Fmt.Rate(p.DiskReadBytesPerSec)),
                new("Disk write", Fmt.Rate(p.DiskWriteBytesPerSec)),
                new("Network receive", Fmt.Rate(p.NetReceiveBytesPerSec)),
                new("Network send", Fmt.Rate(p.NetSendBytesPerSec)),
            ]),
        ]);
        return sections;
    }
}
