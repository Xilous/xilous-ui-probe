using System.Net;
using System.Runtime.InteropServices;

namespace XilousProbe.Cdp;

/// <summary>
/// Finds the process listening on a local TCP port. For a Chromium app that is the
/// browser process, which owns all of the app's top-level windows, so it tells the
/// app which windows the web pointer covers.
/// </summary>
public static partial class PortOwner
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state, localAddr, localPort, remoteAddr, remotePort, owningPid;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(nint pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf, int tableClass, uint reserved);

    public static uint? FindListeningProcess(int port)
    {
        int size = 0;
        GetExtendedTcpTable(0, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            nint buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) continue;
                int count = Marshal.ReadInt32(buf);
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buf + 4 + i * rowSize);
                    int rowPort = IPAddress.NetworkToHostOrder((short)(row.localPort & 0xFFFF)) & 0xFFFF;
                    if (rowPort == port) return row.owningPid;
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        return null;
    }
}
