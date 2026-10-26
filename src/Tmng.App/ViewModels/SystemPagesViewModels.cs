using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tmng.App.Theme;
using Tmng.Core;
using Tmng.Platform.Windows;

namespace Tmng.App.ViewModels;

// ---------------------------------------------------------------- Services

public sealed partial class ServiceRow(string name) : ObservableObject
{
    public string Name { get; } = name;
    public ServiceSample Sample { get; private set; } = null!;

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _startType = "";
    [ObservableProperty] private string _pidText = "";
    [ObservableProperty] private string _description = "";

    public void Update(ServiceSample s)
    {
        Sample = s;
        DisplayName = s.DisplayName;
        State = s.State;
        StartType = s.StartType;
        PidText = s.Pid > 0 ? s.Pid.ToString() : "";
        Description = s.Description ?? "";
    }
}

public sealed partial class ServicesViewModel : ActionPageViewModel
{
    private readonly Dictionary<string, ServiceRow> _rows = [];
    private readonly ObservableCollection<ServiceRow> _items = [];
    private bool _refreshing;
    private DateTime _lastRefresh;

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _runningOnly;
    [ObservableProperty] private bool _stoppedAutomatic;
    [ObservableProperty] private ServiceRow? _selected;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private ServiceStartMode _newStartMode = ServiceStartMode.Manual;

    public ServicesViewModel(Appearance appearance) : base("Services", "", appearance)
    {
        View = new DataGridCollectionView(_items) { Filter = o => Matches((ServiceRow)o) };
        View.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(ServiceRow.DisplayName), ListSortDirection.Ascending));
    }

    public DataGridCollectionView View { get; }
    public IReadOnlyList<ServiceStartMode> StartModes { get; } = Enum.GetValues<ServiceStartMode>();

    /// <summary>Set by the shell: shows the Processes page filtered to a PID.</summary>
    public Action<int>? ShowProcess { get; set; }

    partial void OnFilterChanged(string value) => View.Refresh();
    partial void OnRunningOnlyChanged(bool value) => View.Refresh();
    partial void OnStoppedAutomaticChanged(bool value) => View.Refresh();

    private bool Matches(ServiceRow row)
    {
        if (RunningOnly && !row.Sample.Running) return false;
        if (StoppedAutomatic && (row.Sample.Running || !row.StartType.StartsWith("Automatic"))) return false;
        if (string.IsNullOrWhiteSpace(Filter)) return true;
        var f = Filter.Trim();
        return row.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || row.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase)
            || row.PidText == f;
    }

    public override void Update(Snapshot snapshot)
    {
        if (DateTime.Now - _lastRefresh > TimeSpan.FromSeconds(2)) _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        _lastRefresh = DateTime.Now;
        try
        {
            var services = await Task.Run(ServiceProvider.Read);
            var seen = new HashSet<string>();
            foreach (var s in services)
            {
                seen.Add(s.Name);
                bool added = !_rows.TryGetValue(s.Name, out var row);
                if (added) row = _rows[s.Name] = new ServiceRow(s.Name);
                row!.Update(s);
                if (added) _items.Add(row);
            }
            foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _items.Remove(_rows[gone]);
                _rows.Remove(gone);
            }
            int running = services.Count(s => s.Running);
            int stoppedAuto = services.Count(s => !s.Running && s.StartType == "Automatic");
            Summary = $"{services.Count} services · {running} running · {stoppedAuto} automatic but stopped";
        }
        catch (Exception ex)
        {
            Summary = ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    [RelayCommand]
    private async Task Start()
    {
        if (Selected is { } row) await RunAndRefresh(() => ServiceActions.Start(row.Name));
    }

    [RelayCommand]
    private async Task Stop()
    {
        if (Selected is not { } row) return;
        if (!await Confirm("Stop service", $"Stop {row.DisplayName}? Services that depend on it stop too.", "Stop")) return;
        await RunAndRefresh(() => ServiceActions.Stop(row.Name));
    }

    [RelayCommand]
    private async Task Restart()
    {
        if (Selected is not { } row) return;
        if (!await Confirm("Restart service", $"Restart {row.DisplayName}?", "Restart")) return;
        await RunAndRefresh(() => ServiceActions.Restart(row.Name));
    }

    [RelayCommand]
    private async Task ApplyStartMode()
    {
        if (Selected is not { } row) return;
        var mode = NewStartMode;
        if (mode == ServiceStartMode.Disabled && !await Confirm("Disable service", $"Disable {row.DisplayName}? Windows features that depend on it may stop working.", "Disable")) return;
        await RunAndRefresh(() => ServiceActions.SetStartMode(row.Name, mode));
    }

    [RelayCommand]
    private void GoToProcess()
    {
        if (Selected is { Sample.Pid: > 0 } row) ShowProcess?.Invoke(row.Sample.Pid);
        else ActionMessage = "This service is not running, so it has no process.";
    }

    private async Task RunAndRefresh(Func<ActionResult> action)
    {
        await RunAsync(action);
        await RefreshAsync();
    }
}

