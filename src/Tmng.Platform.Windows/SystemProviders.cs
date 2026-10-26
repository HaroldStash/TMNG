using System.Diagnostics;
using System.Net.NetworkInformation;
using Tmng.Core;
using Tmng.Platform.Windows.Interop;

namespace Tmng.Platform.Windows;

internal sealed class MemoryProvider
{
    public MemorySample Read()
    {
        var status = new Kernel32.MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Kernel32.MemoryStatusEx>() };
        if (Kernel32.GlobalMemoryStatusEx(ref status) == 0) return MemorySample.Empty;

        Reading cached = default, committed = default, limit = default, paged = default, nonPaged = default;
        uint size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Kernel32.PerformanceInformation>();
        if (Kernel32.K32GetPerformanceInfo(out var perf, size) != 0)
        {
            double page = perf.PageSize;
            cached = Reading.Of(perf.SystemCache * page);
            committed = Reading.Of(perf.CommitTotal * page);
            limit = Reading.Of(perf.CommitLimit * page);
            paged = Reading.Of(perf.KernelPaged * page);
            nonPaged = Reading.Of(perf.KernelNonpaged * page);
        }

        return new MemorySample(
            Reading.Of(status.TotalPhys),
            Reading.Of(status.TotalPhys - status.AvailPhys),
            Reading.Of(status.AvailPhys),
            cached,
            committed,
            limit)
        {
            PagedPoolBytes = paged,
            NonPagedPoolBytes = nonPaged,
        };
    }
}

/// <summary>
/// Per physical disk from the PhysicalDisk counters. Instances are named "&lt;number&gt; &lt;letters&gt;",
/// e.g. "4 C: H:"; "_Total" is every disk combined.
/// </summary>
internal sealed class DiskProvider(PdhQuery query)
{
    private readonly PdhCounter? _idle = query.Add(@"\PhysicalDisk(*)\% Idle Time");
    private readonly PdhCounter? _read = query.Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
    private readonly PdhCounter? _write = query.Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
    private readonly PdhCounter? _response = query.Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");

    public DiskSample Read()
    {
        var idle = _idle?.Read();
        var read = _read?.Read();
        var write = _write?.Read();
        var response = _response?.Read();

        Reading Active(string instance) => Get(idle, _idle, instance).Map(v => Math.Clamp(100 - v, 0, 100));

        var devices = new List<DiskDeviceSample>();
        foreach (var instance in (idle ?? read ?? []).Keys.Where(k => k != "_Total"))
        {
            int space = instance.IndexOf(' ');
            if (!int.TryParse(space < 0 ? instance : instance[..space], out int number)) continue;
            devices.Add(new DiskDeviceSample(number, space < 0 ? "" : instance[(space + 1)..].Trim(),
                Active(instance), Get(read, _read, instance), Get(write, _write, instance),
                Get(response, _response, instance).Map(seconds => seconds * 1000)));
        }

        return new DiskSample(Active("_Total"), Get(read, _read, "_Total"), Get(write, _write, "_Total"))
        {
            Devices = devices.OrderBy(d => d.Number).ToList(),
        };
    }

    private static Reading Get(Dictionary<string, double>? values, PdhCounter? counter, string instance) =>
        counter is null ? Reading.Unsupported
        : values is not null && values.TryGetValue(instance, out var v) ? Reading.Of(v)
        : Reading.Unavailable;
}

internal sealed class NetworkProvider
{
    private sealed record NicInfo(string Name, string Description, string Type, long? Speed, string? Mac, IReadOnlyList<string> V4, IReadOnlyList<string> V6);

    private Dictionary<string, (long Rx, long Tx)> _previous = [];
    private readonly Dictionary<string, NicInfo> _info = [];
    private long _lastTimestamp;
    private DateTime _infoRefreshed;

    public NetworkSample Read()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = _lastTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;

        // Addresses and link speed change rarely; refresh them every 30 seconds rather than every tick.
        bool refreshInfo = DateTime.Now - _infoRefreshed > TimeSpan.FromSeconds(30);
        if (refreshInfo)
        {
            _info.Clear();
            _infoRefreshed = DateTime.Now;
        }

        long rx = 0, tx = 0;
        var current = new Dictionary<string, (long Rx, long Tx)>();
        var interfaces = new List<NicSample>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            if (!_info.TryGetValue(nic.Id, out var info))
                _info[nic.Id] = info = Describe(nic);

            // Windows also lists filter-driver "interfaces" (WFP, QoS) and WAN miniports that carry the
            // same bytes as the real adapter. None of them has an IP address, so requiring one counts
            // each byte once. It also skips a physical NIC bound to a Hyper-V switch, whose traffic
            // is already counted on its vEthernet adapter.
            if (info.V4.Count == 0 && info.V6.Count == 0) continue;

            var stats = nic.GetIPStatistics();
            current[nic.Id] = (stats.BytesReceived, stats.BytesSent);
            Reading nicRx = default, nicTx = default;
            if (elapsed > 0 && _previous.TryGetValue(nic.Id, out var prev))
            {
                long dRx = Math.Max(0, stats.BytesReceived - prev.Rx), dTx = Math.Max(0, stats.BytesSent - prev.Tx);
                rx += dRx;
                tx += dTx;
                nicRx = Reading.Of(dRx / elapsed);
                nicTx = Reading.Of(dTx / elapsed);
            }

            interfaces.Add(new NicSample(nic.Id, info.Name, info.Description, info.Type, info.Speed, info.Mac, info.V4, info.V6,
                nicRx, nicTx, stats.BytesReceived, stats.BytesSent));
        }

        _previous = current;
        _lastTimestamp = now;
        if (elapsed <= 0) return NetworkSample.Empty with { Interfaces = interfaces };
        return new NetworkSample(Reading.Of(rx / elapsed), Reading.Of(tx / elapsed)) { Interfaces = interfaces };
    }

    private static NicInfo Describe(NetworkInterface nic)
    {
        var addresses = nic.GetIPProperties().UnicastAddresses.Select(a => a.Address).ToList();
        string mac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
        string type = nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
            NetworkInterfaceType.Ppp => "Dial-up / VPN",
            _ => nic.NetworkInterfaceType.ToString(),
        };
        return new NicInfo(nic.Name, nic.Description, type, nic.Speed > 0 ? nic.Speed : null, mac.Length > 0 ? mac : null,
            addresses.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(a => a.ToString()).ToList(),
            addresses.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6).Select(a => a.ToString()).ToList());
    }
}

internal sealed class PowerProvider
{
    public PowerSample Read()
    {
        if (Kernel32.GetSystemPowerStatus(out var status) == 0) return PowerSample.Empty;

        bool? onAc = status.AcLineStatus switch { 0 => false, 1 => true, _ => null };
        Reading battery = (status.BatteryFlag & 128) != 0 ? Reading.Unsupported
            : status.BatteryLifePercent == 255 ? Reading.Unavailable
            : Reading.Of(status.BatteryLifePercent);
        return new PowerSample(onAc, battery);
    }
}
