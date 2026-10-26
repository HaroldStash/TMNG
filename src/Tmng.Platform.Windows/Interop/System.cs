using System.Runtime.InteropServices;

namespace Tmng.Platform.Windows.Interop;

internal static unsafe partial class ServiceApi
{
    public const uint ScManagerConnect = 0x0001;
    public const uint ScManagerEnumerate = 0x0004;
    public const uint ServiceQueryConfig = 0x0001;
    public const uint ServiceChangeConfig = 0x0002;
    public const uint ServiceQueryStatus = 0x0004;
    public const uint ServiceStart = 0x0010;
    public const uint ServiceStop = 0x0020;
    public const int ScEnumProcessInfo = 0;
    public const uint ServiceWin32 = 0x30;
    public const uint ServiceStateAll = 3;
    public const int ErrorMoreData = 234;
    public const uint ControlStop = 1;
    public const uint AcceptStop = 1;
    public const uint NoChange = 0xFFFFFFFF;
    public const uint ConfigDelayedAutoStart = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EnumServiceStatusProcess
    {
        public char* ServiceName;
        public char* DisplayName;
        public ServiceStatusProcess Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true)]
    public static partial nint OpenSCManager(char* machine, char* database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenService(nint manager, string name, uint access);

    [LibraryImport("advapi32.dll")]
    public static partial int CloseServiceHandle(nint handle);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    public static partial int EnumServicesStatusEx(nint manager, int infoLevel, uint serviceType, uint serviceState, byte* services,
        uint bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, char* groupName);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    public static partial int StartService(nint service, uint argCount, nint args);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int ControlService(nint service, uint control, out ServiceStatus status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    public static partial int QueryServiceStatus(nint service, out ServiceStatus status);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true)]
    public static partial int ChangeServiceConfig(nint service, uint serviceType, uint startType, uint errorControl, char* binaryPath,
        char* loadOrderGroup, nint tagId, char* dependencies, char* startName, char* password, char* displayName);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    public static partial int ChangeServiceConfig2(nint service, uint infoLevel, void* info);
}

internal static unsafe partial class Wts
{
    public const int UserName = 5;
    public const int DomainName = 7;
    public const int ClientName = 10;

    [StructLayout(LayoutKind.Sequential)]
    public struct SessionInfo
    {
        public uint SessionId;
        public char* WinStationName;
        public int State;
    }

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    public static partial int WTSEnumerateSessions(nint server, uint reserved, uint version, out SessionInfo* sessions, out uint count);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    public static partial int WTSQuerySessionInformation(nint server, uint sessionId, int infoClass, out char* buffer, out uint bytes);

    [LibraryImport("wtsapi32.dll")]
    public static partial void WTSFreeMemory(void* memory);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    public static partial int WTSDisconnectSession(nint server, uint sessionId, int wait);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    public static partial int WTSLogoffSession(nint server, uint sessionId, int wait);
}

internal static unsafe partial class Shlwapi
{
    [LibraryImport("shlwapi.dll", EntryPoint = "SHLoadIndirectString", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHLoadIndirectString(string source, char* output, uint outputLength, nint reserved);

    /// <summary>Resolves "@file.dll,-123" resource references; returns other strings unchanged.</summary>
    public static string Resolve(string value)
    {
        if (!value.StartsWith('@')) return value;
        char* buffer = stackalloc char[1024];
        return SHLoadIndirectString(value, buffer, 1024, 0) == 0 ? new string(buffer) : value;
    }
}
