using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI.Platform;

/// <summary>
/// Detects whether the current session is a Remote Desktop session. Checked on demand,
/// because a local console session can be taken over remotely and back while running.
/// </summary>
internal static class RemoteSession
{
    private const int SmRemoteSession = 0x1000;

    internal static bool IsActive() => GetSystemMetrics(SmRemoteSession) != 0;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
