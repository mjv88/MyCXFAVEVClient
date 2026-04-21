using System;
using System.Net;
using System.Runtime.InteropServices;

namespace DatevConnector.Webclient
{
    // Resolves the Windows session ID of the process that owns a loopback
    // TCP peer. Used to reject cross-session connections on Terminal Server.
    internal static class LoopbackPeerSession
    {
        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable,
            ref int pdwSize,
            bool bOrder,
            int ulAf,
            int tableClass,
            int reserved);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint State;
            public uint LocalAddr;
            public uint LocalPort;   // network byte order, high 16 bits zero
            public uint RemoteAddr;
            public uint RemotePort;  // network byte order, high 16 bits zero
            public uint OwningPid;
        }

        // Returns the session ID of the peer's owning process, or null if
        // the peer cannot be resolved (closed before lookup, PID recycled,
        // table race). Callers should treat null as "reject".
        public static uint? ResolvePeerSessionId(IPEndPoint local, IPEndPoint peer)
        {
            if (local == null || peer == null) return null;

            uint localAddr = (uint)BitConverter.ToInt32(local.Address.GetAddressBytes(), 0);
            uint peerAddr  = (uint)BitConverter.ToInt32(peer.Address.GetAddressBytes(),  0);
            uint localPortNbo = HostPortToTableNbo((ushort)local.Port);
            uint peerPortNbo  = HostPortToTableNbo((ushort)peer.Port);

            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET,
                TCP_TABLE_OWNER_PID_ALL, 0);
            if (size <= 0) return null;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint rc = GetExtendedTcpTable(buffer, ref size, false, AF_INET,
                    TCP_TABLE_OWNER_PID_ALL, 0);
                if (rc != 0) return null;

                int rowCount = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = IntPtr.Add(buffer, 4);
                int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));

                for (int i = 0; i < rowCount; i++)
                {
                    var row = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(
                        IntPtr.Add(rowPtr, i * rowSize),
                        typeof(MIB_TCPROW_OWNER_PID));

                    // The row whose LOCAL endpoint is our peer and whose
                    // REMOTE endpoint is our local socket — from the peer's
                    // point of view, we are its remote.
                    if (row.LocalAddr  == peerAddr  && row.LocalPort  == peerPortNbo  &&
                        row.RemoteAddr == localAddr && row.RemotePort == localPortNbo)
                    {
                        if (ProcessIdToSessionId(row.OwningPid, out uint sid))
                            return sid;
                        return null;
                    }
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static uint CurrentSessionId()
        {
            return ProcessIdToSessionId(GetCurrentProcessId(), out uint sid) ? sid : 0;
        }

        // Table stores ports as: low 16 bits = network-order bytes of the port,
        // high 16 bits zero. Build the comparison value the same way.
        private static uint HostPortToTableNbo(ushort port)
        {
            ushort nbo = (ushort)((port >> 8) | (port << 8));
            return (uint)nbo;
        }
    }
}
