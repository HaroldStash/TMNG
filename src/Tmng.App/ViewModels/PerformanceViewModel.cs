using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Tmng.App.Controls;
using Tmng.App.Theme;
using Tmng.Core;
using Tmng.Platform.Windows;

namespace Tmng.App.ViewModels;

/// <summary>Shared by every device: the history store and the one-time hardware inventory.</summary>
public sealed class PerformanceContext
{
    public PerformanceContext(TelemetryHub hub)
    {
        Hub = hub;
        _ = Task.Run(() => Inventory = HardwareInventory.Collect());
    }

    public TelemetryHub Hub { get; }

    /// <summary>Null until the background collection finishes; devices show "—" for those facts meanwhile.</summary>
    public HardwareInventory? Inventory { get; private set; }

    public TraceLine Line(string key, double min, double max, bool fill = false) => new(Hub.Series(key), min, max, fill);
}

/// <summary>One entry in the device list, with its tile on the left and its detail page on the right.</summary>
public abstract partial class DeviceViewModel(string key, string glyph, Func<InkSet, Ink> ink, PerformanceContext context) : ObservableObject
{
    protected PerformanceContext Context { get; } = context;
    public string Key { get; } = key;
    public string Glyph { get; } = glyph;
    public Func<InkSet, Ink> SelectInk { get; } = ink;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _line1 = "";
    [ObservableProperty] private string _line2 = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _graphCaption = "";
    [ObservableProperty] private double? _meter;
    [ObservableProperty] private string _valueText = "—";
    [ObservableProperty] private Ink? _ink;
    [ObservableProperty] private Color? _gradientMid;
    [ObservableProperty] private Color? _gradientEnd;
    [ObservableProperty] private IReadOnlyList<TraceLine> _tileLines = [];
    [ObservableProperty] private IReadOnlyList<KeyValue> _stats = [];
    [ObservableProperty] private IReadOnlyList<KeyValue> _specs = [];
    [ObservableProperty] private long _generation;

    public void Refresh(Snapshot snapshot)
    {
        Update(snapshot);
        Generation = snapshot.Generation;
    }

    protected abstract void Update(Snapshot snapshot);

    public virtual void ApplyInks(InkSet inks) => Ink = SelectInk(inks);

    protected static double? Fraction(Reading r, double max) => r.HasValue && max > 0 ? Math.Clamp(r.Value / max, 0, 1) : null;
    protected static KeyValue KV(string key, string value) => new(key, value);

    protected static TraceLine Tint(TraceLine line, Color color)
    {
        line.Color = color;
        return line;
    }
    protected static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "Unavailable" : value;

    /// <summary>Scale that grows with the largest value seen and decays slowly, with a floor.</summary>
    protected static double Track(ref double peak, Reading r, double floor)
    {
        if (r.HasValue) peak = Math.Max(r.Value, peak * 0.995);
        return peak = Math.Max(peak, floor);
    }
}

// ---------------------------------------------------------------- CPU

public sealed partial class LogicalCell(int index, CoreKind kind, TraceLine utility, TraceLine kernel) : ObservableObject
{
    public string Label { get; } = $"CPU {index}" + kind switch { CoreKind.Performance => " · P", CoreKind.Efficiency => " · E", _ => "" };
    public CoreKind Kind { get; } = kind;
    public TraceLine Utility { get; } = utility;
    public TraceLine Kernel { get; } = kernel;

    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private Ink? _ink;
    [ObservableProperty] private IReadOnlyList<TraceLine> _lines = [kernel, utility];
}

public sealed partial class CpuDevice : DeviceViewModel
{
    private readonly TraceLine _total, _kernel;
    private InkSet? _inks;

    [ObservableProperty] private IReadOnlyList<LogicalCell> _cells = [];
    [ObservableProperty] private int _columns = 8;

    public CpuDevice(PerformanceContext context) : base("cpu", "", i => i.Cpu, context)
    {
        Title = "CPU";
        GraphCaption = "% Utilization by logical processor · red is time spent in the kernel";
        _total = context.Line(Metrics.Cpu, 0, 100, fill: true);
        _kernel = context.Line(Metrics.CpuKernel, 0, 100, fill: true);
        TileLines = [_kernel, _total];
    }

    public override void ApplyInks(InkSet inks)
    {
        base.ApplyInks(inks);
        _inks = inks;
        _total.Color = inks.Cpu.Accent;
        _kernel.Color = inks.Kernel.Accent;
        GradientMid = inks.Monochrome ? null : inks.Temp.Accent;
        GradientEnd = inks.Monochrome ? null : inks.Kernel.Accent;
        TileLines = [_kernel, _total];
        foreach (var cell in Cells) Paint(cell, inks);
    }

