using System.Diagnostics;
using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Tmng.Core;
using Tmng.Platform.Windows.Interop;

namespace Tmng.Platform.Windows;

public enum IoTraceState { NotStarted, Starting, Running, NeedsAdmin, Failed }

public readonly record struct ProcessIo(double NetReceive, double NetSend, double DiskRead, double DiskWrite);

/// <summary>Bytes per second over one sampling interval. Ports are as the trace reported them.</summary>
public sealed class IoRates
{
    public static readonly IoRates NeedsAdmin = new(Availability.AccessDenied, [], [], 0);
    public static readonly IoRates NotReady = new(Availability.Unavailable, [], [], 0);

    internal IoRates(Availability state, Dictionary<int, ProcessIo> processes, Dictionary<FlowKey, (double Rx, double Tx)> flows, double eventsPerSec)
    {
        State = state;
        Processes = processes;
        Flows = flows;
        EventsPerSec = eventsPerSec;
    }

    public Availability State { get; }
    public double EventsPerSec { get; }
    internal Dictionary<int, ProcessIo> Processes { get; }
    internal Dictionary<FlowKey, (double Rx, double Tx)> Flows { get; }

    /// <summary>One process's rates. A traced process with no events really did no I/O, so it reads 0.</summary>
    public (Reading NetRx, Reading NetTx, Reading DiskRead, Reading DiskWrite) For(int pid)
    {
        if (State != Availability.Measured)
        {
            var missing = new Reading(0, State);
            return (missing, missing, missing, missing);
        }
        var io = Processes.GetValueOrDefault(pid);
        return (Reading.Of(io.NetReceive), Reading.Of(io.NetSend), Reading.Of(io.DiskRead), Reading.Of(io.DiskWrite));
    }

    /// <summary>
    /// Traffic for one endpoint. The trace's port byte order and which side it calls source are
    /// not documented, so every orientation is tried; the PID keeps a wrong match unlikely.
    /// </summary>
    public (Reading Rx, Reading Tx) For(ConnectionSample c)
    {
        if (State != Availability.Measured)
        {
            var missing = new Reading(0, State);
            return (missing, missing);
        }

        bool udp = c.Protocol.StartsWith("UDP", StringComparison.Ordinal);
        int local = c.LocalPort, remote = c.RemotePort ?? 0;
        if (c.IsListener && !udp) return (Reading.Of(0), Reading.Of(0));

        foreach (var key in Candidates(c.Pid, udp, local, remote))
        {
            if (Flows.TryGetValue(key, out var flow)) return (Reading.Of(flow.Rx), Reading.Of(flow.Tx));
        }
        return (Reading.Of(0), Reading.Of(0));
    }

    private static IEnumerable<FlowKey> Candidates(int pid, bool udp, int local, int remote)
    {
        int l = Swap(local), r = Swap(remote);
        if (udp)
        {
            yield return new FlowKey(pid, true, local, 0);
            yield return new FlowKey(pid, true, l, 0);
            yield break;
        }
        yield return new FlowKey(pid, false, local, remote);
        yield return new FlowKey(pid, false, remote, local);
        yield return new FlowKey(pid, false, l, r);
        yield return new FlowKey(pid, false, r, l);
    }

    internal static int Swap(int port) => (ushort)((port << 8) | ((port >> 8) & 0xFF));
}

internal readonly record struct FlowKey(int Pid, bool Udp, int PortA, int PortB);

/// <summary>
/// Real-time kernel trace (ETW) of TCP/UDP sends and receives and physical disk transfers.
/// Windows has no per-process network counter; this trace is how Task Manager and Resource
/// Monitor get theirs, and starting it requires administrator rights.
/// </summary>
public sealed class IoTrace : IDisposable
{
    private const string SessionName = "TMNG-IO";

    public static IoTrace Shared { get; } = new();

    private sealed class Totals { public long NetRx, NetTx, DiskRead, DiskWrite; }
    private sealed class Flow { public long Rx, Tx; }

    private readonly Lock _gate = new();
    private Dictionary<int, Totals> _processes = [];
    private Dictionary<FlowKey, Flow> _flows = [];
    private long _events;
    private long _lastCollect;

    // Disk completions can arrive in another process's context; resolve the issuing thread instead.
    private readonly Dictionary<int, int> _threadOwners = [];
    private long _threadOwnersReset = Stopwatch.GetTimestamp();

    private TraceEventSession? _session;
    private Thread? _thread;

    public IoTraceState State { get; private set; }
    public string? Problem { get; private set; }
    public IoRates Latest { get; private set; } = IoRates.NotReady;

