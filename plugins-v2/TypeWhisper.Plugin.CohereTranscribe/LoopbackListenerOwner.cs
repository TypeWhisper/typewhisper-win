using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TypeWhisper.Plugin.CohereTranscribe;

internal static class LoopbackListenerOwner
{
    // IPv4 TCP_TABLE_OWNER_PID_ALL. Ports are stored in network byte order.
    internal static int? FindProcess(int port, int? remotePort = null)
    {
        uint size = 0;
        var error = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        for (var attempt = 0; attempt < 3 && error == 122; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                error = GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0);
                if (error == 122) continue;
                if (error != 0) throw new Win32Exception((int)error);
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++)
                {
                    var row = IntPtr.Add(buffer, 4 + i * 24);
                    var address = unchecked((uint)Marshal.ReadInt32(row, 4));
                    var localPort = (Marshal.ReadByte(row, 8) << 8) | Marshal.ReadByte(row, 9);
                    var state = Marshal.ReadInt32(row);
                    var peerPort = (Marshal.ReadByte(row, 16) << 8) | Marshal.ReadByte(row, 17);
                    if ((address == 0x0100007f || address == 0) && localPort == port
                        && (remotePort is { } peer ? state == 5 && peerPort == peer : state == 2))
                        return Marshal.ReadInt32(row, 20);
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new Win32Exception((int)error, "The local speech listener owner could not be verified.");
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int tableClass, uint reserved);
}
