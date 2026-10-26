using CommunityToolkit.Mvvm.ComponentModel;
using Tmng.App.Controls;
using Tmng.App.Theme;
using Tmng.Core;

namespace Tmng.App.ViewModels;

public sealed partial class TileViewModel(string title, string glyph, Func<InkSet, Ink> ink) : ObservableObject
{
    public string Glyph { get; } = glyph;
    public Func<InkSet, Ink> SelectInk { get; } = ink;

    [ObservableProperty] private string _title = title;
    [ObservableProperty] private Ink? _ink;
    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double? _bar;
}

public sealed record TopProcessRow(string Pid, string Name, string Cpu, string Gpu, string Memory);

public sealed partial class SummaryViewModel : PageViewModel
{
    private readonly TraceLine _cpuLine, _tempLine, _kernelLine, _memoryLine;
    private double _peakClock;
    private double _networkPeak = 128 * 1024;
    private double _wattPeak = 50;

    [ObservableProperty] private long _generation;
    [ObservableProperty] private IReadOnlyList<TraceLine> _cpuLines = [];
    [ObservableProperty] private IReadOnlyList<TraceLine> _memoryLines = [];

    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private double? _cpuMeter;
    [ObservableProperty] private string _clockText = "—";
    [ObservableProperty] private double? _clockMeter;
    [ObservableProperty] private string _tempText = "—";
    [ObservableProperty] private double? _tempMeter;
    [ObservableProperty] private string _gpuText = "—";
    [ObservableProperty] private double? _gpuMeter;
    [ObservableProperty] private string _cpuFooter = "";
    [ObservableProperty] private string _clockFooter = "";

    [ObservableProperty] private IReadOnlyList<TopProcessRow> _topProcesses = [];
    [ObservableProperty] private string _processTotal = "";

    [ObservableProperty] private string _memoryText = "—";
    [ObservableProperty] private string _memoryPercentText = "—";
    [ObservableProperty] private double? _memoryMeter;
    [ObservableProperty] private string _memoryAvailable = "";
    [ObservableProperty] private string _memoryCached = "";
    [ObservableProperty] private string _memoryCommitted = "";

    private readonly TileViewModel _disk, _network, _energy, _gpu, _npu, _thermal;

    public SummaryViewModel(TelemetryHub hub, Appearance appearance) : base("Summary", "", appearance)
    {
        _cpuLine = new TraceLine(hub.Series(Metrics.Cpu), 0, 100, fill: true);
        _tempLine = new TraceLine(hub.Series(Metrics.CpuTemp), 0, 110);
        _kernelLine = new TraceLine(hub.Series(Metrics.CpuKernel), 0, 100, fill: true);
        _memoryLine = new TraceLine(hub.Series(Metrics.MemUsed), 0, 1, fill: true);

        _disk = new TileViewModel("Disks", "", i => i.Disk);
        _network = new TileViewModel("Network", "", i => i.Network);
        _energy = new TileViewModel("Energy", "", i => i.Energy);
        _gpu = new TileViewModel("GPU", "", i => i.Gpu);
        _npu = new TileViewModel("NPU", "", i => i.Npu);
        _thermal = new TileViewModel("Thermals", "", i => i.Thermal);
        Tiles = [_disk, _network, _energy, _gpu, _npu, _thermal];
        ApplyInks();
    }

    public IReadOnlyList<TileViewModel> Tiles { get; }

    protected override void OnInksChanged() => ApplyInks();

    private void ApplyInks()
    {
        _cpuLine.Color = Inks.Cpu.Accent;
        _tempLine.Color = Inks.Thermal.Accent;
        _kernelLine.Color = Inks.Kernel.Accent;
        _memoryLine.Color = Inks.Memory.Accent;
        CpuLines = [_kernelLine, _tempLine, _cpuLine];
        MemoryLines = [_memoryLine];
        foreach (var tile in Tiles) tile.Ink = tile.SelectInk(Inks);
    }

    public override void Update(Snapshot s)
    {
        UpdateCpu(s);
        UpdateMemory(s.Memory);
        UpdateTiles(s);
        Generation = s.Generation;
    }

    private void UpdateCpu(Snapshot s)
    {
        var cpu = s.Cpu;
        CpuText = Fmt.Percent(cpu.Utilization);
        CpuMeter = Fraction(cpu.Utilization, 100);

        if (cpu.FrequencyMhz.HasValue) _peakClock = Math.Max(_peakClock, cpu.FrequencyMhz.Value);
        ClockText = Fmt.Frequency(cpu.FrequencyMhz);
        ClockMeter = Fraction(cpu.FrequencyMhz, _peakClock);

        TempText = Fmt.Temperature(s.Sensors.CpuTemperatureC);
        TempMeter = Fraction(s.Sensors.CpuTemperatureC, 110);

        var gpu = PrimaryGpu(s);
        GpuText = gpu is null ? "n/a" : Fmt.Percent(gpu.Utilization);
        GpuMeter = gpu is null ? null : Fraction(gpu.Utilization, 100);

        int performance = cpu.LogicalKinds.Count(k => k == CoreKind.Performance);
        int efficiency = cpu.LogicalKinds.Count(k => k == CoreKind.Efficiency);
        CpuFooter = $"{cpu.Logical.Count} logical processors" + (efficiency > 0 ? $" · {performance} P + {efficiency} E" : "");
        ClockFooter = $"Clock {ClockText} (meter relative to session peak)";

        TopProcesses = s.Processes
            .Where(p => p.Cpu.HasValue)
            .OrderByDescending(p => p.Cpu.Value)
            .ThenByDescending(p => p.PrivateWorkingSet)
            .Take(12)
            .Select(p => new TopProcessRow(p.Identity.Pid.ToString(), p.Name, Fmt.Percent(p.Cpu), Fmt.Percent(p.Gpu, 0), Fmt.Bytes(p.PrivateWorkingSet)))
            .ToList();
        ProcessTotal = $"{s.Processes.Count} total";
    }

