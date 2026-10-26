using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Tmng.Core;
using Tmng.Platform.Windows.Interop;

namespace Tmng.Platform.Windows;

/// <summary>Every TCP and UDP endpoint with its owning PID, from the extended IP Helper tables.</summary>
public static unsafe class ConnectionProvider
{
    private static readonly string[] TcpStates =
        ["", "Closed", "Listening", "SYN sent", "SYN received", "Established", "FIN wait 1", "FIN wait 2",
         "Close wait", "Closing", "Last ACK", "Time wait", "Delete TCB"];

    public static IReadOnlyList<ConnectionSample> Read()
    {
        var result = new List<ConnectionSample>(512);
        ReadTable(IpHlpApi.AfInet, tcp: true, result);
        ReadTable(IpHlpApi.AfInet6, tcp: true, result);
        ReadTable(IpHlpApi.AfInet, tcp: false, result);
        ReadTable(IpHlpApi.AfInet6, tcp: false, result);

        var io = IoTrace.Shared.Latest;
        for (int i = 0; i < result.Count; i++)
        {
            var (rx, tx) = io.For(result[i]);
            result[i] = result[i] with { ReceiveBytesPerSec = rx, SendBytesPerSec = tx };
        }
        return result;
    }

    private static void ReadTable(int family, bool tcp, List<ConnectionSample> result)
    {
        uint size = 0;
        uint status = tcp
            ? IpHlpApi.GetExtendedTcpTable(null, ref size, 0, family, IpHlpApi.TcpTableOwnerPidAll, 0)
            : IpHlpApi.GetExtendedUdpTable(null, ref size, 0, family, IpHlpApi.UdpTableOwnerPid, 0);
        if (status != IpHlpApi.ErrorInsufficientBuffer || size == 0) return;

        size += 4096; // the table can grow between the two calls
        byte* table = (byte*)NativeMemory.Alloc(size);
        try
        {
            status = tcp
                ? IpHlpApi.GetExtendedTcpTable(table, ref size, 0, family, IpHlpApi.TcpTableOwnerPidAll, 0)
                : IpHlpApi.GetExtendedUdpTable(table, ref size, 0, family, IpHlpApi.UdpTableOwnerPid, 0);
            if (status != 0) return;

            uint count = *(uint*)table;
            byte* rows = table + 4;
            for (int i = 0; i < count; i++)
            {
                if (tcp && family == IpHlpApi.AfInet)
                {
                    var r = ((IpHlpApi.TcpRow*)rows)[i];
                    var local = new IPAddress(r.LocalAddr);
                    bool listening = r.State == 2;
                    result.Add(new ConnectionSample("TCP", local, Port(r.LocalPort),
                        listening ? null : new IPAddress(r.RemoteAddr), listening ? null : Port(r.RemotePort),
                        State(r.State), (int)r.OwningPid, listening ? ExposureOf(local) : Exposure.Connection));
                }
                else if (tcp)
                {
                    var r = ((IpHlpApi.Tcp6Row*)rows)[i];
                    var local = new IPAddress(new ReadOnlySpan<byte>(r.LocalAddr, 16), r.LocalScopeId);
                    bool listening = r.State == 2;
                    result.Add(new ConnectionSample("TCPv6", local, Port(r.LocalPort),
                        listening ? null : new IPAddress(new ReadOnlySpan<byte>(r.RemoteAddr, 16), r.RemoteScopeId),
                        listening ? null : Port(r.RemotePort),
                        State(r.State), (int)r.OwningPid, listening ? ExposureOf(local) : Exposure.Connection));
                }
                else if (family == IpHlpApi.AfInet)
                {
                    var r = ((IpHlpApi.UdpRow*)rows)[i];
                    var local = new IPAddress(r.LocalAddr);
                    result.Add(new ConnectionSample("UDP", local, Port(r.LocalPort), null, null, "Bound", (int)r.OwningPid, ExposureOf(local)));
                }
                else
                {
                    var r = ((IpHlpApi.Udp6Row*)rows)[i];
                    var local = new IPAddress(new ReadOnlySpan<byte>(r.LocalAddr, 16), r.LocalScopeId);
                    result.Add(new ConnectionSample("UDPv6", local, Port(r.LocalPort), null, null, "Bound", (int)r.OwningPid, ExposureOf(local)));
                }
            }
        }
        finally
        {
            NativeMemory.Free(table);
        }
    }

    /// <summary>Ports are stored in network byte order in the low 16 bits.</summary>
    private static int Port(uint raw) => BinaryPrimitives.ReverseEndianness((ushort)raw);

    private static string State(uint state) => state < TcpStates.Length ? TcpStates[state] : $"State {state}";

    private static Exposure ExposureOf(IPAddress local) =>
        IPAddress.IsLoopback(local) ? Exposure.Loopback
        : local.Equals(IPAddress.Any) || local.Equals(IPAddress.IPv6Any) ? Exposure.AllInterfaces
        : Exposure.SpecificAddress;
}

