using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI.Platform;

internal static partial class NativeMethods
{
    public const uint AF_INET = 2;
    public const uint AF_INET6 = 23;
    // Established connections only; listeners cannot be the peer of a request.
    public const uint TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenUser = 1;

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        uint addressFamily, uint tableClass, uint reserved);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, uint length, out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);
}
