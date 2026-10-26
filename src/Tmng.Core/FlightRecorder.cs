using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Tmng.Core;

/// <summary>One tick of the seven recorded panels. Null means the reading was not available, never zero.</summary>
public sealed record TraceFrame(
    DateTime Time,
    double? Cpu,
    double? Kernel,
    double? ClockMhz,
    double? Gpu,
    double? MemoryUsed,
    double? MemoryTotal,
    double? DiskActive,
    double? DiskRead,
    double? DiskWrite,
    double? NetReceive,
    double? NetSend,
    double? CpuWatts,
    double? GpuWatts,
    double? CpuTemp,
    double? GpuTemp,
    IReadOnlyList<TraceProcess> Top)
{
    /// <summary>Busiest network users, when the kernel I/O trace was running (administrator only).</summary>
    public IReadOnlyList<TraceProcess> TopNetwork { get; init; } = [];
}

public sealed record TraceProcess(int Pid, string Name, double? Cpu, long Memory)
{
    public double? Network { get; init; }
}

public enum TraceEventKind { Started, Exited, Trigger, Marker }

public sealed record TraceEvent(DateTime Time, TraceEventKind Kind, int Pid, string Name);

public sealed record Trace(
    int Version,
    string Machine,
    DateTime Start,
    double IntervalSeconds,
    string? Reason,
    IReadOnlyList<TraceFrame> Frames,
    IReadOnlyList<TraceEvent> Events)
{
    public TimeSpan Duration => Frames.Count == 0 ? TimeSpan.Zero : Frames[^1].Time - Frames[0].Time;
}

public static class SnapshotExtensions
{
    /// <summary>The GPU using the most dedicated memory, which is the discrete card when there is one.</summary>
    public static AdapterSample? PrimaryGpu(this Snapshot s) => s.Adapters
        .Where(a => a.Kind == AdapterKind.Gpu)
        .OrderByDescending(a => a.DedicatedBytes.HasValue ? a.DedicatedBytes.Value : -1)
        .FirstOrDefault();

    public static TraceFrame ToFrame(this Snapshot s) => new(
        s.Timestamp,
        s.Cpu.Utilization.Nullable,
        s.Cpu.Kernel.Nullable,
        s.Cpu.FrequencyMhz.Nullable,
        s.PrimaryGpu()?.Utilization.Nullable,
        s.Memory.UsedBytes.Nullable,
        s.Memory.TotalBytes.Nullable,
        s.Disk.ActivePercent.Nullable,
        s.Disk.ReadBytesPerSec.Nullable,
        s.Disk.WriteBytesPerSec.Nullable,
        s.Network.ReceiveBytesPerSec.Nullable,
        s.Network.SendBytesPerSec.Nullable,
        s.Sensors.CpuPackageWatts.Nullable,
        s.Sensors.GpuWatts.Nullable,
        s.Sensors.CpuTemperatureC.Nullable,
        s.Sensors.GpuTemperatureC.Nullable,
        s.Processes.Where(p => p.Cpu.HasValue)
            .OrderByDescending(p => p.Cpu.Value)
            .Take(5)
            .Select(p => new TraceProcess(p.Identity.Pid, p.Name, p.Cpu.Nullable, p.PrivateWorkingSet))
            .ToList())
    {
        TopNetwork = s.Processes
            .Where(p => p.NetReceiveBytesPerSec.HasValue && p.NetSendBytesPerSec.HasValue)
            .Select(p => (p, rate: p.NetReceiveBytesPerSec.Value + p.NetSendBytesPerSec.Value))
            .Where(x => x.rate > 0)
            .OrderByDescending(x => x.rate)
            .Take(3)
            .Select(x => new TraceProcess(x.p.Identity.Pid, x.p.Name, x.p.Cpu.Nullable, x.p.PrivateWorkingSet) { Network = x.rate })
            .ToList(),
    };
}

