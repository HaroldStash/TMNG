using System.Security.Principal;
using Tmng.Core;

namespace Tmng.Platform.Windows;

public static class Elevation
{
    public static bool IsElevated { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}

/// <summary>
/// Composes the native providers into one snapshot per tick. Each provider is isolated: a
/// failure marks that provider unhealthy and leaves its readings unavailable, it never zeroes them.
/// </summary>
public sealed class WindowsTelemetrySource : ITelemetrySource
{
    private readonly PdhQuery _pdh = new();
    private readonly CpuProvider _cpu;
    private readonly GpuProvider _gpu;
    private readonly DiskProvider _disk;
    private readonly EnergyProvider _energy;
    private readonly MemoryProvider _memory = new();
    private readonly NetworkProvider _network = new();
    private readonly PowerProvider _power = new();
    private readonly ProcessProvider _processes = new();
    private readonly SensorProvider _sensors = new();

    public WindowsTelemetrySource()
    {
        _cpu = new CpuProvider(_pdh, CpuTopology.Read());
        _gpu = new GpuProvider(_pdh);
        _disk = new DiskProvider(_pdh);
        _energy = new EnergyProvider(_pdh);
        IoTrace.Shared.Start();
    }

    public bool IncludeSelf { get; set; }

    public Snapshot Sample(long generation)
    {
        var health = new List<ProviderHealth>();
        _pdh.Collect();

        var cpu = Guard("CPU", health, _cpu.Read, CpuSample.Empty);
        var memory = Guard("Memory", health, _memory.Read, MemorySample.Empty);
        var (adapters, usage) = Guard("GPU", health, _gpu.Read, ([], AcceleratorUsage.None));
        var network = Guard("Network", health, _network.Read, NetworkSample.Empty);
        var disk = Guard("Disk", health, _disk.Read, DiskSample.Empty);
        var power = Guard("Power", health, _power.Read, PowerSample.Empty);
        var io = IoTrace.Shared.Collect();
        var processes = Guard("Processes", health, () => _processes.Read(IncludeSelf, usage, io), (IReadOnlyList<ProcessSample>)[]);
        var sensors = Guard("Sensors", health, _sensors.Read, SensorSample.Empty);

        // Windows' own package meter needs no driver, so it wins over the register-based reading.
        var (package, meters) = Guard("Energy", health, _energy.Read, (Reading.Unavailable, (IReadOnlyList<SensorReading>)[]));
        if (package.HasValue || meters.Count > 0)
        {
            sensors = sensors with
            {
                CpuPackageWatts = package.HasValue ? package : sensors.CpuPackageWatts,
                All = [.. meters, .. sensors.All],
            };
        }

        if (_sensors.Problem is { } problem)
            health.Add(new ProviderHealth("Sensors", false, problem));
        else if (_sensors.Ready && !Elevation.IsElevated)
            health.Add(new ProviderHealth("Sensors", false, "Run as administrator for CPU temperature"));
        else if (_sensors.Ready && !SensorDriver.IsInstalled)
            health.Add(new ProviderHealth("Sensors", false, "Install the PawnIO driver for CPU temperature"));

        if (IoTrace.Shared.State == IoTraceState.Failed)
            health.Add(new ProviderHealth("I/O trace", false, IoTrace.Shared.Problem));

        return new Snapshot(generation, DateTime.Now, cpu, memory, adapters, network, disk, sensors, power, processes, health);
    }

    private static T Guard<T>(string provider, List<ProviderHealth> health, Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            health.Add(new ProviderHealth(provider, false, ex.Message));
            return fallback;
        }
    }

    public void Dispose()
    {
        IoTrace.Shared.Dispose();
        _sensors.Dispose();
        _pdh.Dispose();
    }
}
