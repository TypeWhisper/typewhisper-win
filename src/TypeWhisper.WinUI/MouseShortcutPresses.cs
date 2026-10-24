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

    /// <summary>Whether a shortcut owns this button's press and its release has not happened yet.</summary>
    internal bool Holds(int key) => _owned.Contains(key);

    /// <summary>The replayed modifiers whose real release, after the click's press, is already known, ordered by time.</summary>
    /// <param name="modifiers">The modifiers replayed with the click's press.</param>
    /// <param name="press">The click's press time on the Windows uptime clock.</param>
    /// <param name="released">A modifier's last release time seen by the keyboard hook, if any.</param>
    internal static (int Modifier, uint Time)[] KnownReleases(int[] modifiers, uint press, Func<int, uint?> released) =>
        modifiers.Select(modifier => (modifier, time: released(modifier)))
            .Where(step => step.time is { } time && unchecked((int)(time - press)) > 0)
            .Select(step => (step.modifier, step.time!.Value))
            .OrderBy(step => unchecked((int)(step.Value - press)))
            .ToArray();

    /// <summary>The modifiers of a matched click that are no longer held, as virtual keys to replay around the click.</summary>
    /// <param name="chord">The shortcut the hook matched at event time, or null.</param>
    /// <param name="downNow">Whether a generic modifier key is held now.</param>
    /// <remarks>The UI thread handles a click after the keyboard events queued before it. A modifier released in
    /// between would otherwise be missing from the chord, and the shortcut would silently do nothing.</remarks>
    /// <summary>The virtual key that ReleasedModifiers uses for a modifier key, or 0 for any other key.</summary>
    internal static int ReplayKey(int key) => key switch
    {
        0x11 or 0xA2 or 0xA3 => 0xA2, 0x12 or 0xA4 or 0xA5 => 0xA4, 0x10 or 0xA0 or 0xA1 => 0xA0, 0x5B or 0x5C => 0x5B, _ => 0
    };

    /// <summary>When to replay a modifier's release: its real release time if that came after the click, else the click's.</summary>
    /// <param name="click">The click's time on the Windows uptime clock.</param>
    /// <param name="released">The modifier's last release time seen by the keyboard hook, if any.</param>
    /// <remarks>Uptime subtraction stays valid across the 49-day wrap. A hold longer than the tap limit stays a hold.</remarks>
    internal static uint ReplayReleaseTime(uint click, uint? released) =>
        released is { } time && unchecked((int)(time - click)) > 0 ? time : click;

    /// <summary>Orders the replayed releases of a click's modifiers by event time, each on its side of the click's own release.</summary>
    /// <param name="modifiers">The modifiers replayed with the click's press.</param>
    /// <param name="press">The click's press time on the Windows uptime clock.</param>
    /// <param name="release">The click's release time.</param>
    /// <param name="released">A modifier's last release time seen by the keyboard hook, if any.</param>
    internal static (int Modifier, uint Time, bool BeforeClick)[] PlanReleases(int[] modifiers, uint press, uint release, Func<int, uint?> released) =>
        modifiers.Select(modifier =>
        {
            var time = ReplayReleaseTime(press, released(modifier));
            return (modifier, time, unchecked((int)(time - release)) <= 0);
        }).OrderBy(step => unchecked((int)(step.time - press))).ToArray();

    internal static int[] ReleasedModifiers(string? chord, Func<int, bool> downNow)
    {
        if (chord is null) return [];
        var parts = chord.Split('+');
        return new (string Name, int Key)[] { ("CTRL", 0xA2), ("ALT", 0xA4), ("SHIFT", 0xA0), ("WIN", 0x5B) }
            .Where(modifier => parts.Contains(modifier.Name) && !(modifier.Name switch
            {
                "CTRL" => downNow(0x11), "ALT" => downNow(0x12), "SHIFT" => downNow(0x10), _ => downNow(0x5B) || downNow(0x5C)
            }))
            .Select(modifier => modifier.Key).ToArray();
    }
}