/// <summary>
/// A rule that fired. Created when it fires; <see cref="Path"/> or <see cref="Error"/> is filled in
/// once the trace, which keeps recording for <see cref="FlightRecorder.CaptureAfter"/>, is saved.
/// </summary>
public sealed class AlertRecord
{
    public DateTime Time { get; set; }
    public string Rule { get; set; } = "";
    /// <summary>What held, e.g. "CPU temperature above 85 °C and CPU fan below 600 RPM for 10 s".</summary>
    public string Reason { get; set; } = "";
    public string? TopProcess { get; set; }
    /// <summary>Each condition's most extreme value, from the start of the qualifying stretch to the end of the capture.</summary>
    public List<ConditionExtreme> Extremes { get; set; } = [];
    public DateTime CaptureUntil { get; set; }
    public string? Path { get; set; }
    public string? Error { get; set; }

    public bool Capturing => Path is null && Error is null;
}

/// <summary>
/// Always-on rolling buffer of every panel plus process start and exit events. A manual
/// recording or a firing trigger turns the buffer into a .tmngtrace file.
/// </summary>
public sealed class FlightRecorder
{
    private static readonly TimeSpan TriggerCooldown = TimeSpan.FromMinutes(5);

    private readonly Lock _lock = new();
    private readonly Queue<TraceFrame> _buffer = new();
    private readonly Queue<TraceEvent> _events = new();
    private readonly int _capacity;
    private readonly double _interval;
    private Dictionary<ProcessIdentity, string>? _known;
    private List<TraceFrame>? _recording;
    private List<TraceEvent>? _recordingEvents;
    private readonly List<PendingAlert> _pending = [];

    /// <summary>An alert still recording what happens after it fired.</summary>
    private sealed record PendingAlert(
        AlertRecord Record, IReadOnlyList<AlertCondition> Conditions, double?[] Extremes, List<TraceFrame> Frames, List<TraceEvent> Events);

    public FlightRecorder(TelemetryHub hub, TimeSpan window, string folder)
    {
        _interval = hub.Interval.TotalSeconds;
        _capacity = (int)(window / hub.Interval);
        Window = window;
        Folder = folder;
        hub.Sampled += OnSample;
    }

    public TimeSpan Window { get; }
    public string Folder { get; }

    /// <summary>Replaced as a whole when rules are edited; the sampling thread reads it every tick.</summary>
    public IReadOnlyList<AlertRule> Rules { get; set; } = AlertRule.Defaults();
    public bool IsRecording => _recording is not null;

    /// <summary>When the current manual recording started; null when not recording.</summary>
    public DateTime? RecordingStarted { get; private set; }

    /// <summary>The file name (without extension) the current recording will be saved under.</summary>
    public string? RecordingName { get; private set; }

    public int BufferedFrames
    {
        get { lock (_lock) return _buffer.Count; }
    }

