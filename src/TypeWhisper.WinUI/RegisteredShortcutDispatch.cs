namespace TypeWhisper.WinUI;

// The owner knows whether a chord starts work or stops it. A delayed WM_HOTKEY must
// reach that owner so a stop/cancel is not discarded before the action is resolved.
internal sealed class RegisteredShortcutDispatch(Action<string, bool> callback)
{
    internal void Invoke(string chord, uint timestamp, long now, Func<string, bool> capture)
    {
        var stale = InputEventTiming.IsStale(timestamp, now);
        if (!stale && capture(chord)) return;
        callback(chord, stale);
    }
}
