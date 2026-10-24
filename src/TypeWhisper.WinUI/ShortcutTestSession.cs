namespace TypeWhisper.WinUI;

internal enum ShortcutTestVerdict { Waiting, Works, Paused, NotResponding, WrongOrder, DifferentKeys, NothingReceived }

// Compares what Windows delivered during a shortcut test with what TypeWhisper reacted to. Pure; no native input.
// Timestamps are monotonic milliseconds.
internal sealed class ShortcutTestSession
{
    internal const long ListenMilliseconds = 8000;
    // How long a delivered shortcut may take to reach the registration that owns it.
    internal const long ResponseMilliseconds = 500;
    private readonly HashSet<string> _expected;
    private readonly HashSet<int> _down = [];
    private readonly long _started;
    private long? _deliveredAt;
    // Like the dictation hook, a key outside the shortcut spoils the gesture until every key is released.
    private bool _broken;
    private bool _wrongOrder;

    internal ShortcutTestSession(IEnumerable<string> expected, long now)
    {
        _expected = expected.Select(ShortcutRules.Normalize).Where(chord => chord.Length > 0).ToHashSet();
        _started = now;
    }

    /// <summary>The last chord that Windows delivered, unless it was a modifier on its own.</summary>
    internal string? Seen { get; private set; }
    /// <summary>The configured shortcut that Windows delivered in a valid order, or null.</summary>
    internal string? DeliveredChord { get; private set; }
    /// <summary>The configured shortcut that TypeWhisper reacted to.</summary>
    internal string? Responded { get; private set; }

    /// <summary>Records a key or mouse button as Windows delivered it, before any shortcut handling.</summary>
    /// <param name="key">A virtual-key code; the middle and side mouse buttons use 0x04 to 0x06.</param>
    /// <param name="down">True for a press, false for a release.</param>
    /// <param name="now">The current monotonic time in milliseconds.</param>
    internal void Delivered(int key, bool down, long now)
    {
        if (!down)
        {
            _down.Remove(key);
            if (_down.Count == 0) _broken = false;
            return;
        }
        if (!_down.Add(key)) return;
        var chord = Chord(_down);
        if (!_expected.Contains(chord)) _broken |= !ShortcutKeys.IsModifier(key);
        else if (_broken) _wrongOrder = true;
        else if (_deliveredAt is null) { _deliveredAt = now; DeliveredChord = chord; }
        if (_down.Any(pressed => !ShortcutKeys.IsModifier(pressed)) || _down.Count > 1) Seen = chord;
    }

    internal void Respond(string chord)
    {
        var normalized = ShortcutRules.Normalize(chord);
        if (_expected.Contains(normalized)) Responded = normalized;
    }

    internal ShortcutTestVerdict Verdict(long now, bool paused)
    {
        if (Responded is not null) return ShortcutTestVerdict.Works;
        if (paused) return ShortcutTestVerdict.Paused;
        if (_deliveredAt is { } delivered)
            return now - delivered >= ResponseMilliseconds ? ShortcutTestVerdict.NotResponding : ShortcutTestVerdict.Waiting;
        if (now - _started < ListenMilliseconds) return ShortcutTestVerdict.Waiting;
        return _wrongOrder ? ShortcutTestVerdict.WrongOrder
            : Seen is null ? ShortcutTestVerdict.NothingReceived : ShortcutTestVerdict.DifferentKeys;
    }

    /// <summary>Whether a function key of the shortcut could arrive as a media key, or not at all, on a laptop keyboard.</summary>
    internal bool ExpectsFunctionKey => _expected.Any(chord =>
        ShortcutKeys.TryParse(chord.Split('+')[^1], out var key) && key is >= 0x70 and <= 0x87);

    /// <summary>Whether Windows delivered a browser, volume, media or launch key where the shortcut has a function key.</summary>
    internal bool FunctionKeyArrivedAsMediaKey => ExpectsFunctionKey && Seen is { } seen
        && ShortcutKeys.TryParse(seen.Split('+')[^1], out var key) && key is >= 0xA6 and <= 0xB7;

    private static string Chord(IEnumerable<int> keys) => ShortcutRules.Normalize(string.Join("+", keys.Select(key => key switch
    {
        0x11 or 0xA2 or 0xA3 => "CTRL", 0x12 or 0xA4 or 0xA5 => "ALT",
        0x10 or 0xA0 or 0xA1 => "SHIFT", 0x5B or 0x5C => "WIN",
        _ => ShortcutKeys.Token(key)
    }).Distinct()));
}