    private static void Paint(LogicalCell cell, InkSet inks)
    {
        cell.Ink = cell.Kind == CoreKind.Efficiency ? inks.Efficiency : inks.Cpu;
        cell.Utility.Color = cell.Ink.Accent;
        cell.Kernel.Color = inks.Kernel.Accent;
        cell.Lines = [cell.Kernel, cell.Utility];
    }

    protected override void Update(Snapshot s)
    {
        var cpu = s.Cpu;
        var inv = Context.Inventory;
        if (Cells.Count != cpu.Logical.Count)
        {
            Cells = cpu.Logical.Select((_, i) => new LogicalCell(i,
                i < cpu.LogicalKinds.Count ? cpu.LogicalKinds[i] : CoreKind.Uniform,
                Context.Line(Metrics.Logical(i), 0, 100, fill: true),
                Context.Line(Metrics.LogicalKernel(i), 0, 100, fill: true))).ToList();
            Columns = cpu.Logical.Count <= 8 ? 4 : cpu.Logical.Count <= 24 ? 6 : 8;
            if (_inks is not null) foreach (var c in Cells) Paint(c, _inks);
        }
        for (int i = 0; i < Cells.Count; i++) Cells[i].Value = Fmt.Percent(cpu.Logical[i], 0);

        int performance = cpu.LogicalKinds.Count(k => k == CoreKind.Performance);
        int efficiency = cpu.LogicalKinds.Count(k => k == CoreKind.Efficiency);

        ValueText = Fmt.Percent(cpu.Utilization);
        Meter = Fraction(cpu.Utilization, 100);
        Line1 = ValueText + "  " + Fmt.Frequency(cpu.FrequencyMhz);
        Line2 = $"{cpu.Logical.Count} logical processors";
        Subtitle = inv?.CpuName ?? $"{cpu.Logical.Count} logical processors";

        Stats =
        [
            KV("Utilization", Fmt.Percent(cpu.Utilization)),
            KV("Speed", Fmt.Frequency(cpu.FrequencyMhz)),
            KV("Kernel time", Fmt.Percent(cpu.Kernel)),
            KV("Performance limit", cpu.PerformanceLimit.HasValue
                ? $"{cpu.PerformanceLimit.Value:0}%{(cpu.PerformanceLimit.Value >= 99.5 ? " (not limited)" : " (held back)")}"
                : Fmt.Missing(cpu.PerformanceLimit.State)),
            KV("Processes", s.Processes.Count.ToString("N0")),
            KV("Threads", s.Processes.Sum(p => (long)p.Threads).ToString("N0")),
            KV("Handles", s.Processes.Sum(p => (long)p.Handles).ToString("N0")),
            KV("Up time", Uptime()),
        ];

        Specs =
        [
            KV("Base speed", inv?.BaseMhz is { } mhz ? Fmt.Frequency(Reading.Of(mhz)) : "Unavailable"),
            KV("Sockets", inv is null ? "—" : inv.Sockets.ToString()),
            KV("Cores", inv is null || inv.Cores == 0 ? "—" : inv.Cores.ToString()),
            KV("Logical processors", cpu.Logical.Count.ToString()),
            KV("Core types", efficiency > 0 ? $"{performance} performance + {efficiency} efficiency threads" : "Uniform"),
            KV("Virtualization", inv?.VirtualizationEnabled switch
            {
                true => inv.HypervisorPresent == true ? "Enabled (hypervisor running)" : "Enabled",
                false => "Disabled in firmware",
                _ => "Unavailable",
            }),
            KV("L1 cache", inv?.L1Bytes is { } l1 ? Fmt.Bytes(l1) : "Unavailable"),
            KV("L2 cache", inv?.L2Bytes is { } l2 ? Fmt.Bytes(l2) : "Unavailable"),
            KV("L3 cache", inv?.L3Bytes is { } l3 ? Fmt.Bytes(l3) : "Unavailable"),
        ];
    }

    private static string Uptime()
    {
        var t = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return $"{(int)t.TotalDays}:{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }
}

// ---------------------------------------------------------------- Memory

public sealed partial class MemoryDevice : DeviceViewModel
{
    private readonly TraceLine _used;

    [ObservableProperty] private IReadOnlyList<TraceLine> _lines = [];
    [ObservableProperty] private string _topLabel = "";

    public MemoryDevice(PerformanceContext context) : base("mem", "", i => i.Memory, context)
    {
        Title = "Memory";
        GraphCaption = "Memory in use · scrolling history";
        _used = context.Line(Metrics.MemUsed, 0, 1, fill: true);
    }

