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
            var pid = FindOwningProcess(peer, localPort);
            return pid is > 4 && HasOwnUser(pid.Value);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine("API peer check failed: " + ex.GetType().Name);
            return false;
        }
    }

    private static unsafe uint? FindOwningProcess(IPEndPoint peer, int localPort)
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
                        NativeMethods.TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
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

    // MIB_TCPTABLE_OWNER_PID: DWORD count, then MIB_TCPROW_OWNER_PID rows of state, local address, local port,
    // remote address, remote port and PID (six DWORDs). Addresses and ports are stored in network byte order.
    private static uint? FindIPv4(ReadOnlySpan<byte> table, ReadOnlySpan<byte> peerAddress, ushort peerPort, ushort localPort)
    {
        var rows = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(table), (uint)((table.Length - 4) / 24));
        for (var i = 0; i < rows; i++)
        {
            var row = table.Slice(4 + i * 24, 24);
            // The row is the peer's socket: its local side is the peer, its remote side is our loopback listener.
            if (Port(row[8..]) == peerPort && row.Slice(4, 4).SequenceEqual(peerAddress) && Port(row[16..]) == localPort && row[12] == 127)
                return BinaryPrimitives.ReadUInt32LittleEndian(row[20..]);
        }
        return null;
    }

    // MIB_TCP6TABLE_OWNER_PID rows: local address (16), local scope, local port, remote address (16), remote scope,
    // remote port, state and PID. Loopback has no scope, so scopes are not compared.
    private static uint? FindIPv6(ReadOnlySpan<byte> table, ReadOnlySpan<byte> peerAddress, ushort peerPort, ushort localPort)
    {
        ReadOnlySpan<byte> loopback = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1];
        var rows = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(table), (uint)((table.Length - 4) / 56));
        for (var i = 0; i < rows; i++)
        {
            var row = table.Slice(4 + i * 56, 56);
            if (Port(row[20..]) == peerPort && row[..16].SequenceEqual(peerAddress) && Port(row[44..]) == localPort && row.Slice(24, 16).SequenceEqual(loopback))
                return BinaryPrimitives.ReadUInt32LittleEndian(row[52..]);
        }
        return null;
    }

    private static ushort Port(ReadOnlySpan<byte> field) =>
        BinaryPrimitives.ReverseEndianness((ushort)BinaryPrimitives.ReadUInt32LittleEndian(field));

    private unsafe bool HasOwnUser(uint pid)
    {
        // Limited query access opens elevated processes of the same user; other users' processes refuse it.
        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
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
