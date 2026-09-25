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
        ("PrintScreen", 0x2C), ("Pause", 0x13), ("ScrollLock", 0x91), ("CapsLock", 0x14), ("NumLock", 0x90), ("Apps", 0x5D), ("Sleep", 0x5F),
        ("NumMultiply", 0x6A), ("NumAdd", 0x6B), ("NumSeparator", 0x6C), ("NumSubtract", 0x6D), ("NumDecimal", 0x6E), ("NumDivide", 0x6F),
        ("BrowserBack", 0xA6), ("BrowserForward", 0xA7), ("BrowserRefresh", 0xA8), ("BrowserStop", 0xA9), ("BrowserSearch", 0xAA), ("BrowserFavorites", 0xAB), ("BrowserHome", 0xAC),
        ("VolumeMute", 0xAD), ("VolumeDown", 0xAE), ("VolumeUp", 0xAF), ("MediaNext", 0xB0), ("MediaPrevious", 0xB1), ("MediaStop", 0xB2), ("MediaPlayPause", 0xB3),
        ("LaunchMail", 0xB4), ("MediaSelect", 0xB5), ("LaunchApp1", 0xB6), ("LaunchApp2", 0xB7),
        ("`", 0xC0), ("-", 0xBD), ("=", 0xBB), ("[", 0xDB), ("]", 0xDD), (";", 0xBA), ("'", 0xDE),
        // A literal comma would split the stored shortcut list.
        ("Comma", 0xBC), (".", 0xBE), ("/", 0xBF), ("\\", 0xDC), ("Oem8", 0xDF), ("Oem102", 0xE2)
    ];
    private static readonly Dictionary<int, string> TokensByKey = Named.ToDictionary(item => item.Key, item => item.Token);
    private static readonly Dictionary<string, int> KeysByToken = Named
        // Windows.System.VirtualKey names, which earlier 1.1 Daily builds stored and registered.
        .Concat<(string Token, int Key)>([("Cancel", 0x03), ("Back", 0x08), ("Clear", 0x0C), ("Return", 0x0D), ("CapitalLock", 0x14),
            ("Kana", 0x15), ("Hangul", 0x15), ("Hanja", 0x19), ("Kanji", 0x19), ("Escape", 0x1B), ("Help", 0x2F),
            ("Application", 0x5D), ("NumberKeyLock", 0x90), ("Scroll", 0x91)])
        .Concat(Run(0x16, "ImeOn", "Junja", "Final", "", "ImeOff", "", "Convert", "NonConvert", "Accept", "ModeChange"))
        .Concat(Run(0x29, "Select", "Print", "Execute", "Snapshot"))
        .Concat(Run(0x6A, "Multiply", "Add", "Separator", "Subtract", "Decimal", "Divide"))
        .Concat(Run(0x88, "NavigationView", "NavigationMenu", "NavigationUp", "NavigationDown", "NavigationLeft", "NavigationRight", "NavigationAccept", "NavigationCancel"))
        .Concat(Run(0xA6, "GoBack", "GoForward", "Refresh", "Stop", "Search", "Favorites", "GoHome"))
        .Concat(Run(0xC3, "GamepadA", "GamepadB", "GamepadX", "GamepadY", "GamepadRightShoulder", "GamepadLeftShoulder", "GamepadLeftTrigger", "GamepadRightTrigger",
            "GamepadDPadUp", "GamepadDPadDown", "GamepadDPadLeft", "GamepadDPadRight", "GamepadMenu", "GamepadView", "GamepadLeftThumbstickButton", "GamepadRightThumbstickButton",
            "GamepadLeftThumbstickUp", "GamepadLeftThumbstickDown", "GamepadLeftThumbstickRight", "GamepadLeftThumbstickLeft",
            "GamepadRightThumbstickUp", "GamepadRightThumbstickDown", "GamepadRightThumbstickRight", "GamepadRightThumbstickLeft"))
        .Concat<(string Token, int Key)>(Enumerable.Range(0, 10).SelectMany(digit => new[] { ($"Number{digit}", 0x30 + digit), ($"NumberPad{digit}", 0x60 + digit) }))
        .ToDictionary(item => item.Token, item => item.Key, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<(string Token, int Key)> Run(int first, params string[] names) =>
        names.Select((name, index) => (name, first + index)).Where(item => item.name.Length > 0);

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
        // MapVirtualKey returns ANSI characters on e.g. Cyrillic layouts, so ask for Unicode.
        // Flag 4 keeps the thread's dead-key state; a dead key (^, ´) returns -1 with its character.
        var buffer = new char[4];
        var count = ToUnicodeEx((uint)key, MapVirtualKey((uint)key, 0), new byte[256], buffer, buffer.Length, 4, GetKeyboardLayout(0));
        return count != 0 && !char.IsControl(buffer[0]) && !char.IsWhiteSpace(buffer[0]) ? buffer[0] : null;
    }

    private static bool IsLayoutKey(int key) => key is >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint key, uint scan, byte[] state, [System.Runtime.InteropServices.Out] char[] buffer, int size, uint flags, IntPtr layout);

    internal static bool IsModifier(int key) => key is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;
}
