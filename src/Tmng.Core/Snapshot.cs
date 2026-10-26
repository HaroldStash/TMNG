namespace Tmng.Core;

public enum CoreKind : byte { Uniform, Performance, Efficiency }

public enum AdapterKind : byte { Gpu, Npu }

/// <summary>A process is identified by PID plus creation time, so a recycled PID is a different process.</summary>
public readonly record struct ProcessIdentity(int Pid, long CreateTime);

public sealed record CpuSample(
    Reading Utilization,
    Reading Kernel,
    Reading FrequencyMhz,
    IReadOnlyList<Reading> Logical,
    IReadOnlyList<CoreKind> LogicalKinds)
{
    public static readonly CpuSample Empty = new(default, default, default, [], []);

    /// <summary>Kernel-mode share of each logical processor, same order as <see cref="Logical"/>.</summary>
    public IReadOnlyList<Reading> LogicalKernel { get; init; } = [];

    /// <summary>
    /// Share of nominal performance the platform currently allows. 100 means unrestricted; lower
    /// means power, thermal or firmware limits are holding the CPU back.
    /// </summary>
    public Reading PerformanceLimit { get; init; }
}

public sealed record MemorySample(
    Reading TotalBytes,
    Reading UsedBytes,
    Reading AvailableBytes,
    Reading CachedBytes,
    Reading CommittedBytes,
    Reading CommitLimitBytes)
{
    public static readonly MemorySample Empty = new(default, default, default, default, default, default);

    public Reading PagedPoolBytes { get; init; }
    public Reading NonPagedPoolBytes { get; init; }
}

public sealed record EngineSample(string Type, Reading Utilization);

public sealed record AdapterSample(string Name, AdapterKind Kind, Reading Utilization, Reading DedicatedBytes, Reading SharedBytes)
{
    /// <summary>Busiest engine of each type (3D, Copy, VideoDecode, Compute_0…).</summary>
    public IReadOnlyList<EngineSample> Engines { get; init; } = [];
}

public sealed record NicSample(
    string Id,
    string Name,
    string Description,
    string Type,
    long? LinkSpeedBitsPerSec,
    string? MacAddress,
    IReadOnlyList<string> IPv4,
    IReadOnlyList<string> IPv6,
    Reading ReceiveBytesPerSec,
    Reading SendBytesPerSec,
    long TotalReceived,
    long TotalSent);

public sealed record NetworkSample(Reading ReceiveBytesPerSec, Reading SendBytesPerSec)
{
    public static readonly NetworkSample Empty = new(default, default);

    public IReadOnlyList<NicSample> Interfaces { get; init; } = [];
}

/// <summary>One physical disk; <see cref="Volumes"/> are its drive letters, e.g. "C: H:".</summary>
public sealed record DiskDeviceSample(int Number, string Volumes, Reading ActivePercent, Reading ReadBytesPerSec, Reading WriteBytesPerSec, Reading ResponseMs);

public sealed record DiskSample(Reading ActivePercent, Reading ReadBytesPerSec, Reading WriteBytesPerSec)
{
    public static readonly DiskSample Empty = new(default, default, default);

    public IReadOnlyList<DiskDeviceSample> Devices { get; init; } = [];
}

public sealed record SensorReading(string Hardware, string Name, string Type, string Unit, Reading Value);

public sealed record SensorSample(
    Reading CpuTemperatureC,
    Reading CpuPackageWatts,
    Reading GpuTemperatureC,
    Reading GpuWatts,
    IReadOnlyList<SensorReading> All)
{
    public static readonly SensorSample Empty = new(default, default, default, default, []);
}

public sealed record PowerSample(bool? OnAcPower, Reading BatteryPercent)
{
    public static readonly PowerSample Empty = new(null, default);
}

public sealed record ProcessSample(
    ProcessIdentity Identity,
    int ParentPid,
    string Name,
    string? ImagePath,
    string? UserName,
    int SessionId,
    int Threads,
    int Handles,
    int BasePriority,
    DateTime? StartTime,
    Reading Cpu,
    TimeSpan CpuTime,
    long PrivateWorkingSet,
    long PrivateBytes,
    long WorkingSet,
    long PageFaults,
    Reading IoReadBytesPerSec,
    Reading IoWriteBytesPerSec,
    Reading Gpu,
    Reading Npu)
{
    // From the kernel I/O trace, which needs administrator rights. Unlike IoRead/IoWrite, these
    // separate physical disk transfers from network traffic.
    public Reading NetReceiveBytesPerSec { get; init; }
    public Reading NetSendBytesPerSec { get; init; }
    public Reading DiskReadBytesPerSec { get; init; }
    public Reading DiskWriteBytesPerSec { get; init; }

    // From the executable's version resource; null when the image is unreadable or carries none.
    public string? Description { get; init; }
    public string? Company { get; init; }

    /// <summary>The executable's description ("Google Chrome") when it has one, else the image name.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : Description;
}

public sealed record ProviderHealth(string Provider, bool Healthy, string? Detail);

public sealed record Snapshot(
    long Generation,
    DateTime Timestamp,
    CpuSample Cpu,
    MemorySample Memory,
    IReadOnlyList<AdapterSample> Adapters,
    NetworkSample Network,
    DiskSample Disk,
    SensorSample Sensors,
    PowerSample Power,
    IReadOnlyList<ProcessSample> Processes,
    IReadOnlyList<ProviderHealth> Health);

public sealed record InfoItem(string Section, string Name, string Value);

public interface ITelemetrySource : IDisposable
{
    /// <summary>Include this application's own process in process lists.</summary>
    bool IncludeSelf { get; set; }

    Snapshot Sample(long generation);
}