    public override void ApplyInks(InkSet inks)
    {
        base.ApplyInks(inks);
        _used.Color = inks.Memory.Accent;
        Lines = [_used];
        TileLines = [_used];
    }

    protected override void Update(Snapshot s)
    {
        var m = s.Memory;
        var inv = Context.Inventory;
        if (m.TotalBytes.HasValue) _used.Max = m.TotalBytes.Value;
        var percent = m.UsedBytes.HasValue && m.TotalBytes.HasValue ? Reading.Of(m.UsedBytes.Value / m.TotalBytes.Value * 100) : default;
        var compressed = s.Processes.FirstOrDefault(p => p.Name == "Memory Compression");

        ValueText = Fmt.Percent(percent);
        Meter = Fraction(percent, 100);
        Line1 = $"{Fmt.Bytes(m.UsedBytes)} / {Fmt.Bytes(m.TotalBytes)}";
        Line2 = ValueText;
        TopLabel = Fmt.Bytes(m.TotalBytes);
        Subtitle = inv is null ? Fmt.Bytes(m.TotalBytes)
            : $"{(inv.InstalledBytes is { } b ? Fmt.Bytes(b) : Fmt.Bytes(m.TotalBytes))} {inv.MemoryType} · {inv.MemoryManufacturer}".TrimEnd(' ', '·');

        Stats =
        [
            KV("In use", Fmt.Bytes(m.UsedBytes)),
            KV("Available", Fmt.Bytes(m.AvailableBytes)),
            KV("Committed", $"{Fmt.Bytes(m.CommittedBytes)} / {Fmt.Bytes(m.CommitLimitBytes)}"),
            KV("Cached", Fmt.Bytes(m.CachedBytes)),
            KV("Paged pool", Fmt.Bytes(m.PagedPoolBytes)),
            KV("Non-paged pool", Fmt.Bytes(m.NonPagedPoolBytes)),
            KV("Compressed store", compressed is null ? "Unavailable" : Fmt.Bytes(compressed.WorkingSet)),
            KV("Hardware reserved", inv?.InstalledBytes is { } installed && m.TotalBytes.HasValue
                ? Fmt.Bytes(Math.Max(0, installed - m.TotalBytes.Value)) : "Unavailable"),
        ];

        Specs =
        [
            KV("Speed", inv?.MemorySpeed is { } speed
                ? $"{speed} MT/s" + (inv.MemoryRatedSpeed is { } rated && rated != speed ? $" (rated {rated})" : "")
                : "Unavailable"),
            KV("Type", Or(inv?.MemoryType)),
            KV("Slots used", inv?.SlotsUsed is { } used ? $"{used} of {(inv.SlotsTotal is { } total ? total.ToString() : "?")}" : "Unavailable"),
            KV("Form factor", Or(inv?.FormFactor)),
            KV("Manufacturer", Or(inv?.MemoryManufacturer)),
        ];
    }
}

// ---------------------------------------------------------------- GPU and NPU

public sealed partial class EngineGraph(string name, TraceLine line) : ObservableObject
{
    public string Name { get; } = name;
    public TraceLine Line { get; } = line;

    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private IReadOnlyList<TraceLine> _lines = [line];
}

public sealed partial class AdapterDevice : DeviceViewModel
{
    private static readonly string[] PreferredEngines = ["3D", "Copy", "Video Decode", "Video Encode", "Video Codec", "Compute"];

    private readonly AdapterKind _kind;
    private readonly int _index;
    private readonly TraceLine _utilization, _memory;
    private double _memoryPeak = 256 * 1024 * 1024;
    private Color _accent = Colors.DodgerBlue;

    [ObservableProperty] private IReadOnlyList<EngineGraph> _engines = [];
    [ObservableProperty] private IReadOnlyList<TraceLine> _memoryLines = [];
    [ObservableProperty] private string _memoryCaption = "";

    public AdapterDevice(AdapterKind kind, int index, PerformanceContext context)
        : base(Metrics.Adapter(kind, index), kind == AdapterKind.Gpu ? "" : "", i => kind == AdapterKind.Gpu ? i.Gpu : i.Npu, context)
    {
        _kind = kind;
        _index = index;
        Title = $"{(kind == AdapterKind.Gpu ? "GPU" : "NPU")} {index}";
        _utilization = context.Line(Metrics.Adapter(kind, index), 0, 100, fill: true);
        _memory = context.Line(Metrics.AdapterMemory(kind, index), 0, _memoryPeak, fill: true);
    }

