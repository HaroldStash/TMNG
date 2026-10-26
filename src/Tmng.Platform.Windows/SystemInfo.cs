using System.Runtime.InteropServices;
using Microsoft.Win32;
using Tmng.Core;
using Tmng.Platform.Windows.Interop;

namespace Tmng.Platform.Windows;

public static class SystemInfo
{
    public static IReadOnlyList<InfoItem> Collect()
    {
        var items = new List<InfoItem>();
        void Add(string section, string name, string? value) => items.Add(new InfoItem(section, name, value ?? "—"));

        using var version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string? build = version?.GetValue("CurrentBuild") as string;
        string? product = version?.GetValue("ProductName") as string;
        // ProductName still says "Windows 10" on Windows 11; the build number is authoritative.
        if (product is not null && int.TryParse(build, out int buildNumber) && buildNumber >= 22000)
            product = product.Replace("Windows 10", "Windows 11");

        const string os = "Operating system";
        Add(os, "Edition", product);
        Add(os, "Version", version?.GetValue("DisplayVersion") as string);
        Add(os, "Build", build is null ? null : $"{build}.{version?.GetValue("UBR")}");
        Add(os, "Architecture", RuntimeInformation.OSArchitecture.ToString());
        Add(os, "Computer name", Environment.MachineName);
        Add(os, "Uptime", Fmt.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64)));
        Add(os, "Elevated", Elevation.IsElevated ? "Yes" : "No");

        const string cpu = "Processor";
        using var processor = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var topology = CpuTopology.Read();
        Add(cpu, "Name", (processor?.GetValue("ProcessorNameString") as string)?.Trim());
        Add(cpu, "Base clock", processor?.GetValue("~MHz") is int mhz ? $"{mhz} MHz" : null);
        Add(cpu, "Physical cores", topology.Cores > 0 ? topology.Cores.ToString() : null);
        Add(cpu, "Logical processors", topology.Keys.Length.ToString());
        if (topology.PerformanceCores > 0)
        {
            Add(cpu, "Performance cores", topology.PerformanceCores.ToString());
            Add(cpu, "Efficiency cores", topology.EfficiencyCores.ToString());
        }

        const string memory = "Memory";
        Add(memory, "Installed", Kernel32.GetPhysicallyInstalledSystemMemory(out ulong kb) != 0 ? Fmt.Bytes(kb * 1024.0) : null);
        var status = new Kernel32.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Kernel32.MemoryStatusEx>() };
        Add(memory, "Usable", Kernel32.GlobalMemoryStatusEx(ref status) != 0 ? Fmt.Bytes(status.TotalPhys) : null);

        const string accelerators = "Graphics and accelerators";
        var adapters = GpuProvider.EnumerateAdapters();
        if (adapters.Count == 0) Add(accelerators, "Adapters", "None reported");
        foreach (var adapter in adapters)
            Add(accelerators, adapter.Kind == AdapterKind.Gpu ? "GPU" : "NPU / compute", adapter.Name);

        return items;
    }
}
