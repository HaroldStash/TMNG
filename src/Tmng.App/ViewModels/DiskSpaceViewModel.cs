using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tmng.App.Controls;
using Tmng.App.Theme;
using Tmng.Core;
using Tmng.Platform.Windows;

namespace Tmng.App.ViewModels;

public sealed record DiskRow(DiskNode Node, long ParentSize)
{
    public string Name => Node.Name;
    public string Kind => Node.IsDirectory ? "Folder" : "File";
    public long Size => Node.Size;
    public string SizeText => Fmt.Bytes(Node.Size);
    public string Share => ParentSize > 0 ? $"{Node.Size * 100.0 / ParentSize:0.0}%" : "";
    public string Files => Node.IsDirectory ? Node.FileCount.ToString("N0") : "";
    public string Modified => Node.Modified == default ? "—" : Node.Modified.ToString("d");
    public string Path => Node.FullPath;
}

public sealed record DuplicateRow(int Group, string SizeText, long Wasted, string Path);

public sealed partial class DiskSpaceViewModel : ActionPageViewModel
{
    private CancellationTokenSource? _cts;
    private DiskNode? _root;

    [ObservableProperty] private IReadOnlyList<string> _drives = [];
    [ObservableProperty] private string? _selectedDrive;
    [ObservableProperty] private bool _scanning;
    [ObservableProperty] private string _progressText = "Choose a drive and scan.";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private DiskNode? _current;
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private IReadOnlyList<DiskRow> _rows = [];
    [ObservableProperty] private DiskRow? _selectedRow;
    [ObservableProperty] private IReadOnlyList<TreemapItem> _treemap = [];
    [ObservableProperty] private IReadOnlyList<DiskRow> _largest = [];
    [ObservableProperty] private DiskRow? _selectedLargest;
    [ObservableProperty] private decimal? _staleDays = 365;
    [ObservableProperty] private IReadOnlyList<DiskRow> _stale = [];
    [ObservableProperty] private DiskRow? _selectedStale;
    [ObservableProperty] private string _staleSummary = "";
    [ObservableProperty] private IReadOnlyList<DuplicateRow> _duplicates = [];
    [ObservableProperty] private DuplicateRow? _selectedDuplicate;
    [ObservableProperty] private string _duplicateSummary = "Scan a drive, then search it for duplicates of 1 MB or more.";