    public override void ApplyInks(InkSet inks)
    {
        base.ApplyInks(inks);
        _accent = SelectInk(inks).Accent;
        _utilization.Color = _accent;
        _memory.Color = _accent;
        TileLines = [_utilization];
        MemoryLines = [_memory];
        foreach (var e in Engines) e.Line.Color = _accent;
    }

    protected override void Update(Snapshot s)
    {
        var adapter = s.Adapters.Where(a => a.Kind == _kind).ElementAtOrDefault(_index);
        if (adapter is null) return;

        // Up to four engine graphs, the familiar ones first.
        var chosen = adapter.Engines
            .OrderBy(e => EngineRank(e.Type))
            .ThenBy(e => e.Type)
            .Take(4)
            .Select(e => e.Type)
            .ToList();
        if (!chosen.SequenceEqual(Engines.Select(e => e.Name)))
        {
            Engines = chosen.Select(name =>
                new EngineGraph(name, Tint(Context.Line(Metrics.AdapterEngine(_kind, _index, name), 0, 100, fill: true), _accent))).ToList();
        }
        foreach (var graph in Engines)
            graph.Value = Fmt.Percent(adapter.Engines.FirstOrDefault(e => e.Type == graph.Name)?.Utilization ?? default, 0);

        // Dedicated memory is graphed against the card's total when a sensor reports it, otherwise against the peak seen.
        var totalSensor = s.Sensors.All.FirstOrDefault(x => x.Name == "GPU Memory Total" && NameMatches(x.Hardware, adapter.Name));
        double memoryMax = totalSensor?.Value is { HasValue: true } t ? t.Value * 1024 * 1024 : Track(ref _memoryPeak, adapter.DedicatedBytes, 256 * 1024 * 1024) * 1.25;
        _memory.Max = memoryMax;
        MemoryCaption = $"Dedicated memory in use · scale {Fmt.Bytes(memoryMax)}{(totalSensor is null ? " (peak seen)" : "")}";

        var temperature = s.Sensors.All.FirstOrDefault(x => x.Type == "Temperature" && x.Name == "GPU Core" && NameMatches(x.Hardware, adapter.Name));
        var power = s.Sensors.All.FirstOrDefault(x => x.Type == "Power" && x.Name.StartsWith("GPU") && NameMatches(x.Hardware, adapter.Name));
        var driver = Context.Inventory?.GpuDrivers.GetValueOrDefault(adapter.Name);

        ValueText = Fmt.Percent(adapter.Utilization);
        Meter = Fraction(adapter.Utilization, 100);
        Line1 = adapter.Name;
        Line2 = ValueText + (temperature?.Value is { HasValue: true } tv ? $"  ({tv.Value:0} °C)" : "");
        Subtitle = adapter.Name;
        GraphCaption = _kind == AdapterKind.Gpu ? "Busiest engines · utilization" : "Compute engines · active time";

        Stats =
        [
            KV("Utilization", Fmt.Percent(adapter.Utilization)),
            KV("Dedicated memory", Fmt.Bytes(adapter.DedicatedBytes)),
            KV("Shared memory", Fmt.Bytes(adapter.SharedBytes)),
            KV("Temperature", temperature is null ? "Unavailable" : Fmt.Temperature(temperature.Value)),
            KV("Power", power is null ? "Unavailable" : Fmt.Watts(power.Value)),
        ];
        Specs =
        [
            KV("Driver version", Or(driver?.Version)),
            KV("Driver date", Or(driver?.Date)),
            KV("Engine types", adapter.Engines.Count.ToString()),
        ];
    }

    private static int EngineRank(string type)
    {
        int rank = Array.FindIndex(PreferredEngines, p => type.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        return rank >= 0 ? rank : 99;
    }

    /// <summary>LibreHardwareMonitor and D3DKMT may name the same card slightly differently.</summary>
    private static bool NameMatches(string sensorHardware, string adapterName) =>
        sensorHardware.Contains(adapterName, StringComparison.OrdinalIgnoreCase) || adapterName.Contains(sensorHardware, StringComparison.OrdinalIgnoreCase);
}

// ---------------------------------------------------------------- Disks

public sealed partial class DiskDevice : DeviceViewModel
{
    private readonly int _number;
    private readonly TraceLine _active, _read, _write;
    private double _ratePeak = 1024 * 1024;
    private double _readTotal, _writeTotal;
    private DateTime _lastSample;

    [ObservableProperty] private IReadOnlyList<TraceLine> _activeLines = [];
    [ObservableProperty] private IReadOnlyList<TraceLine> _transferLines = [];
    [ObservableProperty] private string _transferCaption = "";