    /// <summary>How long an alert's trace keeps recording after the rule fires, so it shows the aftermath too.</summary>
    public TimeSpan CaptureAfter { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Raised on the sampling thread the moment a rule fires, before its trace is saved.</summary>
    public event Action<AlertRecord>? AlertFired;

    /// <summary>Raised on a background thread once an alert's trace is saved or has failed to save.</summary>
    public event Action<AlertRecord>? AlertSaved;

    private void OnSample(Snapshot s)
    {
        var frame = s.ToFrame();
        var values = AlertMetrics.Read(s);
        lock (_lock)
        {
            var events = DiffProcesses(s);
            Push(_buffer, frame, _capacity);
            foreach (var e in events)
            {
                _events.Enqueue(e);
                _recordingEvents?.Add(e);
            }
            while (_events.Count > 0 && _events.Peek().Time < frame.Time - Window) _events.Dequeue();
            _recording?.Add(frame);
            foreach (var pending in _pending)
            {
                pending.Frames.Add(frame);
                pending.Events.AddRange(events);
                Track(pending.Extremes, pending.Conditions, values);
            }
        }
        EvaluateTriggers(frame, values);
        FinishCaptures(frame.Time);
    }

    /// <summary>Keeps each condition's most extreme value: the highest for "above", the lowest for "below".</summary>
    private static void Track(double?[] extremes, IReadOnlyList<AlertCondition> conditions, IReadOnlyDictionary<string, double?> values)
    {
        for (int i = 0; i < conditions.Count && i < extremes.Length; i++)
        {
            if (!values.TryGetValue(conditions[i].Metric, out var v) || v is not { } x) continue;
            extremes[i] = extremes[i] is not { } e ? x
                : conditions[i].Comparison == Comparison.Above ? Math.Max(e, x) : Math.Min(e, x);
        }
    }

    private void FinishCaptures(DateTime now)
    {
        List<PendingAlert> done;
        lock (_lock)
        {
            done = _pending.Where(p => now >= p.Record.CaptureUntil).ToList();
            foreach (var p in done) _pending.Remove(p);
        }
        foreach (var p in done) _ = Task.Run(() => SaveAlert(p));
    }

    /// <summary>Saves alerts still capturing, cut short. Called on shutdown so a fired alert is never lost.</summary>
    public void FlushAlerts()
    {
        List<PendingAlert> pending;
        lock (_lock)
        {
            pending = _pending.ToList();
            _pending.Clear();
        }
        foreach (var p in pending) SaveAlert(p);
    }

    private void SaveAlert(PendingAlert p)
    {
        var record = p.Record;
        lock (_lock)
            record.Extremes = p.Conditions.Select((c, i) => new ConditionExtreme(c.Label, c.Unit, c.Comparison, p.Extremes[i])).ToList();
        try
        {
            record.Path = Save(Build(p.Frames, p.Events, record.Rule));
        }
        catch (Exception ex)
        {
            record.Error = ex.Message;
        }
        AlertSaved?.Invoke(record);
    }

    private List<TraceEvent> DiffProcesses(Snapshot s)
    {
        var current = s.Processes.ToDictionary(p => p.Identity, p => p.Name);
        var events = new List<TraceEvent>();
        if (_known is not null)
        {
            foreach (var (id, name) in current)
                if (!_known.ContainsKey(id)) events.Add(new TraceEvent(s.Timestamp, TraceEventKind.Started, id.Pid, name));
            foreach (var (id, name) in _known)
                if (!current.ContainsKey(id)) events.Add(new TraceEvent(s.Timestamp, TraceEventKind.Exited, id.Pid, name));
        }
        _known = current;
        return events;
    }

    private void EvaluateTriggers(TraceFrame frame, IReadOnlyDictionary<string, double?> values)
    {
        foreach (var rule in Rules)
        {
            var conditions = rule.Conditions;
            if (!rule.Enabled || !rule.Holds(values))
            {
                rule.Consecutive = 0;
                rule.StreakExtremes = [];
                continue;
            }

            if (rule.StreakExtremes.Length != conditions.Count) rule.StreakExtremes = new double?[conditions.Count];
            Track(rule.StreakExtremes, conditions, values);
            rule.Consecutive++;
            if (rule.Consecutive * _interval < rule.Seconds || frame.Time - rule.LastFired < TriggerCooldown) continue;

            var extremes = rule.StreakExtremes;
            rule.LastFired = frame.Time;
            rule.Consecutive = 0;
            rule.StreakExtremes = [];
            var top = frame.Top.FirstOrDefault();
            var record = new AlertRecord
            {
                Time = frame.Time,
                Rule = rule.Title,
                Reason = rule.Describe(),
                TopProcess = top is null ? null : top.Cpu is { } cpu ? $"{top.Name} ({cpu:0.0}% CPU)" : top.Name,
                CaptureUntil = frame.Time + CaptureAfter,
            };
            lock (_lock)
            {
                var marker = new TraceEvent(frame.Time, TraceEventKind.Trigger, 0, rule.Title);
                _events.Enqueue(marker);
                _recordingEvents?.Add(marker);
                _pending.Add(new PendingAlert(record, conditions, extremes, _buffer.ToList(), _events.ToList()));
            }
            AlertFired?.Invoke(record);
        }
    }

    private static void Push<T>(Queue<T> queue, T item, int capacity)
    {
        queue.Enqueue(item);
        while (queue.Count > capacity) queue.Dequeue();
    }

    /// <summary>The rolling buffer as a trace: the last <see cref="Window"/> of history.</summary>
    public Trace CaptureBuffer(string reason)
    {
        lock (_lock) return Build(_buffer.ToList(), _events.ToList(), reason);
    }

    public void StartRecording()
    {
        lock (_lock)
        {
            _recording = [];
            _recordingEvents = [];
            RecordingStarted = DateTime.Now;
            RecordingName = FileName(RecordingStarted.Value, ManualReason);
        }
    }

    private const string ManualReason = "Manual recording";

    /// <summary>Ends the recording. The trace keeps the name it was given at the start.</summary>
    public (Trace Trace, string Name)? StopRecording()
    {
        lock (_lock)
        {
            if (_recording is null) return null;
            var trace = Build(_recording, _recordingEvents!, ManualReason);
            string name = RecordingName!;
            _recording = null;
            _recordingEvents = null;
            RecordingStarted = null;
            RecordingName = null;
            return (trace, name);
        }
    }

    private Trace Build(List<TraceFrame> frames, List<TraceEvent> events, string reason) =>
        new(1, Environment.MachineName, frames.Count > 0 ? frames[0].Time : DateTime.Now, _interval, reason, frames, events);

    public string Save(Trace trace, string? name = null)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, (name ?? FileName(trace.Start, trace.Reason ?? "trace")) + TraceFile.Extension);
        TraceFile.Save(trace, path);
        return path;
    }

    private static string FileName(DateTime start, string reason)
    {
        // A long multi-condition rule would otherwise exceed what Windows allows in a path.
        string name = Sanitize(reason);
        return $"{start:yyyy-MM-dd HH-mm-ss} {(name.Length > 80 ? name[..80].TrimEnd() + "…" : name)}";
    }

    private static string Sanitize(string s) =>
        new(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '%' ? '-' : c).ToArray());
}