/// <summary>Background reverse DNS with a cache. Lookups never block the caller.</summary>
public static class ReverseDns
{
    private static readonly ConcurrentDictionary<IPAddress, string?> Cache = new();
    private static readonly SemaphoreSlim Gate = new(8);

    /// <summary>The host name if already resolved; otherwise starts a lookup and returns null.</summary>
    public static string? Lookup(IPAddress address)
    {
        if (!IsPublic(address)) return null;
        if (Cache.TryGetValue(address, out var name)) return name;
        Cache[address] = null;
        _ = ResolveAsync(address);
        return null;
    }

    private static async Task ResolveAsync(IPAddress address)
    {
        await Gate.WaitAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var entry = await Dns.GetHostEntryAsync(address.ToString(), timeout.Token);
            if (!string.IsNullOrEmpty(entry.HostName) && entry.HostName != address.ToString())
                Cache[address] = entry.HostName;
        }
        catch (Exception)
        {
            // No PTR record or lookup failed: the address stays unnamed.
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool IsPublic(IPAddress a)
    {
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return false;
        if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast) return false;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily != AddressFamily.InterNetwork) return true;
        var b = a.GetAddressBytes();
        return !(b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 169 && b[1] == 254) || b[0] >= 224);
    }
}

public static class ConnectionActions
{
    private const string RulePrefix = "TMNG:";

    /// <summary>Resets an established IPv4 TCP connection. Windows offers no equivalent for IPv6.</summary>
    public static ActionResult Close(ConnectionSample c)
    {
        if (c.Protocol != "TCP" || c.IsListener || c.RemoteAddress is null)
            return ActionResult.Fail("Only established IPv4 TCP connections can be closed individually.");
        if (!Elevation.IsElevated) return ActionResult.NeedsAdmin("Closing a connection");

        var row = new IpHlpApi.TcpRowBasic
        {
            State = IpHlpApi.TcpStateDeleteTcb,
            LocalAddr = (uint)BitConverter.ToInt32(c.LocalAddress.GetAddressBytes()),
            LocalPort = BinaryPrimitives.ReverseEndianness((ushort)c.LocalPort),
            RemoteAddr = (uint)BitConverter.ToInt32(c.RemoteAddress.GetAddressBytes()),
            RemotePort = BinaryPrimitives.ReverseEndianness((ushort)c.RemotePort!.Value),
        };
        uint status = IpHlpApi.SetTcpEntry(ref row);
        return status == 0
            ? ActionResult.Ok($"Closed {c.LocalPort} → {c.RemoteAddress}:{c.RemotePort}.")
            : ActionResult.Fail($"Windows refused to close the connection (error {status}).");
    }

    /// <summary>
    /// Listener: blocks inbound traffic to that program on that port.
    /// Connection: blocks outbound traffic to that remote address.
    /// </summary>
    public static ActionResult Block(ConnectionSample c, string? programPath)
    {
        if (!Elevation.IsElevated) return ActionResult.NeedsAdmin("Adding a firewall rule");

        string protocol = c.Protocol.StartsWith("TCP") ? "TCP" : "UDP";
        string name, args;
        if (c.IsListener)
        {
            name = $"{RulePrefix} block inbound {protocol} {c.LocalPort}" + (programPath is null ? "" : $" {Path.GetFileName(programPath)}");
            args = $"advfirewall firewall add rule name=\"{name}\" dir=in action=block protocol={protocol} localport={c.LocalPort}"
                + (programPath is null ? "" : $" program=\"{programPath}\"");
        }
        else
        {
            name = $"{RulePrefix} block outbound {c.RemoteAddress}";
            args = $"advfirewall firewall add rule name=\"{name}\" dir=out action=block remoteip={c.RemoteAddress}";
        }

        var (exit, output) = Netsh(args);
        return exit == 0 ? ActionResult.Ok($"Firewall rule added: \"{name}\".") : ActionResult.Fail(output.Trim());
    }

    /// <summary>Removes every firewall rule TMNG created, identified by the rule-name prefix.</summary>
    public static ActionResult RemoveRules()
    {
        if (!Elevation.IsElevated) return ActionResult.NeedsAdmin("Removing firewall rules");

        var (_, listing) = Netsh("advfirewall firewall show rule name=all");
        var names = listing.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("Rule Name:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["Rule Name:".Length..].Trim())
            .Where(n => n.StartsWith(RulePrefix, StringComparison.Ordinal))
            .Distinct()
            .ToList();

        foreach (var name in names) Netsh($"advfirewall firewall delete rule name=\"{name}\"");
        return ActionResult.Ok(names.Count == 0 ? "No TMNG firewall rules exist." : $"Removed {names.Count} TMNG firewall rule(s).");
    }

    private static (int Exit, string Output) Netsh(string arguments)
    {
        var info = new ProcessStartInfo("netsh", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
