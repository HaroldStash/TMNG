using System.Diagnostics;
using Tmng.Core;
using Tmng.Platform.Windows.Interop;

namespace Tmng.Platform.Windows;

/// <summary>
/// One NtQuerySystemInformation call returns every process with times, memory and I/O
/// counters. Rates are deltas keyed by <see cref="ProcessIdentity"/>, so a recycled PID
/// starts fresh instead of inheriting another process's history.
/// </summary>
internal sealed unsafe class ProcessProvider
{
    private readonly record struct Previous(long CpuTime, long ReadBytes, long WriteBytes);
    private sealed record StaticInfo(string? Path, string? User, string? Description, string? Company);
    private sealed record VersionInfo(string? Description, string? Company);

    private readonly Dictionary<ProcessIdentity, StaticInfo> _static = [];
    private readonly Dictionary<string, string?> _accounts = [];
    private readonly Dictionary<string, VersionInfo> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _logicalProcessors = Environment.ProcessorCount;
    private readonly int _selfPid = Environment.ProcessId;
    private Dictionary<ProcessIdentity, Previous> _previous = [];
    private byte[] _buffer = new byte[1 << 20];
    private long _lastTimestamp;
    private long _lastFileTime;

    public IReadOnlyList<ProcessSample> Read(bool includeSelf, AcceleratorUsage accelerators, IoRates io)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = _lastTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
        _lastTimestamp = now;
        long nowFileTime = DateTime.UtcNow.ToFileTimeUtc();
        long sinceFileTime = _lastFileTime;
        _lastFileTime = nowFileTime;

        int status;
        while (true)
        {
            uint needed;
            fixed (byte* p = _buffer)
                status = NtDll.NtQuerySystemInformation(NtDll.SystemProcessInformationClass, p, (uint)_buffer.Length, out needed);
            if (status != NtDll.StatusInfoLengthMismatch) break;
            _buffer = new byte[Math.Max(needed + 64 * 1024, (uint)_buffer.Length * 2)];
        }
        if (status < 0) throw new InvalidOperationException($"NtQuerySystemInformation failed (0x{status:X8})");

        var samples = new List<ProcessSample>(512);
        var current = new Dictionary<ProcessIdentity, Previous>(512);

        fixed (byte* p = _buffer)
        {
            uint offset = 0;
            while (true)
            {
                var info = (NtDll.SystemProcessInformation*)(p + offset);
                int pid = (int)info->UniqueProcessId;

                // PID 0 is the idle process: its "CPU" is idle time, not work.
                if (pid != 0 && (includeSelf || pid != _selfPid))
                {
                    var id = new ProcessIdentity(pid, info->CreateTime);
                    long cpuTime = info->UserTime + info->KernelTime;
                    current[id] = new Previous(cpuTime, info->ReadTransferCount, info->WriteTransferCount);

                    Reading cpu = default, ioRead = default, ioWrite = default;
                    if (elapsed > 0 && _previous.TryGetValue(id, out var prev))
                    {
                        cpu = Reading.Of(Math.Clamp((cpuTime - prev.CpuTime) / (elapsed * 1e7 * _logicalProcessors) * 100, 0, 100));
                        ioRead = Reading.Of(Math.Max(0, info->ReadTransferCount - prev.ReadBytes) / elapsed);
                        ioWrite = Reading.Of(Math.Max(0, info->WriteTransferCount - prev.WriteBytes) / elapsed);
                    }
                    else if (sinceFileTime != 0 && info->CreateTime > sinceFileTime)
                    {
                        // Started since the last tick: its counters cover its whole life, so the
                        // rate over that life is measured, not estimated.
                        double life = (nowFileTime - info->CreateTime) / 1e7;
                        if (life >= 0.05)
                        {
                            cpu = Reading.Of(Math.Clamp(cpuTime / (life * 1e7 * _logicalProcessors) * 100, 0, 100));
                            ioRead = Reading.Of(info->ReadTransferCount / life);
                            ioWrite = Reading.Of(info->WriteTransferCount / life);
                        }
                    }

                    string name = info->ImageName.Buffer is null
                        ? (pid == 4 ? "System" : $"Process {pid}")
                        : new string(info->ImageName.Buffer, 0, info->ImageName.Length / 2);
                    var details = GetStatic(id);
                    var (netRx, netTx, diskRead, diskWrite) = io.For(pid);

                    samples.Add(new ProcessSample(
                        id,
                        (int)info->InheritedFromUniqueProcessId,
                        name,
                        details.Path,
                        details.User,
                        (int)info->SessionId,
                        (int)info->NumberOfThreads,
                        (int)info->HandleCount,
                        info->BasePriority,
                        info->CreateTime > 0 ? DateTime.FromFileTime(info->CreateTime) : null,
                        cpu,
                        TimeSpan.FromTicks(cpuTime),
                        info->WorkingSetPrivateSize,
                        (long)info->PrivatePageCount,
                        (long)info->WorkingSetSize,
                        info->PageFaultCount,
                        ioRead,
                        ioWrite,
                        Accelerator(accelerators.Gpu, pid, supported: true),
                        Accelerator(accelerators.Npu, pid, accelerators.HasNpu))
                    {
                        NetReceiveBytesPerSec = netRx,
                        NetSendBytesPerSec = netTx,
                        DiskReadBytesPerSec = diskRead,
                        DiskWriteBytesPerSec = diskWrite,
                        Description = details.Description,
                        Company = details.Company,
                    });
                }

                if (info->NextEntryOffset == 0) break;
                offset += info->NextEntryOffset;
            }
        }

