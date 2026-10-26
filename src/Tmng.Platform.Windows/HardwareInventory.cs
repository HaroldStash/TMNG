using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Tmng.Platform.Windows.Interop;

namespace Tmng.Platform.Windows;

public sealed record DiskInfo(int Number, string Model, string MediaType, string Bus, long SizeBytes);

public sealed record GpuDriverInfo(string Version, string? Date);

/// <summary>
/// Hardware facts that do not change while TMNG runs. Collected once, off the UI thread; any
/// part that cannot be read stays null and is shown as unavailable.
/// </summary>
public sealed record HardwareInventory
{
    public string? CpuName { get; init; }
    public int? BaseMhz { get; init; }
    public int Sockets { get; init; }
    public int Cores { get; init; }
    public int Logical { get; init; }
    public long? L1Bytes { get; init; }
    public long? L2Bytes { get; init; }
    public long? L3Bytes { get; init; }
    public bool? VirtualizationEnabled { get; init; }
    public bool? HypervisorPresent { get; init; }

    public string? MemoryType { get; init; }
    public int? MemorySpeed { get; init; }
    public int? MemoryRatedSpeed { get; init; }
    public int? SlotsUsed { get; init; }
    public int? SlotsTotal { get; init; }
    public string? FormFactor { get; init; }
    public string? MemoryManufacturer { get; init; }
    public long? InstalledBytes { get; init; }

    public IReadOnlyDictionary<int, DiskInfo> Disks { get; init; } = new Dictionary<int, DiskInfo>();
    public IReadOnlyDictionary<string, GpuDriverInfo> GpuDrivers { get; init; } = new Dictionary<string, GpuDriverInfo>();

    public static HardwareInventory Collect()
    {
        var inventory = new HardwareInventory();
        inventory = Try(inventory, CollectCpu);
        inventory = Try(inventory, CollectMemory);
        inventory = Try(inventory, i => i with { Disks = CollectDisks() });
        inventory = Try(inventory, i => i with { GpuDrivers = CollectGpuDrivers() });
        return inventory;
    }

    private static HardwareInventory Try(HardwareInventory inventory, Func<HardwareInventory, HardwareInventory> step)
    {
        try
        {
            return step(inventory);
        }
        catch (Exception)
        {
            return inventory; // that part stays unavailable
        }
    }

    private static unsafe HardwareInventory CollectCpu(HardwareInventory inventory)
    {
        using var processor = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var topology = CpuTopology.Read();
        long l1 = 0, l2 = 0, l3 = 0;
        int sockets = 0;

        uint length = 0;
        const int relationAll = 0xFFFF, relationCache = 2, relationPackage = 3;
        Kernel32.GetLogicalProcessorInformationEx(relationAll, null, ref length);
        var buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            if (Kernel32.GetLogicalProcessorInformationEx(relationAll, p, ref length) != 0)
            {
                for (uint offset = 0; offset < length;)
                {
                    byte* entry = p + offset;
                    int relationship = *(int*)entry;
                    uint size = *(uint*)(entry + 4);
                    if (relationship == relationPackage) sockets++;
                    else if (relationship == relationCache)
                    {
                        // CACHE_RELATIONSHIP: Level@8, CacheSize@12. One entry per cache instance, so totals add up.
                        byte level = entry[8];
                        uint cacheSize = *(uint*)(entry + 12);
                        if (level == 1) l1 += cacheSize;
                        else if (level == 2) l2 += cacheSize;
                        else if (level == 3) l3 += cacheSize;
                    }
                    if (size == 0) break;
                    offset += size;
                }
            }
        }

        bool? virtualization = null, hypervisor = null;
        using (var search = new ManagementObjectSearcher("SELECT VirtualizationFirmwareEnabled FROM Win32_Processor"))
            foreach (var o in search.Get()) virtualization = o["VirtualizationFirmwareEnabled"] as bool?;
        using (var search = new ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem"))
            foreach (var o in search.Get()) hypervisor = o["HypervisorPresent"] as bool?;

        return inventory with
        {
            CpuName = (processor?.GetValue("ProcessorNameString") as string)?.Trim(),
            BaseMhz = processor?.GetValue("~MHz") as int?,
            Sockets = Math.Max(1, sockets),
            Cores = topology.Cores,
            Logical = topology.Keys.Length,
            L1Bytes = l1 > 0 ? l1 : null,
            L2Bytes = l2 > 0 ? l2 : null,
            L3Bytes = l3 > 0 ? l3 : null,
            VirtualizationEnabled = virtualization,
            HypervisorPresent = hypervisor,
        };
    }

