using LibreHardwareMonitor.Hardware;
using Tmng.Core;

namespace Tmng.Platform.Windows;

/// <summary>
/// Temperatures, power and clocks via LibreHardwareMonitor. CPU temperature and package power
/// come from model-specific registers, which need administrator rights and a kernel driver;
/// without them those readings are reported as AccessDenied, never as zero.
/// </summary>
internal sealed class SensorProvider : IDisposable
{
    private static readonly string[] CpuTemperatureNames = ["CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Tdie", "Package"];
    private static readonly string[] CpuPowerNames = ["CPU Package", "Package"];

    private readonly Task<Computer?> _opening;

    public SensorProvider()
    {
        _opening = Task.Run(Open);
    }

    public string? Problem { get; private set; }

    public bool Ready => _opening.IsCompleted;

    private Computer? Open()
    {
        try
        {
            var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsBatteryEnabled = true };
            computer.Open();
            return computer;
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            return null;
        }
    }

    public SensorSample Read()
    {
        if (!_opening.IsCompleted || _opening.Result is not { } computer) return SensorSample.Empty;

        var all = new List<SensorReading>();
        var cpuTemps = new List<ISensor>();
        var cpuPower = new List<ISensor>();
        var gpus = new List<IHardware>();

        foreach (var hardware in computer.Hardware)
        {
            Visit(hardware, all);
            if (hardware.HardwareType == HardwareType.Cpu)
            {
                cpuTemps.AddRange(hardware.Sensors.Where(s => s.SensorType == SensorType.Temperature && !s.Name.Contains("Distance")));
                cpuPower.AddRange(hardware.Sensors.Where(s => s.SensorType == SensorType.Power));
            }
            else if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                gpus.Add(hardware);
            }
        }

        // Prefer a discrete GPU when an integrated one is also present.
        var gpu = gpus.OrderBy(g => g.HardwareType == HardwareType.GpuIntel ? 1 : 0).FirstOrDefault();

        return new SensorSample(
            Pick(cpuTemps, CpuTemperatureNames, fallbackToMax: true),
            Pick(cpuPower, CpuPowerNames, fallbackToMax: false),
            gpu is null ? Reading.Unsupported : Pick(gpu.Sensors.Where(s => s.SensorType == SensorType.Temperature).ToList(), ["GPU Core"], fallbackToMax: false),
            gpu is null ? Reading.Unsupported : Pick(gpu.Sensors.Where(s => s.SensorType == SensorType.Power).ToList(), ["GPU Package", "GPU Power", "GPU Core"], fallbackToMax: false),
            all);
    }

    private static void Visit(IHardware hardware, List<SensorReading> all)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors)
            all.Add(new SensorReading(hardware.Name, sensor.Name, sensor.SensorType.ToString(), Unit(sensor.SensorType), ReadingOf(sensor)));
        foreach (var sub in hardware.SubHardware) Visit(sub, all);
    }

    /// <summary>
    /// Without elevation LibreHardwareMonitor reports register-backed CPU sensors as 0 rather
    /// than null, and a 0 °C temperature is a missing read, not a measurement. Neither may
    /// surface as a value.
    /// </summary>
    private static Reading ReadingOf(ISensor sensor)
    {
        bool registerBacked = sensor.Hardware.HardwareType == HardwareType.Cpu
            && sensor.SensorType is SensorType.Temperature or SensorType.Power or SensorType.Clock or SensorType.Voltage or SensorType.Current;
        if (registerBacked && !Elevation.IsElevated) return Reading.AccessDenied;
        if (sensor.Value is not { } value) return MissingReading();
        if (sensor.SensorType == SensorType.Temperature && value <= 0) return MissingReading();
        return Reading.Of(value);
    }

    private static Reading Pick(List<ISensor> sensors, string[] preferred, bool fallbackToMax)
    {
        if (sensors.Count == 0) return Elevation.IsElevated ? Reading.Unsupported : Reading.AccessDenied;

        foreach (var name in preferred)
        {
            var match = sensors.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null && ReadingOf(match) is { HasValue: true } reading) return reading;
        }

        var readings = sensors.Select(ReadingOf).ToList();
        var measured = readings.Where(r => r.HasValue).ToList();
        if (measured.Count > 0)
            return fallbackToMax ? measured.MaxBy(r => r.Value) : measured[0];
        return readings.FirstOrDefault(r => r.State == Availability.AccessDenied, MissingReading());
    }

    private static Reading MissingReading() => Elevation.IsElevated ? Reading.Unavailable : Reading.AccessDenied;

    private static string Unit(SensorType type) => type switch
    {
        SensorType.Temperature => "°C",
        SensorType.Power => "W",
        SensorType.Clock => "MHz",
        SensorType.Load or SensorType.Level or SensorType.Control => "%",
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Fan => "RPM",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.Frequency => "Hz",
        SensorType.Energy => "mWh",
        _ => "",
    };

    public void Dispose()
    {
        if (_opening.IsCompletedSuccessfully) _opening.Result?.Close();
    }
}