    private void UpdateMemory(MemorySample m)
    {
        if (m.TotalBytes.HasValue) _memoryLine.Max = m.TotalBytes.Value;
        MemoryText = $"{Fmt.Bytes(m.UsedBytes)} / {Fmt.Bytes(m.TotalBytes)}";
        var percent = m.UsedBytes.HasValue && m.TotalBytes.HasValue ? Reading.Of(m.UsedBytes.Value / m.TotalBytes.Value * 100) : default;
        MemoryPercentText = Fmt.Percent(percent);
        MemoryMeter = Fraction(percent, 100);
        MemoryAvailable = $"Available {Fmt.Bytes(m.AvailableBytes)}";
        MemoryCached = $"Cached {Fmt.Bytes(m.CachedBytes)}";
        MemoryCommitted = $"Committed {Fmt.Bytes(m.CommittedBytes)} of {Fmt.Bytes(m.CommitLimitBytes)}";
    }

    private void UpdateTiles(Snapshot s)
    {
        _disk.Value = Fmt.Percent(s.Disk.ActivePercent);
        _disk.Detail = $"R: {Fmt.Rate(s.Disk.ReadBytesPerSec)}   W: {Fmt.Rate(s.Disk.WriteBytesPerSec)}";
        _disk.Bar = Fraction(s.Disk.ActivePercent, 100);

        var rx = s.Network.ReceiveBytesPerSec;
        var tx = s.Network.SendBytesPerSec;
        var total = rx.HasValue && tx.HasValue ? Reading.Of(rx.Value + tx.Value) : default;
        if (total.HasValue) _networkPeak = Math.Max(total.Value, Math.Max(128 * 1024, _networkPeak * 0.98));
        _network.Value = Fmt.Rate(total);
        _network.Detail = $"R: {Fmt.Rate(rx)}   S: {Fmt.Rate(tx)}";
        _network.Bar = Fraction(total, _networkPeak);

        var cpuWatts = s.Sensors.CpuPackageWatts;
        if (cpuWatts.HasValue) _wattPeak = Math.Max(_wattPeak, cpuWatts.Value);
        string supply = s.Power.OnAcPower switch { true => "AC power", false => "On battery", _ => "Power source unknown" };
        _energy.Value = Fmt.Watts(cpuWatts);
        _energy.Detail = $"CPU package · GPU {Fmt.Watts(s.Sensors.GpuWatts)} · {supply}";
        _energy.Bar = Fraction(cpuWatts, _wattPeak);

        var gpu = PrimaryGpu(s);
        _gpu.Title = gpu is null ? "GPU" : $"GPU {s.Adapters.Where(a => a.Kind == AdapterKind.Gpu).ToList().IndexOf(gpu)}";
        _gpu.Value = gpu is null ? "n/a" : Fmt.Percent(gpu.Utilization);
        _gpu.Detail = gpu is null ? "No GPU reported" : $"{gpu.Name} · {Fmt.Bytes(gpu.DedicatedBytes)} dedicated in use";
        _gpu.Bar = gpu is null ? null : Fraction(gpu.Utilization, 100);

        var npu = s.Adapters.FirstOrDefault(a => a.Kind == AdapterKind.Npu);
        _npu.Value = npu is null ? "n/a" : Fmt.Percent(npu.Utilization);
        _npu.Detail = npu?.Name ?? "No NPU on this system";
        _npu.Bar = npu is null ? null : Fraction(npu.Utilization, 100);

        _thermal.Value = Fmt.Temperature(s.Sensors.CpuTemperatureC);
        _thermal.Detail = s.Sensors.CpuTemperatureC.State == Availability.AccessDenied
            ? "CPU temperature needs administrator rights"
            : $"CPU · GPU {Fmt.Temperature(s.Sensors.GpuTemperatureC)}";
        _thermal.Bar = Fraction(s.Sensors.CpuTemperatureC, 110);
    }

    /// <summary>The GPU using the most dedicated memory, which is the discrete card when there is one.</summary>
    private static AdapterSample? PrimaryGpu(Snapshot s) => s.Adapters
        .Where(a => a.Kind == AdapterKind.Gpu)
        .OrderByDescending(a => a.DedicatedBytes.HasValue ? a.DedicatedBytes.Value : -1)
        .FirstOrDefault();
}
