using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tmng.App.Theme;
using Tmng.Core;
using Tmng.Platform.Windows;

namespace Tmng.App.ViewModels;

public sealed partial class TestOption(BenchmarkTest test, bool selected) : ObservableObject
{
    public BenchmarkTest Test { get; } = test;
    public string Name => Test.Name;
    public string Description => Test.Description;

    [ObservableProperty] private bool _selected = selected;
}

public sealed record ResultRow(string Test, string Result, string Baseline, string Change, string Verdict);

public sealed record HistoryRow(string When, string Summary);

public sealed partial class BenchmarksViewModel : ActionPageViewModel
{
    private readonly BenchmarkHistory _history;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _progress = "";
    [ObservableProperty] private IReadOnlyList<ResultRow> _results = [];
    [ObservableProperty] private IReadOnlyList<HistoryRow> _runs = [];
    [ObservableProperty] private string _changes = "";
    [ObservableProperty] private string? _diskDrive;

    public BenchmarksViewModel(Appearance appearance) : base("Benchmarks", "", appearance)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TMNG");
        _history = new BenchmarkHistory(Path.Combine(folder, "benchmarks.json"));
        _history.Load();

        Tests =
        [
            new(CpuMemoryBenchmarks.SingleThread, true),
            new(CpuMemoryBenchmarks.MultiThread, true),
            new(CpuMemoryBenchmarks.Simd, true),
            new(CpuMemoryBenchmarks.MemoryBandwidth, true),
            new(CpuMemoryBenchmarks.MemoryLatency, true),
            new(DiskBenchmark.SequentialWrite, true),
            new(DiskBenchmark.SequentialRead, true),
            new(DiskBenchmark.RandomRead, true),
        ];
        Drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.Name).ToList();
        DiskDrive = Drives.FirstOrDefault();
        ShowHistory();
    }

    public IReadOnlyList<TestOption> Tests { get; }
    public IReadOnlyList<string> Drives { get; }
    public string SimdNote => $"This CPU runs .NET vector code with {CpuMemoryBenchmarks.SimdWidth}.";

    private static readonly Dictionary<string, Func<CancellationToken, double>> CpuTests = new()
    {
        [CpuMemoryBenchmarks.SingleThread.Id] = CpuMemoryBenchmarks.RunSingleThread,
        [CpuMemoryBenchmarks.MultiThread.Id] = CpuMemoryBenchmarks.RunMultiThread,
        [CpuMemoryBenchmarks.Simd.Id] = CpuMemoryBenchmarks.RunSimd,
        [CpuMemoryBenchmarks.MemoryBandwidth.Id] = CpuMemoryBenchmarks.RunMemoryBandwidth,
        [CpuMemoryBenchmarks.MemoryLatency.Id] = CpuMemoryBenchmarks.RunMemoryLatency,
    };

    [RelayCommand]
    private async Task Run()
    {
        var selected = Tests.Where(t => t.Selected).Select(t => t.Test).ToList();
        if (selected.Count == 0) return;
        _cts = new CancellationTokenSource();
        Running = true;
        Results = [];
        Changes = "";
        var token = _cts.Token;
        var progress = new Progress<string>(p => Progress = p);
        string? drive = DiskDrive;

        try
        {
            var scores = await Task.Run(() =>
            {
                var s = new Dictionary<string, double>();
                foreach (var test in selected.Where(t => CpuTests.ContainsKey(t.Id)))
                {
                    ((IProgress<string>)progress).Report($"Running {test.Name}…");
                    s[test.Id] = CpuTests[test.Id](token);
                }
                if (drive is not null && selected.Any(t => t.Id.StartsWith("disk.")))
                {
                    foreach (var (id, value) in DiskBenchmark.Run(DiskBenchmark.FolderFor(drive), progress, token))
                        if (selected.Any(t => t.Id == id)) s[id] = value;
                }
                return s;
            }, token);

            var run = new BenchmarkRun(DateTime.Now, Environment.MachineName, DiskBenchmark.Fingerprint(), scores);
            var comparisons = selected.Where(t => scores.ContainsKey(t.Id)).Select(t => _history.Compare(t, scores[t.Id], null)).ToList();
            _history.Add(run);

            Results = comparisons.Select(c => new ResultRow(
                c.Test.Name,
                $"{Number(c.Score)} {c.Test.Unit}",
                c.Baseline is { } b ? $"{Number(b)} {c.Test.Unit}" : "first run",
                c.ChangePercent is { } p ? $"{(p >= 0 ? "+" : "")}{p:0.0}%" : "",
                c.Baseline is null ? "Baseline recorded" : c.Regression ? "Slower than usual" : Better(c) ? "Faster than usual" : "Normal"))
                .ToList();

            var changed = _history.ChangesSincePrevious(run);
            bool regressed = comparisons.Any(c => c.Regression);
            Changes = changed.Count == 0
                ? (regressed ? "Slower than your baseline, and nothing in the system fingerprint changed: check background load, power plan and temperatures." : "")
                : (regressed ? "Slower than your baseline. Changed since the previous run:\n" : "Changed since the previous run:\n") + string.Join("\n", changed);
            Progress = $"Finished at {DateTime.Now:T}. Drive tests used {(drive is null ? "no drive" : DiskBenchmark.FolderFor(drive))}.";
            ShowHistory();
        }
        catch (OperationCanceledException)
        {
            Progress = "Cancelled.";
        }
        catch (Exception ex)
        {
            Progress = $"Benchmark failed: {ex.Message}";
        }
        finally
        {
            Running = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private static bool Better(BenchmarkComparison c) =>
        c.ChangePercent is { } p && (c.Test.HigherIsBetter ? p > 7 : p < -7);

    private void ShowHistory()
    {
        Runs = _history.Runs.OrderByDescending(r => r.Time).Take(30).Select(r => new HistoryRow(
            r.Time.ToString("g"),
            string.Join(" · ", Tests.Where(t => r.Scores.ContainsKey(t.Test.Id)).Select(t => $"{t.Test.Name.Replace("Disk ", "").Replace("CPU ", "")} {Number(r.Scores[t.Test.Id])}"))))
            .ToList();
    }

    private static string Number(double v) => v >= 1000 ? v.ToString("N0") : v >= 10 ? v.ToString("0.0") : v.ToString("0.00");
}