    private static HardwareInventory CollectMemory(HardwareInventory inventory)
    {
        var modules = new List<ManagementBaseObject>();
        using (var search = new ManagementObjectSearcher("SELECT Manufacturer, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory"))
            modules.AddRange(search.Get().Cast<ManagementBaseObject>());

        int? slots = null;
        using (var search = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
            foreach (var o in search.Get()) slots = (slots ?? 0) + Convert.ToInt32(o["MemoryDevices"] ?? 0);

        var first = modules.FirstOrDefault();
        string? type = first?["SMBIOSMemoryType"] is { } t ? Convert.ToInt32(t) switch
        {
            20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3",
            30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => null,
        } : null;
        string? form = first?["FormFactor"] is { } f ? Convert.ToInt32(f) switch
        {
            8 => "DIMM", 12 => "SODIMM", 13 => "SRIMM", 0 => null, _ => null,
        } : null;

        Kernel32.GetPhysicallyInstalledSystemMemory(out ulong kilobytes);
        return inventory with
        {
            MemoryType = type,
            MemorySpeed = first?["ConfiguredClockSpeed"] is { } c ? Convert.ToInt32(c) : null,
            MemoryRatedSpeed = first?["Speed"] is { } s ? Convert.ToInt32(s) : null,
            SlotsUsed = modules.Count > 0 ? modules.Count : null,
            SlotsTotal = slots,
            FormFactor = form,
            MemoryManufacturer = (first?["Manufacturer"] as string)?.Trim(),
            InstalledBytes = kilobytes > 0 ? (long)kilobytes * 1024 : null,
        };
    }

    private static Dictionary<int, DiskInfo> CollectDisks()
    {
        var disks = new Dictionary<int, DiskInfo>();
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        using var search = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk"));
        foreach (var o in search.Get())
        {
            if (!int.TryParse(o["DeviceId"]?.ToString(), out int number)) continue;
            string media = Convert.ToInt32(o["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified" };
            string bus = Convert.ToInt32(o["BusType"] ?? 0) switch
            {
                1 => "SCSI", 3 => "ATA", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD",
                15 => "Virtual", 16 => "Storage Spaces", 17 => "NVMe", _ => "Other",
            };
            disks[number] = new DiskInfo(number, (o["FriendlyName"] as string)?.Trim() ?? $"Disk {number}", media, bus,
                Convert.ToInt64(o["Size"] ?? 0L));
        }
        return disks;
    }

    /// <summary>Driver version and date for each display adapter, keyed by its driver description.</summary>
    public static Dictionary<string, GpuDriverInfo> CollectGpuDrivers()
    {
        var result = new Dictionary<string, GpuDriverInfo>(StringComparer.OrdinalIgnoreCase);
        using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        foreach (var sub in display?.GetSubKeyNames().Where(n => n.All(char.IsDigit)) ?? [])
        {
            using var key = display!.OpenSubKey(sub);
            if (key?.GetValue("DriverDesc") is string name && key.GetValue("DriverVersion") is string version)
                result[name] = new GpuDriverInfo(version, key.GetValue("DriverDate") as string);
        }
        return result;
    }
}

public static unsafe partial class PowerPlan
{
    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(nint root, out nint scheme);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadFriendlyName(nint root, nint scheme, nint subGroup, nint setting, byte* buffer, ref uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    /// <summary>Name of the active Windows power plan, e.g. "Balanced"; null if it cannot be read.</summary>
    public static string? ActiveName()
    {
        if (PowerGetActiveScheme(0, out nint scheme) != 0) return null;
        try
        {
            uint size = 512;
            byte* buffer = stackalloc byte[512];
            if (PowerReadFriendlyName(0, scheme, 0, 0, buffer, ref size) != 0) return null;
            return new string((char*)buffer).Trim();
        }
        finally
        {
            LocalFree(scheme);
        }
    }
}
