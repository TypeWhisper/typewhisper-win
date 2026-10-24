namespace TypeWhisper.WinUI;

// Decides which mouse button events belong to a shortcut. Pure; no native input.
internal sealed class MouseShortcutPresses
{
    private const int MiddleDown = 0x207, MiddleUp = 0x208, SideDown = 0x20B, SideUp = 0x20C;
    private readonly HashSet<int> _owned = [];

    /// <summary>Reads a low-level mouse message as a middle (0x04), back (0x05) or forward (0x06) button press or release.</summary>
    internal static bool TryDecode(long message, uint mouseData, out int key, out bool down)
    {
        key = message switch
        {
            MiddleDown or MiddleUp => 0x04,
            // The high word names the side button: 1 is back, 2 is forward.
            SideDown or SideUp => (mouseData >> 16) switch { 1 => 0x05, 2 => 0x06, _ => 0 },
            _ => 0
        };
        down = message is MiddleDown or SideDown;
        return key != 0;
    }

    internal static bool IsButtonMessage(long message) => message is MiddleDown or MiddleUp or SideDown or SideUp;

    /// <summary>Returns whether the event belongs to a shortcut and must not reach other apps.</summary>
    /// <param name="key">The button as a virtual-key code: 0x04, 0x05 or 0x06.</param>
    /// <param name="down">True for a press, false for a release.</param>
    /// <param name="modifiers">The modifiers held during the event, e.g. "CTRL+SHIFT".</param>
    /// <param name="chords">The normalized shortcuts that use a mouse button.</param>
    /// <param name="active">False while the shortcuts are paused; a press then stays with the focused app.</param>
    /// <param name="chord">The normalized shortcut that this press starts, or null.</param>
    internal bool Button(int key, bool down, string modifiers, IReadOnlySet<string> chords, bool active, out string? chord)
    {
        chord = null;
        // A release belongs to the shortcut exactly when its press did, so apps never see half a click.
        if (!down) return _owned.Remove(key);
        var candidate = ShortcutRules.Normalize(modifiers.Length == 0 ? ShortcutKeys.Token(key) : modifiers + "+" + ShortcutKeys.Token(key));
        if (!active || !chords.Contains(candidate)) { _owned.Remove(key); return false; }
        _owned.Add(key);
        chord = candidate;
        return true;
    }

    internal void Reset() => _owned.Clear();
}