    public DiskDevice(int number, PerformanceContext context) : base($"disk.{number}", "", i => i.Disk, context)
    {
        _number = number;
        _active = context.Line(Metrics.DeviceActive(number), 0, 100, fill: true);
        _read = context.Line(Metrics.DeviceRead(number), 0, 1, fill: true);
        _write = context.Line(Metrics.DeviceWrite(number), 0, 1);
    }

    public override void ApplyInks(InkSet inks)
    {
        base.ApplyInks(inks);
        _active.Color = inks.Disk.Accent;
        _read.Color = inks.Disk.Accent;
        _write.Color = inks.Monochrome ? inks.Efficiency.Accent : inks.Temp.Accent;
        ActiveLines = [_active];
        TransferLines = [_read, _write];
        TileLines = [_active];
    }

    protected override void Update(Snapshot s)
    {
        var disk = s.Disk.Devices.FirstOrDefault(d => d.Number == _number);
        if (disk is null) return;
        var info = Context.Inventory?.Disks.GetValueOrDefault(_number);

        // Bytes moved since TMNG started, integrated from the per-second rates.
        double seconds = _lastSample == default ? 0 : (s.Timestamp - _lastSample).TotalSeconds;
        _lastSample = s.Timestamp;
        if (seconds is > 0 and < 10)
        {
            if (disk.ReadBytesPerSec.HasValue) _readTotal += disk.ReadBytesPerSec.Value * seconds;
            if (disk.WriteBytesPerSec.HasValue) _writeTotal += disk.WriteBytesPerSec.Value * seconds;
        }

        double peak = Math.Max(Track(ref _ratePeak, disk.ReadBytesPerSec, 1024 * 1024), Track(ref _ratePeak, disk.WriteBytesPerSec, 1024 * 1024));
        _read.Max = _write.Max = peak * 1.1;
        TransferCaption = $"Transfer rate · read and write · scale {Fmt.Bytes(peak * 1.1)}/s";

        var drives = disk.Volumes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => { try { var d = new DriveInfo(v); return d.IsReady ? d : null; } catch { return null; } })
            .OfType<DriveInfo>()
            .ToList();
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)![..2];

        Title = disk.Volumes.Length > 0 ? $"Disk {_number} ({disk.Volumes})" : $"Disk {_number}";
        string media = info is null ? "" : $"{info.MediaType} ({info.Bus})";
        Subtitle = info?.Model ?? Title;
        ValueText = Fmt.Percent(disk.ActivePercent, 0);
        Meter = Fraction(disk.ActivePercent, 100);
        Line1 = info is null ? "" : media;
        Line2 = ValueText;
        GraphCaption = "% Active time";

        Stats =
        [
            KV("Active time", Fmt.Percent(disk.ActivePercent)),
            KV("Avg. response time", disk.ResponseMs.HasValue ? $"{disk.ResponseMs.Value:0.00} ms" : Fmt.Missing(disk.ResponseMs.State)),
            KV("Read speed", Fmt.Rate(disk.ReadBytesPerSec)),
            KV("Write speed", Fmt.Rate(disk.WriteBytesPerSec)),
            KV("Read since TMNG started", Fmt.Bytes(_readTotal)),
            KV("Written since TMNG started", Fmt.Bytes(_writeTotal)),
        ];
        Specs =
        [
            KV("Model", Or(info?.Model)),
            KV("Type", info is null ? "Unavailable" : media),
            KV("Capacity", info is null ? "Unavailable" : Fmt.Bytes(info.SizeBytes)),
            KV("Formatted", drives.Count == 0 ? "No volumes" : Fmt.Bytes(drives.Sum(d => d.TotalSize))),
            KV("Free", drives.Count == 0 ? "—" : Fmt.Bytes(drives.Sum(d => d.TotalFreeSpace))),
            KV("File systems", drives.Count == 0 ? "—" : string.Join(", ", drives.Select(d => $"{d.Name.TrimEnd('\\')} {d.DriveFormat}"))),
            KV("System disk", drives.Any(d => d.Name.StartsWith(systemDrive, StringComparison.OrdinalIgnoreCase)) ? "Yes" : "No"),
        ];
    }
}

// ---------------------------------------------------------------- Network

public sealed partial class NetworkDevice : DeviceViewModel
{
    private readonly string _id;
    private readonly TraceLine _receive, _send;
    private double _peak = 64 * 1024;

    [ObservableProperty] private IReadOnlyList<TraceLine> _lines = [];

    public NetworkDevice(string id, PerformanceContext context) : base($"nic.{id}", "", i => i.Network, context)
    {
        _id = id;
        _receive = context.Line(Metrics.NicReceive(id), 0, 1, fill: true);
        _send = context.Line(Metrics.NicSend(id), 0, 1);
    }

