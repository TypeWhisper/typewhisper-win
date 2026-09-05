namespace TypeWhisper.WinUI;

internal enum HybridHotkeyAction { Toggle, Start, Stop, Cancel }

// Pure state machine; timestamps are monotonic milliseconds. No native input or audio.
internal sealed class HybridHotkeyState
{
    internal const long HoldMilliseconds = 300;
    private readonly HashSet<int> _down = [];
    private string? _armed;
    private long _pressedAt;
    private bool _startedByGesture;
    private bool _blocked;

    internal HybridHotkeyAction? Key(int key, bool down, long now, IReadOnlySet<string> bindings, bool recording = false)
    {
        if (down && !_down.Add(key)) return null;
        if (!down) _down.Remove(key);
        var chord = Chord();
        HybridHotkeyAction? action = null;
        if (_armed is not null && chord != _armed)
        {
            // Capture already started on key-down. A tap keeps it running.
            // Extra keys discard speculative capture instead of transcribing it.
            action = !_startedByGesture ? null : down ? HybridHotkeyAction.Cancel
                : now - _pressedAt >= HoldMilliseconds ? HybridHotkeyAction.Stop : null;
            _armed = null; _startedByGesture = false; _blocked = true;
        }
        if (!_blocked && down && bindings.Contains(chord))
        {
            _armed = chord; _pressedAt = now;
            _startedByGesture = !recording;
            action = recording ? HybridHotkeyAction.Stop : HybridHotkeyAction.Start;
        }
        // Non-modifier keys outside a configured chord invalidate the gesture.
        if (down && _armed is null && !IsModifier(key)) _blocked = true;
        if (_down.Count == 0) _blocked = false;
        return action;
    }

    private static bool IsModifier(int key) => key is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;
    private string Chord() => PrototypeShortcutRules.Normalize(string.Join("+", _down.Select(key => key switch
    {
        0x11 or 0xA2 or 0xA3 => "CTRL", 0x12 or 0xA4 or 0xA5 => "ALT",
        0x10 or 0xA0 or 0xA1 => "SHIFT", 0x5B or 0x5C => "WIN",
        >= 65 and <= 90 or >= 48 and <= 57 => ((char)key).ToString(),
        >= 112 and <= 135 => $"F{key - 111}",
        0x20 => "SPACE", 0x0D => "ENTER", 0x1B => "ESC", 0x08 => "BACKSPACE",
        _ => $"VK{key}"
    }).Distinct()));
}
