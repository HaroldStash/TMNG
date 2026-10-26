using System.Runtime.InteropServices;

namespace Tmng.Platform.Windows.Interop;

internal static unsafe partial class Kernel32
{
    public const uint ProcessTerminate = 0x0001;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint ThreadQueryLimitedInformation = 0x0800;
    public const int RelationProcessorCore = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
            SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int K32GetPerformanceInfo(out PerformanceInformation info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int GetPhysicallyInstalledSystemMemory(out ulong kilobytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, int inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    public static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenThread(uint access, int inheritHandle, uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetProcessIdOfThread(nint thread);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    public static partial int QueryFullProcessImageName(nint process, uint flags, char* buffer, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int SetPriorityClass(nint process, uint priorityClass);
}

internal static unsafe partial class Advapi32
{
    public const uint TokenQuery = 0x0008;
    public const int TokenUser = 1;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int GetTokenInformation(nint token, int infoClass, void* info, uint length, out uint returnLength);

    [LibraryImport("advapi32.dll")]
    public static partial uint GetLengthSid(nint sid);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupAccountSidW", SetLastError = true)]
    public static partial int LookupAccountSid(char* systemName, nint sid, char* name, ref uint nameLength, char* domain, ref uint domainLength, out int use);
}

internal static unsafe partial class NtDll
{
    public const int SystemProcessInformationClass = 5;
    public const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [StructLayout(LayoutKind.Sequential)]
    public struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    /// <summary>SYSTEM_PROCESS_INFORMATION; sequential layout reproduces the native alignment on x64 and ARM64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemProcessInformation
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public nuint UniqueProcessKey;
        public nuint PeakVirtualSize;
        public nuint VirtualSize;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivatePageCount;
        public long ReadOperationCount;
        public long WriteOperationCount;
        public long OtherOperationCount;
        public long ReadTransferCount;
        public long WriteTransferCount;
        public long OtherTransferCount;
    }

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int infoClass, void* buffer, uint length, out uint returnLength);
}

/// <summary>D3DKMT thunks: adapter enumeration that, unlike DXGI, also reports compute-only (NPU) devices.</summary>
internal static unsafe partial class Gdi32
{
    public const int KmtQaiAdapterRegistryInfo = 8;
    public const int KmtQaiAdapterType = 15;

    public const uint TypeRenderSupported = 1u << 0;
    public const uint TypeDisplaySupported = 1u << 1;
    public const uint TypeSoftwareDevice = 1u << 2;
    public const uint TypeComputeOnly = 1u << 11;

    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AdapterInfo
    {
        public uint Adapter;
        public Luid AdapterLuid;
        public uint NumOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EnumAdapters2
    {
        public uint NumAdapters;
        public AdapterInfo* Adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public void* PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CloseAdapter
    {
        public uint Adapter;
    }

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTEnumAdapters2(ref EnumAdapters2 args);

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo args);

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTCloseAdapter(ref CloseAdapter args);
}

internal static unsafe partial class Pdh
{
    public const uint FormatDouble = 0x00000200;
    public const uint FormatNoCap100 = 0x00008000;
    public const int MoreData = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Sequential)]
    public struct FormattedValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FormattedValueItem
    {
        public char* Name;
        public FormattedValue Value;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    public static partial int PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    public static partial int PdhCloseQuery(nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial int PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, void* buffer);
}
