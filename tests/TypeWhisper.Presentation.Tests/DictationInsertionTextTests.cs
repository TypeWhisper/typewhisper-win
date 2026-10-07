using TypeWhisper.Presentation;
using Xunit;

// Ported from DictationInsertionTextFormatterTests in the macOS app.
public sealed class DictationInsertionTextTests
{
    private static DictationInsertionContext Context(string value, int location, int length = 0) =>
        new(value[..location], value[(location + length)..]);

    private static readonly DictationInsertionContext EmptyField = new("", "");

    [Theory]
    [InlineData("Hello")]
    [InlineData("Hello ")]
    [InlineData("Hello\n")]
    [InlineData("")]
    public void LeavesTextUnchangedWithoutContext(string text) =>
        Assert.Equal(text, DictationInsertionText.ForPaste(text, null));

    [Fact]
    public void SeparatesConsecutiveDictations() =>
        Assert.Equal(" This is a test.", DictationInsertionText.ForPaste("This is a test.", Context("Hello world.", 12)));

    [Theory]
    // Spaces between words, none before punctuation or after an existing space.
    [InlineData("coffeemachine", 6, 0, "strong", " strong ")]
    [InlineData("coffee machine", 7, 0, "strong", "strong ")]
    [InlineData("Hello,", 5, 0, "friend", " friend")]
    [InlineData("Done.Next", 5, 0, "Another item", " Another item ")]
    // Continuing a sentence: lowercase first word and drop the final period before following text.
    [InlineData("start, I will begin", 5, 0, "Dictation in the middle of a sentence before the comma.",
        " dictation in the middle of a sentence before the comma")]
    [InlineData("dictating.", 9, 0, "my first sentence.", " my first sentence")]
    [InlineData("The presentation will bemachine", 24, 0, "Presented tomorrow", " presented tomorrow ")]
    [InlineData("will begin", 5, 0, "Immediately.", "immediately ")]
    [InlineData("dictating.", 9, 0, " my first sentence ", " my first sentence")]
    [InlineData("we use", 6, 0, "NASA tools.", " NASA tools.")]
    [InlineData("about", 5, 0, "TypeWhisper", " TypeWhisper")]
    [InlineData("coffeemachine", 6, 0, "Strong.", " strong ")]
    [InlineData("coffeemachine", 6, 0, "Really?", " really? ")]
    // Han, Kana and Hangul words are not separated by spaces; mixed Latin boundaries are.
    [InlineData("你好", 1, 0, "世", "世")]
    [InlineData("あい", 1, 0, "カ", "カ")]
    [InlineData("가나", 1, 0, "다", "다")]
    [InlineData("AB", 1, 0, "中", " 中 ")]
    // A replaced selection is ignored; only the text around it counts.
    [InlineData("say hello there", 4, 5, "goodbye", "goodbye")]
    public void FitsTextToTheCursorPosition(string value, int location, int length, string text, string expected) =>
        Assert.Equal(expected, DictationInsertionText.ForPaste(text, Context(value, location, length)));

    [Theory]
    [InlineData("name@example.com.", "name@example.com")]
    [InlineData("https://example.com.", "https://example.com")]
    [InlineData("www.example.com.", "www.example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("3.14.", "3.14")]
    [InlineData("1,5.", "1,5")]
    [InlineData("1,000.50.", "1,000.50")]
    [InlineData("1.000,50.", "1.000,50")]
    [InlineData("+49 171 2345678.", "+49 171 2345678")]
    [InlineData("(030) 123456.", "(030) 123456")]
    [InlineData("1.2.3.", "1.2.3")]
    [InlineData("v2.10.4.", "v2.10.4")]
    // A period is a legal part of URL paths and queries (RFC 3986 section 2.3).
    [InlineData("https://example.com/docs.", "https://example.com/docs.")]
    [InlineData("https://example.com/files/report.", "https://example.com/files/report.")]
    [InlineData("https://example.com/search?q=Dr.", "https://example.com/search?q=Dr.")]
    [InlineData("27. 09. 2026.", "27. 09. 2026.")]
    [InlineData("27 / 09 / 2026.", "27 / 09 / 2026.")]
    [InlineData("2026 - 09 - 27.", "2026 - 09 - 27.")]
    [InlineData("Dr.", "Dr.")]
    [InlineData("U.S.", "U.S.")]
    [InlineData("e.g.", "e.g.")]
    [InlineData("Dr.med.", "Dr.med.")]
    [InlineData("Ph.D.", "Ph.D.")]
    [InlineData("Hello world.", "Hello world.")]
    [InlineData("Contact me at name@example.com.", "Contact me at name@example.com.")]
    [InlineData("19.04.2026.", "19.04.2026.")]
    [InlineData("2026-09-27.", "2026-09-27.")]
    [InlineData("123.", "123.")]
    [InlineData("Wait...", "Wait...")]
    [InlineData("Really?", "Really?")]
    public void StripsModelAddedPeriodFromStandaloneValues(string text, string expected) =>
        Assert.Equal(expected, DictationInsertionText.ForPaste(text, EmptyField));

    [Fact]
    public void StandaloneValueCleanupCanBeTurnedOff() =>
        Assert.Equal("name@example.com.", DictationInsertionText.ForPaste("name@example.com.", EmptyField, false));

    [Fact]
    public void StandaloneValueCleanupNeedsAnEmptyField() =>
        Assert.Equal("name@example.com.", DictationInsertionText.ForPaste("name@example.com.", Context("Email: ", 7)));

    [Fact]
    public void FinalPeriodIsStrippedOnlyOnceMidSentence() =>
        Assert.Equal(" name@example.com ", DictationInsertionText.ForPaste("name@example.com.", Context("ab", 1)));
}
