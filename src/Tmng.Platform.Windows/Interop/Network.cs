using System.Runtime.InteropServices;

namespace Tmng.Platform.Windows.Interop;

internal static unsafe partial class IpHlpApi
{
    public const int AfInet = 2;
    public const int AfInet6 = 23;
    public const int TcpTableOwnerPidAll = 5;
    public const int UdpTableOwnerPid = 1;
    public const uint ErrorInsufficientBuffer = 122;
    public const uint TcpStateDeleteTcb = 12;

    [StructLayout(LayoutKind.Sequential)]
    public struct TcpRow
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Tcp6Row
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId, LocalPort;
        public fixed byte RemoteAddr[16];
        public uint RemoteScopeId, RemotePort, State, OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UdpRow
    {
        public uint LocalAddr, LocalPort, OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Udp6Row
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId, LocalPort, OwningPid;
    }

    /// <summary>MIB_TCPROW, as SetTcpEntry takes it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TcpRowBasic
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort;
    }

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetExtendedTcpTable(void* table, ref uint size, int order, int addressFamily, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetExtendedUdpTable(void* table, ref uint size, int order, int addressFamily, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint SetTcpEntry(ref TcpRowBasic row);
}
