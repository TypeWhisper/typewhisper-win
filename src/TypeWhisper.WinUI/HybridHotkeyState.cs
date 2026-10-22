using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal enum HybridHotkeyAction { Toggle, Start, Stop, Cancel }

// Pure state machine; timestamps are monotonic milliseconds. No native input or audio.
internal sealed class HybridHotkeyState
{
    internal const long HoldMilliseconds = 300;
    // Stands in for the synthetic left Ctrl that AltGr sends; outside the virtual-key range.
    internal const int AltGrControl = 0x1A2;
    // A held key that is not a modifier repeats while it is the newest key. Without any event for this
    // long it was released and its key-up got lost, e.g. swallowed by another app's keyboard hook.
    // Windows can then still report it as down, which would block every shortcut until it is pressed again.
    internal const long LostReleaseMilliseconds = 1500;
    private readonly HashSet<int> _down = [];
    private readonly Dictionary<int, long> _lastEvent = [];
    private readonly List<int> _dropped = [];
    private string? _armed;
    private long _pressedAt;
    private bool _startedByGesture;
    private bool _blocked;
    // The keys held when the gesture was last invalidated.
    private readonly HashSet<int> _blockedWith = [];
    private RecordingMode? _mode;

    internal void ResetAfterInterruption(IEnumerable<int> heldKeys)
    {
        _down.Clear();
        _lastEvent.Clear();
        _down.UnionWith(heldKeys);
        _armed = null; _startedByGesture = false; _mode = null;
        Block(_down.Count > 0);
    }

    internal void Suspend()
    {
        _armed = null; _startedByGesture = false; Block(_down.Count > 0);
    }

    internal HybridHotkeyAction? Key(int key, bool down, long now, IReadOnlySet<string> bindings, bool recording = false,
        RecordingMode mode = RecordingMode.Hybrid, bool paused = false, Func<int, bool>? held = null, bool stale = false)
    {
        // A second key-down without auto-repeat in between follows a lost key-up, e.g. of a Toggle
        // shortcut that started a recording. It is a new press, so the key is released first.
        if (down && _down.Contains(key) && _lastEvent.TryGetValue(key, out var last) && now - last > LostReleaseMilliseconds)
        {
            var released = Key(key, false, now, bindings, recording, mode, paused, held, stale);
            var dropped = _dropped.Append(key).ToArray();
            UnblockAfterLostRelease();
            var again = Key(key, true, now, bindings, recording, mode, paused, held, stale);
            _dropped.AddRange(dropped);
            return again ?? released;
        }
        HybridHotkeyAction? action = null;
        _dropped.Clear();
        foreach (var pressed in _down) _lastEvent.TryAdd(pressed, now);
        // Windows skips a low-level hook that misses its timeout, e.g. while capture starts on
        // the UI thread. A lost key-up would otherwise block the next press of the shortcut.
        // Keys of a running Hold or Hybrid gesture stay, so a held shortcut is never ended by this.
        if (held is not null && _down.RemoveWhere(pressed => !held(pressed) || LostRelease(pressed, key, now, mode)) > 0)
        {
            if (_armed is not null && Chord() != _armed)
            {
                // The release time is unknown, so only a gesture started in Hold mode can infer a Stop;
                // a Hybrid gesture counts as a tap. Keys still held must be released before the next gesture.
                action = _startedByGesture && (_mode ?? mode) == RecordingMode.Hold ? HybridHotkeyAction.Stop : null;
                _armed = null; _startedByGesture = false; Block(_down.Count > 0);
            }
            else if (_down.Count == 0) Block(false);
            if (_dropped.Count > 0) UnblockAfterLostRelease();
        }
        if (_mode is not null && _mode != mode)
        {
            // A changed setting must never reinterpret keys which are already held.
            action = _startedByGesture && recording ? HybridHotkeyAction.Cancel : action;
            _armed = null; _startedByGesture = false; Block(_down.Count > 0);
        }
        _mode = mode;
        if (_lastEvent.Count > _down.Count)
            foreach (var released in _lastEvent.Keys.Where(pressed => !_down.Contains(pressed)).ToArray()) _lastEvent.Remove(released);
        if (down) _lastEvent[key] = now;
        if (down && !_down.Add(key)) return action;
        if (!down) { _down.Remove(key); _lastEvent.Remove(key); }
        // Delayed input cannot start capture, including the remaining keys of that gesture.
        // Terminal actions still stop an existing recording after the UI recovers.
        if (paused || stale && !recording) { Suspend(); return null; }
        var chord = Chord();
        if (_armed is not null && chord != _armed)
        {
            // Capture already started on key-down. A tap keeps it running.
            // Extra keys discard speculative capture instead of transcribing it.
            action = !_startedByGesture ? null : down ? HybridHotkeyAction.Cancel
                : mode == RecordingMode.Hold || mode == RecordingMode.Hybrid && now - _pressedAt >= HoldMilliseconds
                    ? HybridHotkeyAction.Stop : null;
            _armed = null; _startedByGesture = false; Block(true);
        }
        if (!_blocked && down && bindings.Contains(chord))
        {
            _armed = chord; _pressedAt = now;
            _startedByGesture = !recording;
            // Keep a Stop inferred from a lost release when a hold-mode press is otherwise ignored.
            action = recording ? mode == RecordingMode.Hold ? action : HybridHotkeyAction.Stop : HybridHotkeyAction.Start;
        }
        // Non-modifier keys outside a configured chord invalidate the gesture.
        if (down && _armed is null && !IsModifier(key)) Block(true);
        if (_down.Count == 0) Block(false);
        return action;
    }