        _previous = current;
        foreach (var gone in _static.Keys.Where(k => !current.ContainsKey(k)).ToList())
            _static.Remove(gone);
        return samples;
    }

    private static Reading Accelerator(Dictionary<int, double>? byPid, int pid, bool supported) =>
        !supported ? Reading.Unsupported
        : byPid is null ? Reading.Unavailable
        : Reading.Of(Math.Min(100, byPid.GetValueOrDefault(pid)));

    private StaticInfo GetStatic(ProcessIdentity id)
    {
        if (_static.TryGetValue(id, out var cached)) return cached;

        string? path = null, user = null;
        nint handle = Kernel32.OpenProcess(Kernel32.ProcessQueryLimitedInformation, 0, (uint)id.Pid);
        if (handle != 0)
        {
            try
            {
                char* buffer = stackalloc char[1024];
                uint size = 1024;
                if (Kernel32.QueryFullProcessImageName(handle, 0, buffer, ref size) != 0)
                    path = new string(buffer, 0, (int)size);
                user = QueryUser(handle);
            }
            finally
            {
                Kernel32.CloseHandle(handle);
            }
        }

        var version = path is null ? null : GetVersion(path);
        return _static[id] = new StaticInfo(path, user, version?.Description, version?.Company);
    }

    private VersionInfo GetVersion(string path)
    {
        if (_versions.TryGetValue(path, out var cached)) return cached;
        VersionInfo version;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            version = new VersionInfo(info.FileDescription?.Trim(), info.CompanyName?.Trim());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            version = new VersionInfo(null, null);
        }
        return _versions[path] = version;
    }

    private string? QueryUser(nint process)
    {
        if (Advapi32.OpenProcessToken(process, Advapi32.TokenQuery, out var token) == 0) return null;
        try
        {
            byte* info = stackalloc byte[256];
            if (Advapi32.GetTokenInformation(token, Advapi32.TokenUser, info, 256, out _) == 0) return null;

            nint sid = *(nint*)info;
            uint sidLength = Advapi32.GetLengthSid(sid);
            string key = Convert.ToHexString(new ReadOnlySpan<byte>((void*)sid, (int)sidLength));
            if (_accounts.TryGetValue(key, out var account)) return account;

            char* name = stackalloc char[256];
            char* domain = stackalloc char[256];
            uint nameLength = 256, domainLength = 256;
            account = Advapi32.LookupAccountSid(null, sid, name, ref nameLength, domain, ref domainLength, out _) != 0
                ? new string(name, 0, (int)nameLength)
                : null;
            _accounts[key] = account;
            return account;
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
    }
}
