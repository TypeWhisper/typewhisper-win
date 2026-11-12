using System.Runtime.InteropServices;
using System.Security.Principal;

namespace TypeWhisper.WinUI;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
/// <summary>The parts of a process's identity that decide whether a workflow may send dictation to it.</summary>
internal readonly record struct ProcessIdentity(uint Session, SecurityIdentifier User, int Integrity);

// A workflow names its target app only by process name (#764), and any process can carry that name. The dictation and
// a restart of the app's program therefore only go to processes that run in this Windows session, under this user and
// at least at TypeWhisper's integrity level, as the app the user started does. A process of another signed-in user, a
// sandboxed low-integrity process or one whose identity cannot be read is never chosen.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class TargetAppProcessTrust
{
    private static readonly Lazy<ProcessIdentity?> Own = new(() => Read((uint)Environment.ProcessId));

    /// <summary>Whether a process with this identity may receive dictation meant for the user's own app.</summary>
    internal static bool Admits(ProcessIdentity own, ProcessIdentity? candidate) =>
        candidate is { } other && other.Session == own.Session && other.User.Equals(own.User) && other.Integrity >= own.Integrity;

    /// <summary>Whether the process with this id may receive dictation; false when its identity cannot be read.</summary>
    internal static bool IsTrusted(uint processId) =>
        Own.Value is { } own && Admits(own, Read(processId));

    /// <summary>The session, user and integrity level of a process, or null when any of them cannot be read.</summary>
    internal static ProcessIdentity? Read(uint processId)
    {
        if (!ProcessIdToSessionId(processId, out var session)) return null;
        var process = OpenProcess(QueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
            try
            {
                // TOKEN_USER and TOKEN_MANDATORY_LABEL both start with a SID pointer followed by the SID itself.
                var information = Marshal.AllocHGlobal(InformationSize);
                try
                {
                    if (!GetTokenInformation(token, TokenUser, information, InformationSize, out _)) return null;
                    var user = new SecurityIdentifier(Marshal.ReadIntPtr(information));
                    if (!GetTokenInformation(token, TokenIntegrityLevel, information, InformationSize, out _)) return null;
                    var label = new SecurityIdentifier(Marshal.ReadIntPtr(information)).Value;
                    // A mandatory label is S-1-16-<level>, such as S-1-16-8192 for medium integrity.
                    return int.TryParse(label[(label.LastIndexOf('-') + 1)..], out var integrity)
                        ? new ProcessIdentity(session, user, integrity) : null;
                }
                finally { Marshal.FreeHGlobal(information); }
            }
            finally { CloseHandle(token); }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { return null; }
        finally { CloseHandle(process); }
    }

    // A SID pointer and attributes plus a SID of at most 68 bytes.
    private const int InformationSize = 128;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const int TokenIntegrityLevel = 25;
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, uint length, out uint returnLength);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
