using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI.Platform;

/// <summary>Resolves the process behind a loopback TCP peer through the IP Helper connection table and admits
/// it when its primary token belongs to the same user as this process.</summary>
internal sealed class WindowsLocalPeerVerifier : ILocalPeerVerifier
{
    // Same user, not same session: the discovery token is readable by every process of the user regardless of
    // session, so a second console/RDP session or a per-user service of the same account gains nothing it could
    // not already read, while a session check would lock those legitimate clients out.
    private readonly SecurityIdentifier _user = WindowsIdentity.GetCurrent().User!;
    private static int _tableSizeHint = 32 * 1024;

    /// <inheritdoc/>
    public bool IsOwnUser(IPEndPoint peer, int localPort)
    {
        // Unknown peers are rejected rather than tolerated: the connection is open while its request is handled,
        // so the common case always has a row. No row means the peer already went away (its response could not be
        // delivered anyway), and a row whose process cannot be opened belongs to another user, to the kernel
        // (WSL2 mirrored networking, container relays) or to a process that exited; none of them may proceed.
        try
        {
            return FindOwningProcess(peer, localPort) is { Pid: > 4 } owner && HasOwnUser(owner.Pid, owner.Created);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine("API peer check failed: " + ex.GetType().Name);
            return false;
        }
    }

    private readonly record struct Owner(uint Pid, long Created);

    private static unsafe Owner? FindOwningProcess(IPEndPoint peer, int localPort)
    {
        var address = peer.Address.IsIPv4MappedToIPv6 ? peer.Address.MapToIPv4() : peer.Address;
        var ipv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        Span<byte> peerAddress = stackalloc byte[16];
        if (!address.TryWriteBytes(peerAddress, out var addressLength)) return null;
        peerAddress = peerAddress[..addressLength];
        var size = (uint)Volatile.Read(ref _tableSizeHint);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = ArrayPool<byte>.Shared.Rent((int)size);
            try
            {
                size = (uint)buffer.Length;
                uint result;
                fixed (byte* table = buffer)
                    result = NativeMethods.GetExtendedTcpTable((IntPtr)table, ref size, false, ipv6 ? NativeMethods.AF_INET6 : NativeMethods.AF_INET,
                        NativeMethods.TCP_TABLE_OWNER_MODULE_CONNECTIONS, 0);
                if (result == NativeMethods.ERROR_INSUFFICIENT_BUFFER)
                {
                    // Remember the larger table with headroom so a busy machine normally needs one call.
                    size += 4096;
                    Volatile.Write(ref _tableSizeHint, Math.Max(Volatile.Read(ref _tableSizeHint), (int)size));
                    continue;
                }
                if (result != 0) return null;
                return ipv6 ? FindIPv6(buffer, peerAddress, (ushort)peer.Port, (ushort)localPort)
                    : FindIPv4(buffer, peerAddress, (ushort)peer.Port, (ushort)localPort);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        return null;
    }

    // MIB_TCPTABLE_OWNER_MODULE: DWORD count, padding to the rows' 8-byte alignment, then MIB_TCPROW_OWNER_MODULE
    // rows of state, local address, local port, remote address, remote port and PID (six DWORDs), the creation
    // FILETIME and 16 ULONGLONGs of module information. Addresses and ports are stored in network byte order.
    private const int RowsOffset = 8, IPv4RowSize = 160, IPv6RowSize = 192;

    private static Owner? FindIPv4(ReadOnlySpan<byte> table, ReadOnlySpan<byte> peerAddress, ushort peerPort, ushort localPort)
    {
        var rows = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(table), (uint)((table.Length - RowsOffset) / IPv4RowSize));
        for (var i = 0; i < rows; i++)
        {
            var row = table.Slice(RowsOffset + i * IPv4RowSize, IPv4RowSize);
            // The row is the peer's socket: its local side is the peer, its remote side is our loopback listener.
            if (Port(row[8..]) == peerPort && row.Slice(4, 4).SequenceEqual(peerAddress) && Port(row[16..]) == localPort && row[12] == 127)
                return new(BinaryPrimitives.ReadUInt32LittleEndian(row[20..]), BinaryPrimitives.ReadInt64LittleEndian(row[24..]));
        }
        return null;
    }

    // MIB_TCP6ROW_OWNER_MODULE rows: local address (16), local scope, local port, remote address (16), remote scope,
    // remote port, state, PID, creation FILETIME and module information. Loopback has no scope, so scopes are not
    // compared.
    private static Owner? FindIPv6(ReadOnlySpan<byte> table, ReadOnlySpan<byte> peerAddress, ushort peerPort, ushort localPort)
    {
        ReadOnlySpan<byte> loopback = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1];
        var rows = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(table), (uint)((table.Length - RowsOffset) / IPv6RowSize));
        for (var i = 0; i < rows; i++)
        {
            var row = table.Slice(RowsOffset + i * IPv6RowSize, IPv6RowSize);
            if (Port(row[20..]) == peerPort && row[..16].SequenceEqual(peerAddress) && Port(row[44..]) == localPort && row.Slice(24, 16).SequenceEqual(loopback))
                return new(BinaryPrimitives.ReadUInt32LittleEndian(row[52..]), BinaryPrimitives.ReadInt64LittleEndian(row[56..]));
        }
        return null;
    }

    private static ushort Port(ReadOnlySpan<byte> field) =>
        BinaryPrimitives.ReverseEndianness((ushort)BinaryPrimitives.ReadUInt32LittleEndian(field));

    private unsafe bool HasOwnUser(uint pid, long connectionCreated)
    {
        // Limited query access opens elevated processes of the same user; other users' processes refuse it.
        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            // The table keeps naming the process that created the socket, even after it handed the socket to another
            // process and exited. A process started after the connection was created only reuses that ID.
            if (!NativeMethods.GetProcessTimes(process, out var processCreated, out _, out _, out _) ||
                connectionCreated <= 0 || processCreated > connectionCreated) return false;
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out var token)) return false;
            try
            {
                // TOKEN_USER is a SID pointer plus attributes followed by the SID itself (at most 68 bytes).
                var information = stackalloc byte[128];
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenUser, (IntPtr)information, 128, out _)) return false;
                return new SecurityIdentifier(*(IntPtr*)information).Equals(_user);
            }
            finally { NativeMethods.CloseHandle(token); }
        }
        finally { NativeMethods.CloseHandle(process); }
    }
}