    public DiskSpaceViewModel(Appearance appearance) : base("Disk Space", "", appearance)
    {
        Drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => d.Name)
            .ToList();
        SelectedDrive = Drives.FirstOrDefault();
    }

    partial void OnCurrentChanged(DiskNode? value) => ShowFolder();
    partial void OnStaleDaysChanged(decimal? value) => ComputeStale();

    [RelayCommand]
    private async Task Scan()
    {
        if (SelectedDrive is not { } drive) return;
        _cts = new CancellationTokenSource();
        Scanning = true;
        ResultText = "";
        var progress = new Progress<ScanProgress>(p => ProgressText = $"{p.Phase}… {p.Items:N0} items");
        try
        {
            var result = await Task.Run(() => DiskScanner.Scan(drive, progress, _cts.Token));
            _root = result.Root;
            ResultText = $"{Fmt.Bytes(result.Root.Size)} in {result.Root.FileCount:N0} files · {result.Method} · {result.Elapsed.TotalSeconds:0.0} s"
                + (result.Note is null ? "" : $"\n{result.Note}");
            ProgressText = "";
            Current = _root;
            Largest = _root.EnumerateFiles().OrderByDescending(f => f.Size).Take(300).Select(f => new DiskRow(f, _root.Size)).ToList();
            ComputeStale();
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            ProgressText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            Scanning = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Up()
    {
        if (Current?.Parent is { } parent) Current = parent;
    }

    [RelayCommand]
    private void Enter(object? target)
    {
        var node = target switch
        {
            DiskNode n => n,
            DiskRow r => r.Node,
            _ => SelectedRow?.Node,
        };
        if (node is { IsDirectory: true }) Current = node;
    }

    private void ShowFolder()
    {
        if (Current is not { } folder) return;
        Location = folder.FullPath;
        var children = folder.Children ?? [];
        Rows = children.Select(c => new DiskRow(c, folder.Size)).ToList();

        var shown = children.Take(250).Select(c => new TreemapItem(c.Name, c.Size, c.IsDirectory, c)).ToList();
        long rest = children.Skip(250).Sum(c => c.Size);
        if (rest > 0) shown.Add(new TreemapItem($"{children.Count - 250:N0} smaller items", rest, false, null));
        Treemap = shown;
    }

    private void ComputeStale()
    {
        if (_root is null) return;
        var cutoff = DateTime.Now.AddDays(-(double)(StaleDays ?? 365));
        var stale = _root.EnumerateFiles().Where(f => f.Modified != default && f.Modified < cutoff).ToList();
        StaleSummary = $"{stale.Count:N0} files not modified in {StaleDays:0} days, using {Fmt.Bytes(stale.Sum(f => f.Size))}. Largest 300 shown.";
        Stale = stale.OrderByDescending(f => f.Size).Take(300).Select(f => new DiskRow(f, _root.Size)).ToList();
    }

    [RelayCommand]
    private async Task FindDuplicates()
    {
        if (_root is null)
        {
            DuplicateSummary = "Scan a drive first.";
            return;
        }
        _cts = new CancellationTokenSource();
        Scanning = true;
        var root = _root;
        var progress = new Progress<ScanProgress>(p => DuplicateSummary = $"{p.Phase}… {p.Items:N0} files checked");
        try
        {
            var groups = await Task.Run(() => DuplicateFinder.Find(root.EnumerateFiles(), 1 << 20, progress, _cts.Token));
            Duplicates = groups.Take(200)
                .SelectMany((g, i) => g.Paths.Select(p => new DuplicateRow(i + 1, Fmt.Bytes(g.Size), g.Wasted, p)))
                .ToList();
            DuplicateSummary = $"{groups.Count:N0} sets of identical files · {Fmt.Bytes(groups.Sum(g => g.Wasted))} reclaimable by keeping one of each.";
        }
        catch (OperationCanceledException)
        {
            DuplicateSummary = "Duplicate search cancelled.";
        }
        finally
        {
            Scanning = false;
        }
    }

    private string? SelectedPath(string tab) => tab switch
    {
        "largest" => SelectedLargest?.Path,
        "stale" => SelectedStale?.Path,
        "duplicates" => SelectedDuplicate?.Path,
        _ => SelectedRow?.Path,
    };

    [RelayCommand]
    private void Reveal(string tab) => ActionMessage = ShellActions.Reveal(SelectedPath(tab)).Message;

    [RelayCommand]
    private async Task Recycle(string tab)
    {
        if (SelectedPath(tab) is not { } path) return;
        if (!await Confirm("Move to Recycle Bin", $"Move {path} to the Recycle Bin?", "Recycle")) return;
        await RunAsync(() => ShellActions.Recycle([path]));
        if (!File.Exists(path) && !Directory.Exists(path)) Remove(path);
    }

    /// <summary>Takes a recycled item out of the scanned tree so totals stay true without a rescan.</summary>
    private void Remove(string path)
    {
        var node = _root?.EnumerateNodes().FirstOrDefault(n => n.FullPath == path);
        if (node?.Parent is not { } parent) return;
        parent.Children!.Remove(node);
        for (var p = parent; p is not null; p = p.Parent)
        {
            p.Size -= node.Size;
            p.FileCount -= node.IsDirectory ? node.FileCount : 1;
        }
        ShowFolder();
        Largest = Largest.Where(r => r.Node != node).ToList();
        Stale = Stale.Where(r => r.Node != node).ToList();
        Duplicates = Duplicates.Where(r => r.Path != path).ToList();
    }
}

internal static class DiskNodeExtensions
{
    public static IEnumerable<DiskNode> EnumerateNodes(this DiskNode root)
    {
        var stack = new Stack<DiskNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            if (node.Children is not null)
                foreach (var child in node.Children) stack.Push(child);
        }
    }
}