    public override void ApplyInks(InkSet inks)
    {
        base.ApplyInks(inks);
        _receive.Color = inks.Network.Accent;
        _send.Color = inks.Monochrome ? inks.Efficiency.Accent : inks.Temp.Accent;
        Lines = [_receive, _send];
        TileLines = [_receive, _send];
    }

    protected override void Update(Snapshot s)
    {
        var nic = s.Network.Interfaces.FirstOrDefault(n => n.Id == _id);
        if (nic is null) return;

        double peak = Math.Max(Track(ref _peak, nic.ReceiveBytesPerSec, 64 * 1024), Track(ref _peak, nic.SendBytesPerSec, 64 * 1024)) * 1.1;
        _receive.Max = _send.Max = peak;
        var total = nic.ReceiveBytesPerSec.HasValue && nic.SendBytesPerSec.HasValue
            ? Reading.Of(nic.ReceiveBytesPerSec.Value + nic.SendBytesPerSec.Value) : default;

        Title = nic.Name;
        Subtitle = nic.Description;
        ValueText = Fmt.Rate(total);
        // The meter shows how much of the link's capacity is in use, not an arbitrary scale.
        Meter = nic.LinkSpeedBitsPerSec is { } bits && total.HasValue ? Math.Clamp(total.Value * 8 / bits, 0, 1) : null;
        Line1 = nic.Type;
        Line2 = $"S: {Fmt.Rate(nic.SendBytesPerSec)}  R: {Fmt.Rate(nic.ReceiveBytesPerSec)}";
        GraphCaption = $"Throughput · receive and send · scale {Fmt.Bytes(peak)}/s · meter shows share of link speed";

        Stats =
        [
            KV("Receive", Fmt.Rate(nic.ReceiveBytesPerSec)),
            KV("Send", Fmt.Rate(nic.SendBytesPerSec)),
            KV("Received since connect", Fmt.Bytes(nic.TotalReceived)),
            KV("Sent since connect", Fmt.Bytes(nic.TotalSent)),
        ];
        Specs =
        [
            KV("Adapter", nic.Description),
            KV("Connection type", nic.Type),
            KV("Link speed", nic.LinkSpeedBitsPerSec is { } speed ? LinkSpeed(speed) : "Unavailable"),
            KV("Hardware address", nic.MacAddress ?? "Unavailable"),
            KV("IPv4 address", nic.IPv4.Count == 0 ? "None" : string.Join(", ", nic.IPv4)),
            KV("IPv6 address", nic.IPv6.Count == 0 ? "None" : string.Join(", ", nic.IPv6.Take(2))),
        ];
    }

    private static string LinkSpeed(long bits) => bits >= 1_000_000_000 ? $"{bits / 1e9:0.#} Gbps" : $"{bits / 1e6:0} Mbps";
}

// ---------------------------------------------------------------- Energy

public sealed record EnergyRow(string Name, double Share, string Watts);

public sealed partial class EnergyDevice : DeviceViewModel
{
    private readonly TraceLine _cpu, _gpu;
    private double _peak = 20;
    private int _ticks;
    private string? _plan;

    [ObservableProperty] private IReadOnlyList<TraceLine> _lines = [];
    [ObservableProperty] private string _planText = "—";
    [ObservableProperty] private string _sourceText = "—";
    [ObservableProperty] private string _batteryText = "—";
    [ObservableProperty] private IReadOnlyList<EnergyRow> _topProcesses = [];
    [ObservableProperty] private string _estimateNote = "";

    public EnergyDevice(PerformanceContext context) : base("energy", "", i => i.Energy, context)
    {
        Title = "Energy";
        _cpu = context.Line(Metrics.CpuPower, 0, 1, fill: true);
        _gpu = context.Line(Metrics.GpuPower, 0, 1);
    }

    public override void ApplyInks(InkSet inks)
    {
        base.ApplyInks(inks);
        _cpu.Color = inks.Energy.Accent;
        _gpu.Color = inks.Gpu.Accent;
        Lines = [_cpu, _gpu];
        TileLines = [_cpu];
    }

