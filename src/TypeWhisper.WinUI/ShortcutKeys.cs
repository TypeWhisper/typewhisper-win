namespace TypeWhisper.WinUI;

// One key vocabulary for the shortcut recorder, the dictation hook and RegisterHotKey.
// Tokens follow the 1.0 hotkey map so imported shortcuts keep matching.
internal static class ShortcutKeys
{
    private static readonly (string Token, int Key)[] Named =
    [
        ("Space", 0x20), ("Enter", 0x0D), ("Backspace", 0x08), ("Tab", 0x09), ("Esc", 0x1B),
        ("Delete", 0x2E), ("Insert", 0x2D), ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22),
        ("Up", 0x26), ("Down", 0x28), ("Left", 0x25), ("Right", 0x27),
        ("PrintScreen", 0x2C), ("Pause", 0x13), ("ScrollLock", 0x91), ("CapsLock", 0x14), ("NumLock", 0x90), ("Apps", 0x5D),
        ("NumMultiply", 0x6A), ("NumAdd", 0x6B), ("NumSeparator", 0x6C), ("NumSubtract", 0x6D), ("NumDecimal", 0x6E), ("NumDivide", 0x6F),
        ("`", 0xC0), ("-", 0xBD), ("=", 0xBB), ("[", 0xDB), ("]", 0xDD), (";", 0xBA), ("'", 0xDE),
        // A literal comma would split the stored shortcut list.
        ("Comma", 0xBC), (".", 0xBE), ("/", 0xBF), ("\\", 0xDC), ("Oem102", 0xE2)
    ];
    private static readonly Dictionary<int, string> TokensByKey = Named.ToDictionary(item => item.Key, item => item.Token);
    private static readonly Dictionary<string, int> KeysByToken = Named
        // Windows.System.VirtualKey names, which earlier 1.1 Daily builds stored for these keys.
        .Concat<(string Token, int Key)>([("Escape", 0x1B), ("Return", 0x0D), ("Back", 0x08), ("CapitalLock", 0x14), ("Scroll", 0x91), ("NumberKeyLock", 0x90),
            ("Snapshot", 0x2C), ("Application", 0x5D), ("Multiply", 0x6A), ("Add", 0x6B), ("Separator", 0x6C),
            ("Subtract", 0x6D), ("Decimal", 0x6E), ("Divide", 0x6F)])
        .ToDictionary(item => item.Token, item => item.Key, StringComparer.OrdinalIgnoreCase);

    internal static string Token(int key) => key switch
    {
        >= 'A' and <= 'Z' or >= '0' and <= '9' => ((char)key).ToString(),
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",
        >= 0x60 and <= 0x69 => $"Num{key - 0x60}",
        _ => TokensByKey.GetValueOrDefault(key, $"VK{key}")
    };

    // Also accepts raw virtual-key numbers ("220", "VK220"), which earlier 1.1 Daily builds stored for unnamed keys.
    internal static bool TryParse(string token, out int key)
    {
        key = 0;
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (KeysByToken.TryGetValue(token, out key)) return true;
        if (token.Length == 1 && char.ToUpperInvariant(token[0]) is var single and (>= 'A' and <= 'Z' or >= '0' and <= '9'))
        { key = single; return true; }
        if (token.Length is 2 or 3 && token[0] is 'F' or 'f' && int.TryParse(token.AsSpan(1), out var function) && function is >= 1 and <= 24)
        { key = 0x6F + function; return true; }
        if (token.Length == 4 && token.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && token[3] is >= '0' and <= '9')
        { key = 0x60 + token[3] - '0'; return true; }
        var number = token.StartsWith("VK", StringComparison.OrdinalIgnoreCase) ? token.AsSpan(2) : token.AsSpan();
        if (int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out key)
            && key is >= 1 and <= 254 && !IsModifier(key)) return true;
        key = 0;
        return false;
    }

    // Punctuation keys are stored by position; show the character they type on the
    // active layout instead, e.g. "<" or "^" on a German keyboard.
    internal static string Label(string part, Func<int, char?>? layout = null) => !TryParse(part, out var key) ? part
        : IsLayoutKey(key) && layout?.Invoke(key) is { } character ? char.ToUpperInvariant(character).ToString() : Token(key);

    internal static string Display(string chord, Func<int, char?>? layout = null) => string.Join(" + ",
        chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(part => Label(part, layout)));

    internal static char? LayoutCharacter(int key)
    {
        // The high bit marks a dead key such as ^ on German layouts; its character is still the label.
        var character = MapVirtualKey((uint)key, 2) & 0x7FFFFFFF;
        return character is > 0x20 and < 0xFFFF && !char.IsControl((char)character) ? (char)character : null;
    }

    private static bool IsLayoutKey(int key) => key is >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);

    internal static bool IsModifier(int key) => key is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;
}
