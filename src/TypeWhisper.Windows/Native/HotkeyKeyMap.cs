using System.Windows.Input;

namespace TypeWhisper.Windows.Native;

internal static class HotkeyKeyMap
{
    private static readonly HotkeyKeyDefinition[] Definitions =
    [
        new("Space", NativeMethods.VK_SPACE, Key.Space),
        new("Enter", NativeMethods.VK_RETURN, Key.Return),
        new("Backspace", NativeMethods.VK_BACK, Key.Back),
        new("Tab", NativeMethods.VK_TAB, Key.Tab),
        new("Delete", NativeMethods.VK_DELETE, Key.Delete),
        new("Insert", NativeMethods.VK_INSERT, Key.Insert),
        new("Home", NativeMethods.VK_HOME, Key.Home),
        new("End", NativeMethods.VK_END, Key.End),
        new("PageUp", NativeMethods.VK_PRIOR, Key.PageUp),
        new("PageDown", NativeMethods.VK_NEXT, Key.PageDown),
        new("Up", NativeMethods.VK_UP, Key.Up),
        new("Down", NativeMethods.VK_DOWN, Key.Down),
        new("Left", NativeMethods.VK_LEFT, Key.Left),
        new("Right", NativeMethods.VK_RIGHT, Key.Right),
        new("PrintScreen", NativeMethods.VK_SNAPSHOT, Key.PrintScreen, Key.Snapshot),
        new("Pause", NativeMethods.VK_PAUSE, Key.Pause),
        new("ScrollLock", NativeMethods.VK_SCROLL, Key.Scroll),
        new("CapsLock", NativeMethods.VK_CAPITAL, Key.CapsLock),
        new("NumLock", NativeMethods.VK_NUMLOCK, Key.NumLock),
        new("Apps", NativeMethods.VK_APPS, Key.Apps),
        new("Sleep", NativeMethods.VK_SLEEP, Key.Sleep),
        new("NumMultiply", NativeMethods.VK_MULTIPLY, Key.Multiply),
        new("NumAdd", NativeMethods.VK_ADD, Key.Add),
        new("NumSeparator", NativeMethods.VK_SEPARATOR, Key.Separator),
        new("NumSubtract", NativeMethods.VK_SUBTRACT, Key.Subtract),
        new("NumDecimal", NativeMethods.VK_DECIMAL, Key.Decimal),
        new("NumDivide", NativeMethods.VK_DIVIDE, Key.Divide),
        new("BrowserBack", NativeMethods.VK_BROWSER_BACK, Key.BrowserBack),
        new("BrowserForward", NativeMethods.VK_BROWSER_FORWARD, Key.BrowserForward),
        new("BrowserRefresh", NativeMethods.VK_BROWSER_REFRESH, Key.BrowserRefresh),
        new("BrowserStop", NativeMethods.VK_BROWSER_STOP, Key.BrowserStop),
        new("BrowserSearch", NativeMethods.VK_BROWSER_SEARCH, Key.BrowserSearch),
        new("BrowserFavorites", NativeMethods.VK_BROWSER_FAVORITES, Key.BrowserFavorites),
        new("BrowserHome", NativeMethods.VK_BROWSER_HOME, Key.BrowserHome),
        new("VolumeMute", NativeMethods.VK_VOLUME_MUTE, Key.VolumeMute),
        new("VolumeDown", NativeMethods.VK_VOLUME_DOWN, Key.VolumeDown),
        new("VolumeUp", NativeMethods.VK_VOLUME_UP, Key.VolumeUp),
        new("MediaNext", NativeMethods.VK_MEDIA_NEXT_TRACK, Key.MediaNextTrack),
        new("MediaPrevious", NativeMethods.VK_MEDIA_PREV_TRACK, Key.MediaPreviousTrack),
        new("MediaStop", NativeMethods.VK_MEDIA_STOP, Key.MediaStop),
        new("MediaPlayPause", NativeMethods.VK_MEDIA_PLAY_PAUSE, Key.MediaPlayPause),
        new("LaunchMail", NativeMethods.VK_LAUNCH_MAIL, Key.LaunchMail),
        new("MediaSelect", NativeMethods.VK_LAUNCH_MEDIA_SELECT, Key.SelectMedia),
        new("LaunchApp1", NativeMethods.VK_LAUNCH_APP1, Key.LaunchApplication1),
        new("LaunchApp2", NativeMethods.VK_LAUNCH_APP2, Key.LaunchApplication2),
        new("`", NativeMethods.VK_OEM_3, Key.OemTilde),
        new("-", NativeMethods.VK_OEM_MINUS, Key.OemMinus),
        new("=", NativeMethods.VK_OEM_PLUS, Key.OemPlus),
        new("[", NativeMethods.VK_OEM_4, Key.OemOpenBrackets),
        new("]", NativeMethods.VK_OEM_6, Key.OemCloseBrackets),
        new(";", NativeMethods.VK_OEM_1, Key.OemSemicolon),
        new("'", NativeMethods.VK_OEM_7, Key.OemQuotes),
        new(",", NativeMethods.VK_OEM_COMMA, Key.OemComma),
        new(".", NativeMethods.VK_OEM_PERIOD, Key.OemPeriod),
        new("/", NativeMethods.VK_OEM_2, Key.OemQuestion),
        new("\\", NativeMethods.VK_OEM_5, Key.OemBackslash, Key.Oem5)
    ];