    private void Block(bool blocked)
    {
        _blocked = blocked;
        _blockedWith.Clear();
        if (blocked) _blockedWith.UnionWith(_down);
    }

    // Once none of the keys behind the block is down anymore, the block only waited for a lost release.
    // Keys pressed since then started the next gesture, like after a release that arrived.
    private void UnblockAfterLostRelease()
    {
        if (_blocked && !_blockedWith.Overlaps(_down)) Block(false);
    }

    /// <summary>Whether the held shortcut that started the current recording includes this left or right modifier key.</summary>
    internal bool Holds(int key) => _armed is not null && _startedByGesture && (_down.Contains(key) || _down.Contains(key switch
    {
        0xA0 or 0xA1 => 0x10, 0xA2 or 0xA3 => 0x11, 0xA4 or 0xA5 => 0x12, _ => -1
    }) || key == 0xA2 && _down.Contains(AltGrControl));

    /// <summary>Keys that the last event dropped as released because their key-up was lost.</summary>
    internal IReadOnlyList<int> DroppedKeys => _dropped;

    private bool LostRelease(int pressed, int key, long now, RecordingMode mode)
    {
        if (_armed is not null && (_mode ?? mode) != RecordingMode.Toggle || pressed == key || IsModifier(pressed) || now - _lastEvent[pressed] <= LostReleaseMilliseconds) return false;
        _dropped.Add(pressed);
        return true;
    }

    private static bool IsModifier(int key) => key == AltGrControl || ShortcutKeys.IsModifier(key);
    // With a main key AltGr is Ctrl+Alt, as for RegisterHotKey. Without one it counts as neither,
    // so typing "@" or "Ś" with AltGr does not start and cancel a modifier-only shortcut.
    private string Chord()
    {
        var altGrAlone = _down.Contains(AltGrControl) && _down.All(IsModifier);
        return ShortcutRules.Normalize(string.Join("+", _down.Where(key => !altGrAlone || key is not (AltGrControl or 0xA5)).Select(key => key switch
        {
            0x11 or 0xA2 or 0xA3 or AltGrControl => "CTRL", 0x12 or 0xA4 or 0xA5 => "ALT",
            0x10 or 0xA0 or 0xA1 => "SHIFT", 0x5B or 0x5C => "WIN",
            _ => ShortcutKeys.Token(key)
        }).Distinct()));
    }
}