    protected override void Update(Snapshot s)
    {
        var cpuW = s.Sensors.CpuPackageWatts;
        var gpuW = s.Sensors.GpuWatts;
        double peak = Math.Max(Track(ref _peak, cpuW, 20), Track(ref _peak, gpuW, 20)) * 1.1;
        _cpu.Max = _gpu.Max = peak;
        if (_ticks++ % 10 == 0) _plan = PowerPlan.ActiveName();

        var total = cpuW.HasValue ? Reading.Of(cpuW.Value + (gpuW.HasValue ? gpuW.Value : 0)) : default;
        ValueText = Fmt.Watts(total);
        Meter = Fraction(total, peak);
        string source = s.Power.OnAcPower switch { true => "AC power", false => "Battery", _ => "Unknown source" };
        Line1 = $"{_plan ?? "Power plan unknown"} · {source}";
        Line2 = ValueText;
        Subtitle = gpuW.HasValue ? "CPU package plus GPU" : "CPU package (GPU power needs administrator rights)";
        GraphCaption = $"Power · CPU package and GPU · scale {peak:0} W";
        PlanText = _plan ?? "Unavailable";
        SourceText = source;
        BatteryText = s.Power.BatteryPercent.State == Availability.Unsupported ? "No battery" : Fmt.Percent(s.Power.BatteryPercent, 0);

        Stats =
        [
            KV("CPU package", Fmt.Watts(cpuW)),
            KV("GPU", Fmt.Watts(gpuW)),
            KV("Effective clock", Fmt.Frequency(s.Cpu.FrequencyMhz)),
            KV("Performance limit", s.Cpu.PerformanceLimit.HasValue ? $"{s.Cpu.PerformanceLimit.Value:0}%" : "Unavailable"),
        ];
        Specs = [];

        // Estimate: split measured watts by each process's share of CPU and GPU utilization.
        if (!cpuW.HasValue)
        {
            TopProcesses = [];
            EstimateNote = "CPU package power is unavailable, so no per-process estimate can be made.";
            return;
        }
        double cpuSum = s.Processes.Where(p => p.Cpu.HasValue).Sum(p => p.Cpu.Value);
        double gpuSum = s.Processes.Where(p => p.Gpu.HasValue).Sum(p => p.Gpu.Value);
        var rows = s.Processes
            .Select(p => (p.Name,
                Watts: (cpuSum > 0 && p.Cpu.HasValue ? cpuW.Value * p.Cpu.Value / cpuSum : 0)
                     + (gpuW.HasValue && gpuSum > 0 && p.Gpu.HasValue ? gpuW.Value * p.Gpu.Value / gpuSum : 0)))
            .GroupBy(p => p.Name)
            .Select(g => (Name: g.Key, Watts: g.Sum(x => x.Watts)))
            .OrderByDescending(x => x.Watts)
            .Take(8)
            .ToList();
        double top = rows.Count > 0 ? Math.Max(rows[0].Watts, 0.01) : 1;
        TopProcesses = rows.Select(r => new EnergyRow(r.Name, r.Watts / top, $"≈ {r.Watts:0.0} W")).ToList();
        EstimateNote = "Estimate: measured CPU package and GPU watts split by each process's share of CPU and GPU utilization (grouped by name). "
            + "Idle power, memory and other components are not attributed, so the figures are a ranking, not a meter.";
    }
}

// ---------------------------------------------------------------- Thermals

public sealed partial class SensorCell(string key, string label, TraceLine line) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public TraceLine Line { get; } = line;

    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private IReadOnlyList<TraceLine> _lines = [line];
}

public sealed partial class ThermalDevice : DeviceViewModel
{
    private readonly TraceLine _cpu, _gpu;
    private Color _accent = Colors.Orange;

    [ObservableProperty] private IReadOnlyList<TraceLine> _lines = [];
    [ObservableProperty] private IReadOnlyList<SensorCell> _sensors = [];
    [ObservableProperty] private string _hotspotText = "—";
    [ObservableProperty] private string _limitText = "—";
    [ObservableProperty] private string _sensorNote = "";

    public ThermalDevice(PerformanceContext context) : base("thermal", "", i => i.Thermal, context)
    {
        Title = "Thermals";
        GraphCaption = "CPU and GPU temperature · 0–110 °C";
        _cpu = context.Line(Metrics.CpuTemp, 0, 110);
        _gpu = context.Line(Metrics.GpuTemp, 0, 110);
    }

    public override void ApplyInks(InkSet inks)
    {
        base.ApplyInks(inks);
        _accent = inks.Thermal.Accent;
        _cpu.Color = _accent;
        _gpu.Color = inks.Gpu.Accent;
        Lines = [_cpu, _gpu];
        TileLines = [_cpu, _gpu];
        foreach (var cell in Sensors) cell.Line.Color = _accent;
    }

