using System.Diagnostics;

namespace Tmng.Core;

/// <summary>History keys recorded by <see cref="TelemetryHub"/>.</summary>
public static class Metrics
{
    public const string Cpu = "cpu";
    public const string CpuKernel = "cpu.kernel";
    public const string CpuClock = "cpu.clock";
    public const string CpuTemp = "cpu.temp";
    public const string CpuPower = "cpu.power";
    public const string GpuTemp = "gpu.temp";
    public const string GpuPower = "gpu.power";
    public const string MemUsed = "mem.used";
    public const string NetRx = "net.rx";
    public const string NetTx = "net.tx";
    public const string DiskActive = "disk.active";
    public const string DiskRead = "disk.read";
    public const string DiskWrite = "disk.write";

    public static string Logical(int index) => $"cpu.lp.{index}";
    public static string LogicalKernel(int index) => $"cpu.lp.{index}.kernel";
    public static string Adapter(AdapterKind kind, int index) => kind == AdapterKind.Gpu ? $"gpu.{index}" : $"npu.{index}";
    public static string AdapterMemory(AdapterKind kind, int index) => Adapter(kind, index) + ".mem";
    public static string AdapterEngine(AdapterKind kind, int index, string engine) => $"{Adapter(kind, index)}.eng.{engine}";
    public static string DeviceActive(int disk) => $"disk.{disk}.active";
    public static string DeviceRead(int disk) => $"disk.{disk}.read";
    public static string DeviceWrite(int disk) => $"disk.{disk}.write";
    public static string NicReceive(string id) => $"net.{id}.rx";
    public static string NicSend(string id) => $"net.{id}.tx";
    public static string Sensor(string hardware, string name) => $"sensor.{hardware}.{name}";
}

/// <summary>
/// Samples a source on a dedicated thread and keeps rolling histories. Every series receives
/// exactly one point per tick; when the sampler stalls (sleep, debugger, overload) the missed
/// ticks are recorded as gaps rather than smoothed over.
/// </summary>
public sealed class TelemetryHub : IDisposable
{
    public const int HistoryLength = 300;

    private readonly ITelemetrySource _source;
    private readonly Dictionary<string, MetricSeries> _series = [];
    private readonly Lock _seriesLock = new();
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private Snapshot? _latest;
    private long _generation;

    public TelemetryHub(ITelemetrySource source, TimeSpan interval)
    {
        _source = source;
        Interval = interval;
    }

    public TimeSpan Interval { get; }

    public Snapshot? Latest => Volatile.Read(ref _latest);

    /// <summary>Raised on the sampling thread after each snapshot is recorded.</summary>
    public event Action<Snapshot>? Sampled;

    public MetricSeries Series(string key)
    {
        lock (_seriesLock)
        {
            if (!_series.TryGetValue(key, out var series))
                _series[key] = series = new MetricSeries(HistoryLength);
            return series;
        }
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Telemetry sampler" };
        _thread.Start();
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        var token = _cts.Token;
        TimeSpan? previous = null;

        while (!token.IsCancellationRequested)
        {
            var started = clock.Elapsed;
            if (previous is { } last)
            {
                int missed = (int)Math.Round((started - last) / Interval) - 1;
                if (missed > 0) AppendGaps(Math.Min(missed, HistoryLength));
            }
            previous = started;

            Snapshot? snapshot = null;
            try
            {
                snapshot = _source.Sample(++_generation);
            }
            catch (Exception)
            {
                AppendGaps(1);
            }

            if (snapshot is not null)
            {
                Record(snapshot);
                Volatile.Write(ref _latest, snapshot);
                Sampled?.Invoke(snapshot);
            }

            var delay = Interval - (clock.Elapsed - started);
            if (delay > TimeSpan.Zero) token.WaitHandle.WaitOne(delay);
        }
    }

    private void Record(Snapshot s)
    {
        var values = new Dictionary<string, Reading>
        {
            [Metrics.Cpu] = s.Cpu.Utilization,
            [Metrics.CpuKernel] = s.Cpu.Kernel,
            [Metrics.CpuClock] = s.Cpu.FrequencyMhz,
            [Metrics.CpuTemp] = s.Sensors.CpuTemperatureC,
            [Metrics.CpuPower] = s.Sensors.CpuPackageWatts,
            [Metrics.GpuTemp] = s.Sensors.GpuTemperatureC,
            [Metrics.GpuPower] = s.Sensors.GpuWatts,
            [Metrics.MemUsed] = s.Memory.UsedBytes,
            [Metrics.NetRx] = s.Network.ReceiveBytesPerSec,
            [Metrics.NetTx] = s.Network.SendBytesPerSec,
            [Metrics.DiskActive] = s.Disk.ActivePercent,
            [Metrics.DiskRead] = s.Disk.ReadBytesPerSec,
            [Metrics.DiskWrite] = s.Disk.WriteBytesPerSec,
        };
        for (int i = 0; i < s.Cpu.Logical.Count; i++)
            values[Metrics.Logical(i)] = s.Cpu.Logical[i];
        for (int i = 0; i < s.Cpu.LogicalKernel.Count; i++)
            values[Metrics.LogicalKernel(i)] = s.Cpu.LogicalKernel[i];

        int gpu = 0, npu = 0;
        foreach (var adapter in s.Adapters)
        {
            int index = adapter.Kind == AdapterKind.Gpu ? gpu++ : npu++;
            values[Metrics.Adapter(adapter.Kind, index)] = adapter.Utilization;
            values[Metrics.AdapterMemory(adapter.Kind, index)] = adapter.DedicatedBytes;
            foreach (var engine in adapter.Engines)
                values[Metrics.AdapterEngine(adapter.Kind, index, engine.Type)] = engine.Utilization;
        }

        foreach (var disk in s.Disk.Devices)
        {
            values[Metrics.DeviceActive(disk.Number)] = disk.ActivePercent;
            values[Metrics.DeviceRead(disk.Number)] = disk.ReadBytesPerSec;
            values[Metrics.DeviceWrite(disk.Number)] = disk.WriteBytesPerSec;
        }

        foreach (var nic in s.Network.Interfaces)
        {
            values[Metrics.NicReceive(nic.Id)] = nic.ReceiveBytesPerSec;
            values[Metrics.NicSend(nic.Id)] = nic.SendBytesPerSec;
        }

        foreach (var sensor in s.Sensors.All.Where(x => x.Type == "Temperature"))
            values[Metrics.Sensor(sensor.Hardware, sensor.Name)] = sensor.Value;

        lock (_seriesLock)
        {
            foreach (var key in values.Keys)
                if (!_series.ContainsKey(key)) _series[key] = new MetricSeries(HistoryLength);

            foreach (var (key, series) in _series)
            {
                if (values.TryGetValue(key, out var reading)) series.Append(reading);
                else series.AppendGap();
            }
        }
    }

    private void AppendGaps(int count)
    {
        lock (_seriesLock)
        {
            foreach (var series in _series.Values)
                for (int i = 0; i < count; i++) series.AppendGap();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
