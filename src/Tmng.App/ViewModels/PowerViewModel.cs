using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tmng.App.Theme;
using Tmng.Core;
using Tmng.Platform.Windows;

namespace Tmng.App.ViewModels;

public sealed partial class SensorRow(string hardware, string name, string type) : ObservableObject
{
    public string Hardware { get; } = hardware;
    public string Name { get; } = name;
    public string Type { get; } = type;

    [ObservableProperty] private string _value = "—";
}

/// <summary>Every sensor LibreHardwareMonitor exposes, live. The first cut of Power and Frequency.</summary>
public sealed partial class PowerViewModel(Appearance appearance) : PageViewModel("Power & Freq", "", appearance)
{
    private readonly Dictionary<string, SensorRow> _rows = [];

    public ObservableCollection<SensorRow> Sensors { get; } = [];

    public string ElevationNote => Elevation.IsElevated
        ? "Running as administrator. If CPU sensors still show \"—\", install the PawnIO driver (winget install namazso.PawnIO)."
        : "Package and per-core power come from Windows' own energy meters and need nothing extra. CPU temperature, voltages and per-core clocks show \"admin\" until TMNG runs as administrator with the PawnIO driver installed.";

    [ObservableProperty] private string _cpuPower = "—";
    [ObservableProperty] private string _gpuPower = "—";
    [ObservableProperty] private string _clock = "—";
    [ObservableProperty] private string _battery = "—";

    public override void Update(Snapshot s)
    {
        CpuPower = Fmt.Watts(s.Sensors.CpuPackageWatts);
        GpuPower = Fmt.Watts(s.Sensors.GpuWatts);
        Clock = Fmt.Frequency(s.Cpu.FrequencyMhz);
        Battery = Fmt.Percent(s.Power.BatteryPercent, 0);

        foreach (var sensor in s.Sensors.All)
        {
            string key = $"{sensor.Hardware}|{sensor.Type}|{sensor.Name}";
            if (!_rows.TryGetValue(key, out var row))
            {
                row = new SensorRow(sensor.Hardware, sensor.Name, sensor.Type);
                _rows[key] = row;
                Sensors.Add(row);
            }
            row.Value = sensor.Value.HasValue ? $"{sensor.Value.Value:0.##} {sensor.Unit}" : Fmt.Missing(sensor.Value.State);
        }
    }
}