    private static readonly Dictionary<Key, string> TokensByKey = BuildTokensByKey();
    private static readonly Dictionary<string, uint> VirtualKeysByToken = Definitions
        .ToDictionary(definition => definition.Token, definition => definition.VirtualKey, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HotkeyMouseButton> MouseButtonsByToken = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MouseLeft"] = HotkeyMouseButton.Left,
        ["MouseRight"] = HotkeyMouseButton.Right,
        ["MouseMiddle"] = HotkeyMouseButton.Middle,
        ["MouseBack"] = HotkeyMouseButton.Back,
        ["MouseForward"] = HotkeyMouseButton.Forward
    };

    /// <summary>
    /// Performs try get token.
    /// </summary>
    public static bool TryGetToken(Key key, out string token)
    {
        if (TokensByKey.TryGetValue(key, out token!))
            return true;

        if (key is >= Key.F1 and <= Key.F24)
        {
            token = key.ToString();
            return true;
        }

        if (key is >= Key.A and <= Key.Z)
        {
            token = key.ToString();
            return true;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            token = ((char)('0' + (key - Key.D0))).ToString();
            return true;
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            token = "Num" + (key - Key.NumPad0);
            return true;
        }

        token = "";
        return false;
    }

    /// <summary>
    /// Performs try get virtual key.
    /// </summary>
    public static bool TryGetVirtualKey(string token, out uint virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (VirtualKeysByToken.TryGetValue(token, out virtualKey))
            return true;

        if (token.StartsWith('F') && token.Length is 2 or 3
            && int.TryParse(token.AsSpan(1), out var functionNumber)
            && functionNumber is >= 1 and <= 24)
        {
            virtualKey = (uint)(NativeMethods.VK_F1 + functionNumber - 1);
            return true;
        }

        if (token.Length == 1)
        {
            if (token[0] is >= 'A' and <= 'Z')
            {
                virtualKey = token[0];
                return true;
            }

            if (token[0] is >= 'a' and <= 'z')
            {
                virtualKey = char.ToUpperInvariant(token[0]);
                return true;
            }

            if (token[0] is >= '0' and <= '9')
            {
                virtualKey = token[0];
                return true;
            }
        }

        if (token.Length == 4
            && token.StartsWith("Num", StringComparison.OrdinalIgnoreCase)
            && token[3] is >= '0' and <= '9')
        {
            virtualKey = (uint)(NativeMethods.VK_NUMPAD0 + (token[3] - '0'));
            return true;
        }

        if (token.Equals("Esc", StringComparison.OrdinalIgnoreCase)
            || token.Equals("Escape", StringComparison.OrdinalIgnoreCase))
        {
            virtualKey = NativeMethods.VK_ESCAPE;
            return true;
        }

        return false;
    }

    public static bool TryGetMouseButton(string token, out HotkeyMouseButton button) =>
        MouseButtonsByToken.TryGetValue(token, out button);

    public static bool TryGetMouseToken(MouseButton button, out string token)
    {
        token = button switch
        {
            MouseButton.Left => "MouseLeft",
            MouseButton.Right => "MouseRight",
            MouseButton.Middle => "MouseMiddle",
            MouseButton.XButton1 => "MouseBack",
            MouseButton.XButton2 => "MouseForward",
            _ => ""
        };
        return token.Length != 0;
    }

    private static Dictionary<Key, string> BuildTokensByKey()
    {
        var tokensByKey = new Dictionary<Key, string>();

        foreach (var definition in Definitions)
        {
            foreach (var key in definition.Keys)
                tokensByKey.TryAdd(key, definition.Token);
        }

        return tokensByKey;
    }
}

internal sealed record HotkeyKeyDefinition(string Token, uint VirtualKey, params Key[] Keys);

internal enum HotkeyMouseButton
{
    Left,
    Right,
    Middle,
    Back,
    Forward
}
