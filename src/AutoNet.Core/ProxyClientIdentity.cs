using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AutoNet.Core;

// Resolves the loopback client endpoint to the kernel-reported owning PID; no user input
// or HTTP header is accepted as process identity.
internal static class ProxyClientIdentity
{
    public static async Task<int?> FindOwnerAsync(TcpClient accepted, CancellationToken ct)
    {
        if (accepted.Client.RemoteEndPoint is not IPEndPoint remote || accepted.Client.LocalEndPoint is not IPEndPoint local ||
            !IPAddress.IsLoopback(remote.Address) || !IPAddress.IsLoopback(local.Address)) return null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var owner = FindOwner(remote, local);
            if (owner is not null) return owner;
            await Task.Delay(50, ct);
        }
        return null;
    }

    private static int? FindOwner(IPEndPoint client, IPEndPoint server)
    {
        var size = 0;
        var first = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
        if (first != 122 || size is < 4 or > 16 * 1024 * 1024) return null;
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(memory, ref size, false, 2, 5, 0) != 0) return null;
            var count = Marshal.ReadInt32(memory);
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            if (count < 0 || (long)count * rowSize + 4 > size) return null;
            int? owner = null;
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(IntPtr.Add(memory, 4 + i * rowSize));
                if (row.State != 5 || !new IPAddress(row.LocalAddress).Equals(client.Address) ||
                    !new IPAddress(row.RemoteAddress).Equals(server.Address) ||
                    Port(row.LocalPort) != client.Port || Port(row.RemotePort) != server.Port || row.ProcessId > int.MaxValue || row.ProcessId == 0)
                    continue;
                if (owner is not null && owner != (int)row.ProcessId) return null;
                owner = (int)row.ProcessId;
            }
            return owner;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static int Port(uint value) => unchecked((ushort)IPAddress.NetworkToHostOrder((short)value));

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, ProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);
}
