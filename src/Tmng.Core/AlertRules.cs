namespace Tmng.Core;

public enum Comparison { Above, Below }

public enum MatchMode { All, Any }

/// <summary>A metric an alert can watch: a system reading or any sensor the machine reports.</summary>
public sealed record MetricInfo(string Key, string Label, string Group, string Unit)
{
    public override string ToString() => $"{Group} · {Label}";
}

/// <summary>Every value an alert can watch, read from one snapshot. Null means not measured this tick.</summary>
public static class AlertMetrics
{
    public static readonly IReadOnlyList<MetricInfo> System =
    [
        new("cpu", "CPU usage", "System", "%"),
        new("gpu", "GPU usage", "System", "%"),
        new("memory", "Memory used", "System", "%"),
        new("disk", "Disk busy", "System", "%"),
        new("network", "Network (in + out)", "System", "MB/s"),
        new("cputemp", "CPU temperature", "System", "°C"),
        new("gputemp", "GPU temperature", "System", "°C"),
        new("cpuwatts", "CPU power", "System", "W"),
        new("gpuwatts", "GPU power", "System", "W"),
        new("clock", "CPU clock", "System", "MHz"),
    ];

    public static string SensorKey(SensorReading r) => $"sensor/{r.Hardware}/{r.Type}/{r.Name}";

    /// <summary>Every sensor in the snapshot as a metric, grouped by kind and device ("Temperature · AMD Ryzen 9").</summary>
    public static IEnumerable<MetricInfo> Sensors(Snapshot s) =>
        s.Sensors.All.Select(r => new MetricInfo(SensorKey(r), r.Name, $"{r.Type} · {r.Hardware}", r.Unit));

    public static Dictionary<string, double?> Read(Snapshot s)
    {
        var memory = s.Memory.UsedBytes.HasValue && s.Memory.TotalBytes.HasValue && s.Memory.TotalBytes.Value > 0
            ? s.Memory.UsedBytes.Value / s.Memory.TotalBytes.Value * 100
            : (double?)null;
        var network = s.Network.ReceiveBytesPerSec.HasValue && s.Network.SendBytesPerSec.HasValue
            ? (s.Network.ReceiveBytesPerSec.Value + s.Network.SendBytesPerSec.Value) / 1e6
            : (double?)null;

        var values = new Dictionary<string, double?>(s.Sensors.All.Count + System.Count)
        {
            ["cpu"] = s.Cpu.Utilization.Nullable,
            ["gpu"] = s.PrimaryGpu()?.Utilization.Nullable,
            ["memory"] = memory,
            ["disk"] = s.Disk.ActivePercent.Nullable,
            ["network"] = network,
            ["cputemp"] = s.Sensors.CpuTemperatureC.Nullable,
            ["gputemp"] = s.Sensors.GpuTemperatureC.Nullable,
            ["cpuwatts"] = s.Sensors.CpuPackageWatts.Nullable,
            ["gpuwatts"] = s.Sensors.GpuWatts.Nullable,
            ["clock"] = s.Cpu.FrequencyMhz.Nullable,
        };
        foreach (var sensor in s.Sensors.All) values[SensorKey(sensor)] = sensor.Value.Nullable;
        return values;
    }
}

/// <summary>One test inside a rule: a metric above or below a value.</summary>
public sealed class AlertCondition
{
    public string Metric { get; set; } = "cpu";
    // Kept with the condition so it still reads sensibly when its sensor is missing (not elevated, device removed).
    public string Label { get; set; } = "CPU usage";
    public string Unit { get; set; } = "%";
    public Comparison Comparison { get; set; }
    public double Threshold { get; set; } = 90;

    public string Describe() =>
        $"{Label} {(Comparison == Comparison.Above ? "above" : "below")} {Threshold:0.##} {Unit}".TrimEnd();

    /// <summary>True or false when measured; null when the metric has no value this tick.</summary>
    public bool? Test(IReadOnlyDictionary<string, double?> values) =>
        values.TryGetValue(Metric, out var v) && v is { } x
            ? Comparison == Comparison.Above ? x > Threshold : x < Threshold
            : null;
}

/// <summary>
/// Conditions that must hold, all or any of them, for a number of seconds in a row. Conditions
/// is replaced as a whole when edited, never changed in place, because the sampling thread reads it.
/// </summary>
public sealed class AlertRule
{
    internal int Consecutive;
    internal DateTime LastFired = DateTime.MinValue;
    internal double?[] StreakExtremes = [];

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>One of the starter rules: editable, but not deletable.</summary>
    public bool BuiltIn { get; set; }
    public MatchMode Match { get; set; }
    public int Seconds { get; set; } = 10;
    public IReadOnlyList<AlertCondition> Conditions { get; set; } = [];

    public string Describe() =>
        string.Join(Match == MatchMode.All ? " and " : " or ", Conditions.Select(c => c.Describe())) + $" for {Seconds} s";

    public string Title => string.IsNullOrWhiteSpace(Name) ? Describe() : Name;

    /// <summary>
    /// A missing reading never counts as a match: an all-rule cannot hold while any of its metrics
    /// is unmeasured, and an any-rule only listens to the metrics it can measure.
    /// </summary>
    public bool Holds(IReadOnlyDictionary<string, double?> values)
    {
        var conditions = Conditions;
        if (conditions.Count == 0) return false;
        var results = conditions.Select(c => c.Test(values)).ToList();
        return Match == MatchMode.All ? results.All(r => r == true) : results.Any(r => r == true);
    }

    public static List<AlertRule> Defaults() =>
    [
        Starter("CPU busy", "cpu", 90, 10),
        Starter("GPU busy", "gpu", 95, 10),
        Starter("Memory nearly full", "memory", 90, 5),
        Starter("Disk busy", "disk", 95, 10),
        Starter("CPU hot", "cputemp", 90, 5),
        Starter("GPU hot", "gputemp", 85, 5),
        Starter("Network busy", "network", 100, 10),
    ];

    private static AlertRule Starter(string name, string metric, double threshold, int seconds)
    {
        var info = AlertMetrics.System.First(m => m.Key == metric);
        return new AlertRule
        {
            Id = "builtin." + metric,
            Name = name,
            BuiltIn = true,
            Seconds = seconds,
            Conditions = [new AlertCondition { Metric = metric, Label = info.Label, Unit = info.Unit, Threshold = threshold }],
        };
    }
}

/// <summary>The most extreme value a condition reached during an alert: the highest for "above", the lowest for "below".</summary>
public sealed record ConditionExtreme(string Label, string Unit, Comparison Comparison, double? Value)
{
    public override string ToString() =>
        $"{Label} {(Comparison == Comparison.Above ? "peak" : "low")} {(Value is { } v ? $"{v:0.#} {Unit}".TrimEnd() : "—")}";
}
