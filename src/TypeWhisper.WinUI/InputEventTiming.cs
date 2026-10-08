namespace TypeWhisper.WinUI;

// KBDLLHOOKSTRUCT.Time and GetMessageTime share the 32-bit Windows uptime clock.
// Subtraction remains valid across its 49-day wrap. Stale input must not start new work.
internal static class InputEventTiming
{
    internal const uint MaximumAgeMilliseconds = 500;
    internal static uint Age(uint eventTime, long now) => unchecked((uint)now - eventTime);
    internal static bool IsStale(uint eventTime, long now) => Age(eventTime, now) > MaximumAgeMilliseconds;
}