/// <summary>.tmngtrace: gzip-compressed JSON of a <see cref="Trace"/>.</summary>
public static class TraceFile
{
    public const string Extension = ".tmngtrace";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static void Save(Trace trace, string path)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        JsonSerializer.Serialize(gzip, trace, Options);
    }

    public static Trace Load(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Trace>(gzip, Options) ?? throw new InvalidDataException("The trace file is empty.");
    }

    /// <summary>One row per frame; missing readings are empty cells, not zeros.</summary>
    public static string ExportCsv(Trace trace, string path)
    {
        var csv = new StringBuilder();
        csv.AppendLine("time,cpu_pct,kernel_pct,clock_mhz,gpu_pct,mem_used_bytes,mem_total_bytes,disk_active_pct,disk_read_bps,disk_write_bps,net_rx_bps,net_tx_bps,cpu_w,gpu_w,cpu_temp_c,gpu_temp_c,top_process");
        foreach (var f in trace.Frames)
        {
            string top = f.Top.Count > 0 ? f.Top[0].Name : "";
            csv.AppendLine(string.Join(',',
                f.Time.ToString("o", CultureInfo.InvariantCulture),
                N(f.Cpu), N(f.Kernel), N(f.ClockMhz), N(f.Gpu), N(f.MemoryUsed), N(f.MemoryTotal), N(f.DiskActive),
                N(f.DiskRead), N(f.DiskWrite), N(f.NetReceive), N(f.NetSend), N(f.CpuWatts), N(f.GpuWatts),
                N(f.CpuTemp), N(f.GpuTemp), $"\"{top.Replace("\"", "\"\"")}\""));
        }
        File.WriteAllText(path, csv.ToString());
        return path;
    }

    private static string N(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
}