// ---------------------------------------------------------------- Startup apps

public sealed record StartupRow(StartupEntry Entry)
{
    public string Name => Entry.Name;
    public string Publisher => Entry.Publisher ?? "—";
    public string Status => Entry.Enabled ? "Enabled" : "Disabled";
    public string Location => Entry.Location;
    public string Command => Entry.Command;

    // Windows only measures programs that ran; an entry it never saw has no impact to report, not zero.
    public string Impact => !Elevation.IsElevated ? Fmt.Missing(Availability.AccessDenied)
        : Entry.Impact is not { } i ? (Entry.Enabled ? "Not measured" : "—")
        : i.FromLatestSignIn ? i.Level.ToString() : $"{i.Level} (earlier sign-in)";

    public double ImpactRank => Entry.Impact is { } i ? (int)i.Level * 1e6 + i.CpuSeconds * 1e3 + i.DiskBytes / 1e6 : -1;
    public string Cpu => Entry.Impact is { } i ? $"{i.CpuSeconds:0.00} s" : Elevation.IsElevated ? "—" : "admin";
    public double CpuValue => Entry.Impact?.CpuSeconds ?? -1;
    public string Disk => Entry.Impact is { } i ? Fmt.Bytes(i.DiskBytes) : Elevation.IsElevated ? "—" : "admin";
    public double DiskValue => Entry.Impact?.DiskBytes ?? -1;
}

/// <summary>One boot in the history chart: time to desktop stacked under the settling time after it.</summary>
public sealed record BootBar(string Date, string Total, double DesktopHeight, double AfterHeight, string Tip);

public sealed partial class StartupViewModel(Appearance appearance) : ActionPageViewModel("Startup apps", "", appearance)
{
    private DateTime _lastRefresh;

    [ObservableProperty] private IReadOnlyList<StartupRow> _entries = [];
    [ObservableProperty] private StartupRow? _selected;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _bootHeadline = "";
    [ObservableProperty] private string _bootDetail = "";
    [ObservableProperty] private IReadOnlyList<BootBar> _bootHistory = [];

    private const double ChartHeight = 64;

    public override void Update(Snapshot snapshot)
    {
        if (DateTime.Now - _lastRefresh > TimeSpan.FromSeconds(15)) _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _lastRefresh = DateTime.Now;
        string? selectedId = Selected?.Entry.Id;
        var (entries, signIns, boots) = await Task.Run(() =>
        {
            var signIns = Safe(StartupImpactProvider.ReadSignIns);
            var entries = StartupProvider.Read().Select(e => e with { Impact = StartupImpactProvider.Match(e, signIns) }).ToList();
            return (entries, signIns, Safe(() => StartupImpactProvider.ReadBoots()));
        });
        Entries = entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => new StartupRow(e)).ToList();
        Selected = Entries.FirstOrDefault(e => e.Entry.Id == selectedId);
        Summary = $"{entries.Count} startup entries · {entries.Count(e => e.Enabled)} enabled" + (signIns.Count > 0
            ? $" · impact is what each program and everything it launched used in the minutes after your sign-in at {signIns[0].Time:g}"
            : "");
        ShowBoots(boots);
    }

    private static IReadOnlyList<T> Safe<T>(Func<IReadOnlyList<T>> read)
    {
        try { return read(); } catch (Exception) { return []; }
    }

    private void ShowBoots(IReadOnlyList<BootRecord> boots)
    {
        if (!Elevation.IsElevated)
        {
            BootHeadline = "Boot times need administrator rights";
            BootDetail = "Windows keeps boot timings and per-app startup measurements where only administrators can read them.";
            BootHistory = [];
            return;
        }
        if (boots.Count == 0)
        {
            BootHeadline = "No boot timings recorded";
            BootDetail = "Windows' boot diagnostics log has no entries yet.";
            BootHistory = [];
            return;
        }

        var last = boots[0];
        BootHeadline = $"Last boot {last.Total.TotalSeconds:0.0} s · desktop after {last.ToDesktop.TotalSeconds:0.0} s · settled {last.AfterDesktop.TotalSeconds:0.0} s later";
        string slow = last.Slowdowns.Count == 0 ? "Windows flagged nothing as slower than usual."
            : "Slower than usual: " + string.Join(", ", last.Slowdowns.OrderByDescending(s => s.Slower).Select(s => $"{s.Name} (+{s.Slower.TotalSeconds:0.0} s)")) + ".";
        BootDetail = $"{last.Start:g} · {last.StartupApps} startup programs · {slow}";

        double longest = boots.Max(b => b.Total.TotalSeconds);
        BootHistory = boots.Take(10).Reverse().Select(b =>
        {
            double scale = longest > 0 ? ChartHeight / longest : 0;
            return new BootBar(b.Start.ToString("M/d"), $"{b.Total.TotalSeconds:0}s",
                b.ToDesktop.TotalSeconds * scale, b.AfterDesktop.TotalSeconds * scale,
                $"{b.Start:g}: {b.Total.TotalSeconds:0.0} s total, desktop after {b.ToDesktop.TotalSeconds:0.0} s");
        }).ToList();
    }

    [RelayCommand]
    private async Task Enable() => await SetEnabled(true);

    [RelayCommand]
    private async Task Disable() => await SetEnabled(false);

    private async Task SetEnabled(bool enabled)
    {
        if (Selected is not { } row) return;
        await RunAsync(() => StartupProvider.SetEnabled(row.Entry, enabled));
        await RefreshAsync();
    }

    [RelayCommand]
    private void Reveal()
    {
        if (Selected is { } row) ActionMessage = ShellActions.Reveal(row.Entry.ImagePath).Message;
    }
}

