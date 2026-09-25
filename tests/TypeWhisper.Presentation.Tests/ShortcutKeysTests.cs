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
    [InlineData("Ctrl+Foo")]
    [InlineData("Ctrl+VK0")]
    [InlineData("Ctrl+300")]
    public void UnknownKeysAreRejected(string candidate) =>
        Assert.NotNull(ShortcutRules.Validate(candidate, allowModifiersOnly: true));
}
