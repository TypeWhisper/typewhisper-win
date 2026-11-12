using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace TypeWhisper.Cli;

/// <summary>Checks that the process named in a discovery file is still the TypeWhisper app that wrote it.</summary>
public static class CliServerProcess
{
    // Discovery files are written after the app started; allow for the file system's timestamp granularity.
    private static readonly TimeSpan ClockSlack = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns whether <paramref name="pid"/> is a TypeWhisper process of the current user that started before the
    /// discovery file was written. A process ID reused after an unexpected exit, a process of another user and any
    /// other program fail the check.
    /// </summary>
    public static bool IsTypeWhisperOfCurrentUser(int pid, DateTime discoveryWrittenUtc)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!string.Equals(process.ProcessName, "TypeWhisper", StringComparison.OrdinalIgnoreCase)) return false;
            if (process.StartTime.ToUniversalTime() > discoveryWrittenUtc + ClockSlack) return false;
            return !OperatingSystem.IsWindows() || OwnedByCurrentUser(pid);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool OwnedByCurrentUser(int pid)
    {
        // Limited query access also opens an elevated TypeWhisper of the same user.
        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(process, TokenQuery | TokenDuplicate, out var token)) return false;
            try
            {
                using var identity = new WindowsIdentity(token);
                using var current = WindowsIdentity.GetCurrent();
                return identity.User is { } user && user.Equals(current.User);
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000, TokenQuery = 0x0008, TokenDuplicate = 0x0002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