    protected override void Update(Snapshot s)
    {
        var temps = s.Sensors.All.Where(x => x.Type == "Temperature").ToList();
        var measured = temps.Where(t => t.Value.HasValue).ToList();
        var hottest = measured.MaxBy(t => t.Value.Value);
        var headline = s.Sensors.CpuTemperatureC.HasValue ? s.Sensors.CpuTemperatureC : s.Sensors.GpuTemperatureC;

        ValueText = Fmt.Temperature(headline);
        Meter = Fraction(headline, 110);
        Subtitle = s.Sensors.CpuTemperatureC.HasValue ? "CPU package"
            : s.Sensors.GpuTemperatureC.HasValue ? "GPU (CPU temperature needs administrator rights and the PawnIO driver)"
            : "Temperatures need administrator rights and the PawnIO driver";
        var limit = s.Cpu.PerformanceLimit;
        Line1 = limit.HasValue && limit.Value < 99.5 ? $"CPU held to {limit.Value:0}%" : "Not limited";
        Line2 = ValueText;
        HotspotText = hottest is null ? "Unavailable" : $"{hottest.Value.Value:0.0} °C · {hottest.Name}";
        LimitText = s.Cpu.PerformanceLimit.HasValue
            ? (s.Cpu.PerformanceLimit.Value >= 99.5 ? "None: full performance allowed" : $"Held to {s.Cpu.PerformanceLimit.Value:0}% of nominal")
            : "Unavailable";

        if (!temps.Select(t => Metrics.Sensor(t.Hardware, t.Name)).SequenceEqual(Sensors.Select(c => c.Key)))
        {
            Sensors = temps.Select(t => new SensorCell(Metrics.Sensor(t.Hardware, t.Name), $"{t.Name} · {t.Hardware}",
                Tint(Context.Line(Metrics.Sensor(t.Hardware, t.Name), 0, 110), _accent))).ToList();
        }
        foreach (var (cell, sensor) in Sensors.Zip(temps)) cell.Value = Fmt.Temperature(sensor.Value);
        int denied = temps.Count(t => t.Value.State == Availability.AccessDenied);
        SensorNote = denied > 0 ? $"{denied} sensor(s) need administrator rights and the PawnIO driver; they show \"admin\" until then." : "";

        Stats =
        [
            KV("CPU", Fmt.Temperature(s.Sensors.CpuTemperatureC)),
            KV("GPU", Fmt.Temperature(s.Sensors.GpuTemperatureC)),
            KV("Hottest sensor", HotspotText),
            KV("Throttling", LimitText),
        ];
        Specs = [];
    }
}

// ---------------------------------------------------------------- Page

/// <summary>Task Manager-style Performance page: a device list with live tiles and a detail pane.</summary>
public sealed partial class PerformanceViewModel : PageViewModel
{
    private readonly PerformanceContext _context;
    private readonly Dictionary<string, DeviceViewModel> _devices = [];

    [ObservableProperty] private IReadOnlyList<DeviceViewModel> _devicesList = [];
    [ObservableProperty] private DeviceViewModel? _selected;

    public PerformanceViewModel(TelemetryHub hub, Appearance appearance) : base("Performance", "", appearance)
    {
        _context = new PerformanceContext(hub);
    }

    protected override void OnInksChanged()
    {
        foreach (var device in DevicesList) device.ApplyInks(Inks);
    }

    public override void Update(Snapshot s)
    {
        var wanted = new List<(string Key, Func<DeviceViewModel> Create)>
        {
            ("cpu", () => new CpuDevice(_context)),
            ("mem", () => new MemoryDevice(_context)),
        };
        int gpu = 0, npu = 0;
        foreach (var adapter in s.Adapters)
        {
            var kind = adapter.Kind;
            int index = kind == AdapterKind.Gpu ? gpu++ : npu++;
            wanted.Add((Metrics.Adapter(kind, index), () => new AdapterDevice(kind, index, _context)));
        }
        foreach (var disk in s.Disk.Devices)
            wanted.Add(($"disk.{disk.Number}", () => new DiskDevice(disk.Number, _context)));
        foreach (var nic in s.Network.Interfaces)
            wanted.Add(($"nic.{nic.Id}", () => new NetworkDevice(nic.Id, _context)));
        wanted.Add(("energy", () => new EnergyDevice(_context)));
        wanted.Add(("thermal", () => new ThermalDevice(_context)));

        if (!wanted.Select(w => w.Key).SequenceEqual(DevicesList.Select(d => d.Key)))
        {
            string? selectedKey = Selected?.Key;
            DevicesList = wanted.Select(w =>
            {
                if (!_devices.TryGetValue(w.Key, out var device))
                {
                    device = _devices[w.Key] = w.Create();
                    device.ApplyInks(Inks);
                }
                return device;
            }).ToList();
            Selected = DevicesList.FirstOrDefault(d => d.Key == selectedKey) ?? DevicesList[0];
        }

        foreach (var device in DevicesList) device.Refresh(s);
    }
}
