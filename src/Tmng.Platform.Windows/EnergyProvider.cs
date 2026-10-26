using System.Text.RegularExpressions;
using Tmng.Core;

namespace Tmng.Platform.Windows;

/// <summary>
/// Windows' own Energy Meter counters (the Energy Metering Interface over RAPL). They give CPU
/// package and per-core power with no driver and no elevation where the platform exposes them.
/// </summary>
internal sealed partial class EnergyProvider(PdhQuery query)
{
    private readonly PdhCounter? _power = query.Add(@"\Energy Meter(*)\Power");

    [GeneratedRegex(@"_core(\d+)_core$", RegexOptions.IgnoreCase)]
    private static partial Regex CoreInstance();

    public bool Supported => _power is not null;

    public (Reading Package, IReadOnlyList<SensorReading> Meters) Read()
    {
        if (_power?.Read() is not { } values) return (Supported ? Reading.Unavailable : Reading.Unsupported, []);

        double? package = null;
        var cores = new List<(int Index, double Watts)>();
        foreach (var (instance, milliwatts) in values)
        {
            if (instance.EndsWith("_pkg", StringComparison.OrdinalIgnoreCase))
                package = (package ?? 0) + milliwatts / 1000;
            else if (CoreInstance().Match(instance) is { Success: true } m)
                cores.Add((int.Parse(m.Groups[1].Value), milliwatts / 1000));
        }

        var meters = new List<SensorReading>();
        if (package is { } p) meters.Add(new SensorReading("Windows Energy Meter", "CPU Package", "Power", "W", Reading.Of(p)));
        foreach (var (index, watts) in cores.OrderBy(c => c.Index))
            meters.Add(new SensorReading("Windows Energy Meter", $"Core #{index}", "Power", "W", Reading.Of(watts)));

        return (package is { } pkg ? Reading.Of(pkg) : Reading.Unsupported, meters);
    }
}
