using TypeWhisper.WinUI;
using Xunit;

public class ShortcutKeysTests
{
    [Theory]
    [InlineData(0xDC, "\\")]
    [InlineData(0xBC, "Comma")]
    [InlineData(0xC0, "`")]
    [InlineData(0xE2, "Oem102")]
    [InlineData(0xDF, "Oem8")]
    [InlineData(0x2D, "Insert")]
    [InlineData(0x21, "PageUp")]
    [InlineData(0x26, "Up")]
    [InlineData(0x6A, "NumMultiply")]
    [InlineData(0x61, "Num1")]
    [InlineData(0x20, "Space")]
    [InlineData(0xA6, "BrowserBack")]
    [InlineData(0xB3, "MediaPlayPause")]
    public void RecordedKeyStartsDictationFromTheHook(int key, string token)
    {
        Assert.Equal(token, ShortcutKeys.Token(key));
        var recorded = "Ctrl+" + ShortcutKeys.Token(key);
        Assert.Null(ShortcutRules.Validate(recorded, allowModifiersOnly: true));
        var bindings = new HashSet<string> { ShortcutRules.Normalize(recorded) };
        var state = new HybridHotkeyState();
        Assert.Null(state.Key(0xA2, true, 0, bindings));
        Assert.Equal(HybridHotkeyAction.Start, state.Key(key, true, 0, bindings));
    }

    [Theory]
    [InlineData(0x2D, "Insert")]
    [InlineData(0x13, "Pause")]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    [InlineData(0xA6, "BrowserBack")]
    [InlineData(0xAD, "VolumeMute")]
    [InlineData(0xB3, "MediaPlayPause")]
    [InlineData(0xB7, "LaunchApp2")]
    public void KeyWithoutTypingJobWorksAlone(int key, string token)
    {
        Assert.Null(ShortcutRules.Validate(token, allowModifiersOnly: true));
        Assert.Null(ShortcutRules.Validate(token, allowModifiersOnly: false));
        var bindings = new HashSet<string> { ShortcutRules.Normalize(token) };
        var state = new HybridHotkeyState();
        Assert.Equal(HybridHotkeyAction.Start, state.Key(key, true, 0, bindings));
        Assert.Equal(HybridHotkeyAction.Stop, state.Key(key, false, HybridHotkeyState.HoldMilliseconds, bindings, recording: true));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("5")]
    [InlineData("Space")]
    [InlineData("Enter")]
    [InlineData("Tab")]
    [InlineData("Backspace")]
    [InlineData("Esc")]
    [InlineData("Delete")]
    [InlineData("Home")]
    [InlineData("Up")]
    [InlineData("Comma")]
    [InlineData("Num1")]
    [InlineData("NumAdd")]
    [InlineData("CapsLock")]
    [InlineData("NumLock")]
    [InlineData("ScrollLock")]
    [InlineData("Apps")]
    [InlineData("PrintScreen")]
    [InlineData("Sleep")]
    public void KeyThatTypesOrTogglesNeedsAModifier(string token)
    {
        Assert.NotNull(ShortcutRules.Validate(token, allowModifiersOnly: true));
        Assert.Null(ShortcutRules.Validate("Ctrl+" + token, allowModifiersOnly: true));
    }

    [Fact]
    public void CopyAndPasteWithInsertDoNotStartAnInsertShortcut()
    {
        var bindings = new HashSet<string> { "INSERT" };
        var state = new HybridHotkeyState();
        Assert.Null(state.Key(0xA2, true, 0, bindings));
        Assert.Null(state.Key(0x2D, true, 0, bindings));
        Assert.Null(state.Key(0x2D, false, 0, bindings));
        Assert.Null(state.Key(0xA2, false, 0, bindings));
        Assert.Null(state.Key(0xA0, true, 0, bindings));
        Assert.Null(state.Key(0x2D, true, 0, bindings));
    }