// ---------------------------------------------------------------- Users

/// <summary>
/// Per-session totals. CPU and memory add up across processes; per-process GPU load is each
/// process's busiest engine, which does not add up, so sessions show no GPU total.
/// </summary>
public sealed record SessionRow(SessionSample Session, int Processes, string Cpu, string Memory, string Network, string Disk)
{
    public string User => Session.Domain.Length > 0 ? $"{Session.Domain}\\{Session.User}" : Session.User;
    public int Id => Session.SessionId;
    public string State => Session.State;
    public string Client => Session.ClientName ?? (Session.Station.StartsWith("RDP", StringComparison.OrdinalIgnoreCase) ? "Remote" : "This PC");
}

public sealed partial class UsersViewModel(Appearance appearance) : ActionPageViewModel("Users", "", appearance)
{
    [ObservableProperty] private IReadOnlyList<SessionRow> _sessions = [];
    [ObservableProperty] private SessionRow? _selected;

    public override void Update(Snapshot s)
    {
        int? selectedId = Selected?.Id;
        Sessions = SessionProvider.Read()
            .Select(session =>
            {
                var processes = s.Processes.Where(p => p.SessionId == session.SessionId).ToList();
                var cpu = processes.Where(p => p.Cpu.HasValue).Sum(p => p.Cpu.Value);
                return new SessionRow(session, processes.Count,
                    Fmt.Percent(Reading.Of(Math.Min(100, cpu))),
                    Fmt.Bytes(processes.Sum(p => p.PrivateWorkingSet)),
                    Total(processes, p => p.NetReceiveBytesPerSec, p => p.NetSendBytesPerSec),
                    Total(processes, p => p.DiskReadBytesPerSec, p => p.DiskWriteBytesPerSec));
            })
            .ToList();
        Selected = Sessions.FirstOrDefault(r => r.Id == selectedId);
    }

    /// <summary>A session's traffic. If any process's reading is missing, so is the total, rather than undercounting.</summary>
    private static string Total(List<ProcessSample> processes, Func<ProcessSample, Reading> a, Func<ProcessSample, Reading> b)
    {
        foreach (var p in processes)
        {
            if (!a(p).HasValue) return Fmt.Missing(a(p).State);
            if (!b(p).HasValue) return Fmt.Missing(b(p).State);
        }
        return Fmt.Rate(Reading.Of(processes.Sum(p => a(p).Value + b(p).Value)));
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        if (Selected is not { } row) return;
        if (!await Confirm("Disconnect user", $"Disconnect {row.User}? Their apps keep running and the screen locks.", "Disconnect")) return;
        await RunAsync(() => SessionProvider.Disconnect(row.Id));
    }

    [RelayCommand]
    private async Task SignOut()
    {
        if (Selected is not { } row) return;
        if (!await Confirm("Sign out user", $"Sign out {row.User}? Unsaved work in their apps will be lost.", "Sign out")) return;
        await RunAsync(() => SessionProvider.SignOut(row.Id));
    }
}