    public void Start()
    {
        if (State != IoTraceState.NotStarted) return;
        if (!Elevation.IsElevated)
        {
            State = IoTraceState.NeedsAdmin;
            Latest = IoRates.NeedsAdmin;
            return;
        }

        State = IoTraceState.Starting;
        _thread = new Thread(Run) { IsBackground = true, Name = "TMNG I/O trace" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            // Windows 8 and later allow kernel sessions under any name, so TMNG never takes over
            // the shared "NT Kernel Logger" another tool may be using.
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.DiskIO |
                KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread);

            var kernel = _session.Source.Kernel;
            kernel.TcpIpSend += e => Network(e.ProcessID, e.size, send: true, udp: false, e.saddr, e.sport, e.dport);
            kernel.TcpIpRecv += e => Network(e.ProcessID, e.size, send: false, udp: false, e.saddr, e.sport, e.dport);
            kernel.TcpIpSendIPV6 += e => Network(e.ProcessID, e.size, send: true, udp: false, e.saddr, e.sport, e.dport);
            kernel.TcpIpRecvIPV6 += e => Network(e.ProcessID, e.size, send: false, udp: false, e.saddr, e.sport, e.dport);
            kernel.UdpIpSend += e => Network(e.ProcessID, e.size, send: true, udp: true, e.saddr, e.sport, e.dport);
            kernel.UdpIpRecv += e => Network(e.ProcessID, e.size, send: false, udp: true, e.saddr, e.sport, e.dport);
            kernel.UdpIpSendIPV6 += e => Network(e.ProcessID, e.size, send: true, udp: true, e.saddr, e.sport, e.dport);
            kernel.UdpIpRecvIPV6 += e => Network(e.ProcessID, e.size, send: false, udp: true, e.saddr, e.sport, e.dport);
            kernel.DiskIORead += e => Disk(e.ProcessID, e.ThreadID, e.TransferSize, write: false);
            kernel.DiskIOWrite += e => Disk(e.ProcessID, e.ThreadID, e.TransferSize, write: true);

            lock (_gate) _lastCollect = Stopwatch.GetTimestamp();
            State = IoTraceState.Running;
            _session.Source.Process(); // blocks until the session stops
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            State = IoTraceState.Failed;
        }
    }

    private void Network(int pid, int size, bool send, bool udp, IPAddress? source, int sourcePort, int destPort)
    {
        if (pid < 0 || size <= 0) return;
        lock (_gate)
        {
            _events++;
            // Loopback never touches an adapter; leaving it out keeps process totals comparable
            // to the adapter totals. It still counts toward the connection's own row.
            if (source is null || !IPAddress.IsLoopback(source))
            {
                var totals = ProcessTotals(pid);
                if (send) totals.NetTx += size; else totals.NetRx += size;
            }

            if (udp)
            {
                AddFlow(new FlowKey(pid, true, sourcePort, 0), size, send);
                if (destPort != sourcePort) AddFlow(new FlowKey(pid, true, destPort, 0), size, send);
            }
            else
            {
                AddFlow(new FlowKey(pid, false, sourcePort, destPort), size, send);
            }
        }
    }

    private void AddFlow(FlowKey key, int size, bool send)
    {
        if (!_flows.TryGetValue(key, out var flow)) _flows[key] = flow = new Flow();
        if (send) flow.Tx += size; else flow.Rx += size;
    }

    private void Disk(int pid, int threadId, int size, bool write)
    {
        if (size <= 0) return;
        lock (_gate)
        {
            _events++;
            if (pid <= 0 && threadId > 0) pid = OwnerOf(threadId);
            if (pid < 0) return;
            var totals = ProcessTotals(pid);
            if (write) totals.DiskWrite += size; else totals.DiskRead += size;
        }
    }

    private Totals ProcessTotals(int pid)
    {
        if (!_processes.TryGetValue(pid, out var totals)) _processes[pid] = totals = new Totals();
        return totals;
    }

    private int OwnerOf(int threadId)
    {
        // Thread IDs are recycled, so the cache is short-lived.
        if (Stopwatch.GetElapsedTime(_threadOwnersReset).TotalSeconds > 30)
        {
            _threadOwners.Clear();
            _threadOwnersReset = Stopwatch.GetTimestamp();
        }
        if (_threadOwners.TryGetValue(threadId, out int pid)) return pid;

        pid = -1;
        nint handle = Kernel32.OpenThread(Kernel32.ThreadQueryLimitedInformation, 0, (uint)threadId);
        if (handle != 0)
        {
            uint owner = Kernel32.GetProcessIdOfThread(handle);
            Kernel32.CloseHandle(handle);
            if (owner != 0) pid = (int)owner;
        }
        return _threadOwners[threadId] = pid;
    }

    /// <summary>Turns the bytes counted since the previous call into rates. Call once per tick.</summary>
    public IoRates Collect()
    {
        if (State != IoTraceState.Running) return Latest;

        Dictionary<int, Totals> processes;
        Dictionary<FlowKey, Flow> flows;
        long events, now = Stopwatch.GetTimestamp();
        double seconds;
        lock (_gate)
        {
            seconds = Stopwatch.GetElapsedTime(_lastCollect, now).TotalSeconds;
            _lastCollect = now;
            (processes, _processes) = (_processes, []);
            (flows, _flows) = (_flows, []);
            (events, _events) = (_events, 0);
        }
        if (seconds <= 0) return Latest;

        return Latest = new IoRates(
            Availability.Measured,
            processes.ToDictionary(p => p.Key, p => new ProcessIo(
                p.Value.NetRx / seconds, p.Value.NetTx / seconds, p.Value.DiskRead / seconds, p.Value.DiskWrite / seconds)),
            flows.ToDictionary(f => f.Key, f => (f.Value.Rx / seconds, f.Value.Tx / seconds)),
            events / seconds);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