    [Theory]
    [InlineData("Ctrl+220", "CTRL+\\")]
    [InlineData("Ctrl+VK220", "CTRL+\\")]
    [InlineData("Ctrl+Snapshot", "CTRL+PRINTSCREEN")]
    [InlineData("Ctrl+Escape", "CTRL+ESC")]
    [InlineData("Ctrl+GoBack", "CTRL+BROWSERBACK")]
    [InlineData("Ctrl+BrowserBack", "CTRL+BROWSERBACK")]
    [InlineData("Ctrl+VolumeMute", "CTRL+VOLUMEMUTE")]
    [InlineData("Ctrl+MediaPlayPause", "CTRL+MEDIAPLAYPAUSE")]
    [InlineData("Ctrl+Sleep", "CTRL+SLEEP")]
    [InlineData("Ctrl+Convert", "CTRL+VK28")]
    [InlineData("Ctrl+NumberPad1", "CTRL+NUM1")]
    [InlineData("control+shift+a", "CTRL+SHIFT+A")]
    public void StoredValuesFromEarlierBuildsMatchTheHookChord(string stored, string normalized) =>
        Assert.Equal(normalized, ShortcutRules.Normalize(stored));

    [Fact]
    public void StoredNumericKeyIsShownAsItsKey() => Assert.Equal("\\", ShortcutKeys.Label("220"));

    [Fact]
    public void PunctuationKeysShowTheCharacterOfTheActiveLayout()
    {
        // German layout: OEM_102 types "<", OEM_5 the dead key "^", OEM_1 "ü".
        var german = new Dictionary<int, char> { [0xE2] = '<', [0xDC] = '^', [0xBA] = 'ü', ['Z'] = 'z' };
        char? Layout(int key) => german.TryGetValue(key, out var character) ? character : null;
        Assert.Equal("CTRL + <", ShortcutKeys.Display("CTRL+OEM102", Layout));
        Assert.Equal("Ctrl + ^", ShortcutKeys.Display("Ctrl+220", Layout));
        Assert.Equal("Ü", ShortcutKeys.Label(";", Layout));
        Assert.Equal("Z", ShortcutKeys.Label("Z", Layout));
        Assert.Equal("Space", ShortcutKeys.Label("SPACE", Layout));
        Assert.Equal("Oem102", ShortcutKeys.Label("OEM102"));
    }

    [Theory]
    [InlineData("Ctrl+Alt+S", "ś")]
    [InlineData("Alt+Ctrl+Shift+S", "Ś")]
    [InlineData("Ctrl+Alt+Comma", "<")]
    [InlineData("Ctrl+Alt+A", null)]
    [InlineData("Ctrl+S", null)]
    [InlineData("Alt+Shift+S", null)]
    [InlineData("Ctrl+Alt+Win+S", null)]
    [InlineData("Ctrl+Alt", null)]
    public void CtrlAltShortcutsMustNotSwallowAltGrCharacters(string candidate, string? typed)
    {
        // Polish programmer's layout for S, Czech for the comma key.
        char? Layout(int key, bool shift) => key switch { 'S' => shift ? 'Ś' : 'ś', 0xBC when !shift => '<', _ => null };
        var error = ShortcutRules.AltGrConflict(candidate, Layout);
        if (typed is null) Assert.Null(error);
        else Assert.Contains($"\"{typed}\"", error);
    }

    [Theory]
    [InlineData("Ctrl+Foo")]
    [InlineData("Ctrl+VK0")]
    [InlineData("Ctrl+300")]
    public void UnknownKeysAreRejected(string candidate) =>
        Assert.NotNull(ShortcutRules.Validate(candidate, allowModifiersOnly: true));

    [Theory]
    [InlineData(0x41, "text")]
    [InlineData(0x35, "text")]
    [InlineData(0x20, "text")]
    [InlineData(0xBE, "text")]
    [InlineData(0x65, "text")]
    [InlineData(0xFF, "vk-FF")]
    [InlineData(0xAF, "vk-AF")]
    [InlineData(0x7C, "vk-7C")]
    public void DiagnosticKindNeverNamesTextKeys(int key, string kind) => Assert.Equal(kind, ShortcutKeys.DiagnosticKind(key));
}
