using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI;

// KBDLLHOOKSTRUCT.Time and GetMessageTime share the 32-bit Windows uptime clock.
// Subtraction remains valid across its 49-day wrap. Stale input must not start new work.
internal static class InputEventTiming
{
    internal const uint MaximumAgeMilliseconds = 500;

    // The same clock as the event timestamps, sleep time included. Since .NET 11,
    // Environment.TickCount64 excludes sleep on Windows, so after a resume every event
    // would look about as old as the total sleep time and be dropped as stale.
    internal static long Now => OperatingSystem.IsWindows() ? unchecked((long)GetTickCount64()) : Environment.TickCount64;

    internal static uint Age(uint eventTime, long now) => unchecked((uint)now - eventTime);
    internal static bool IsStale(uint eventTime, long now) => Age(eventTime, now) > MaximumAgeMilliseconds;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ulong GetTickCount64();
}
