using TypeWhisper.WinUI;
using Xunit;

public class ShortcutKeysTests
{
    [Theory]
    [InlineData(0xDC, "\\")]
    [InlineData(0xBC, "Comma")]
    [InlineData(0xC0, "`")]
    [InlineData(0xE2, "Oem102")]
    [InlineData(0x2D, "Insert")]
    [InlineData(0x21, "PageUp")]
    [InlineData(0x26, "Up")]
    [InlineData(0x6A, "NumMultiply")]
    [InlineData(0x61, "Num1")]
    [InlineData(0x20, "Space")]
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
    [InlineData("control+shift+a", "CTRL+SHIFT+A")]
    public void StoredValuesFromEarlierBuildsMatchTheHookChord(string stored, string normalized) =>
        Assert.Equal(normalized, ShortcutRules.Normalize(stored));

    [Fact]
    public void StoredNumericKeyIsShownAsItsKey() => Assert.Equal("\\", ShortcutKeys.Label("220"));

    [Theory]
    [InlineData("Ctrl+Foo")]
    [InlineData("Ctrl+VK0")]
    [InlineData("Ctrl+300")]
    public void UnknownKeysAreRejected(string candidate) =>
        Assert.NotNull(ShortcutRules.Validate(candidate, allowModifiersOnly: true));
}
