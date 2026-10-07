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
    [InlineData("\u4f60\u597d", 1, 0, "\u4e16", "\u4e16")]
    [InlineData("\u3042\u3044", 1, 0, "\u30ab", "\u30ab")]
    [InlineData("\uac00\ub098", 1, 0, "\ub2e4", "\ub2e4")]
    [InlineData("AB", 1, 0, "\u4e2d", " \u4e2d ")]
    // A replaced selection is ignored; only the text around it counts.
    [InlineData("say hello there", 4, 5, "goodbye", "goodbye")]
    // Character classes checked against the Swift formatter on macOS.
    [InlineData("Cafe\u0301", 5, 0, "Noir", " noir")]
    [InlineData("word\u00a0", 5, 0, "Next", "next")]
    [InlineData("a\ufffc", 2, 0, "Hi", "Hi")]
    [InlineData("Nice \ud83d\udc4d", 7, 0, "Thanks", "Thanks")]
    [InlineData("x\u0661\u0662", 3, 0, "Abc", " abc")]
    [InlineData("the ", 4, 0, "\u01c5ungla", "\u01c6ungla")]
    [InlineData("in ", 3, 0, "\u0130stanbul", "i\u0307stanbul")]
    // Kana marks shared by Hiragana and Katakana count as CJK; a combining mark after a Latin letter does not.
    [InlineData("\u30ab\u30fc", 1, 0, "\u30c9", "\u30c9")]
    [InlineData("\uff76\uff77", 1, 0, "\uff9e", "\uff9e")]
    [InlineData("a\u0323", 2, 0, "o\u0323c", " o\u0323c")]
    // A straight double quote after a word closes a quotation; elsewhere, and an apostrophe, still opens one.
    [InlineData("He said \"hello\"", 15, 0, "Next sentence.", " Next sentence.")]
    [InlineData("He said \"hello.\"", 16, 0, "Next.", " Next.")]
    [InlineData("He said \"", 9, 0, "Hello", "Hello")]
    [InlineData("l'", 2, 0, "amour", "amour")]
    [InlineData("{\"key\":\"", 8, 0, "value", "value")]
    [InlineData("x=\"", 3, 0, "Value", "Value")]
    [InlineData("He said \"\ud83d\udc4d\"", 12, 0, "Next.", " Next.")]
    [InlineData("It grew 50%\"", 12, 0, "Next.", " Next.")]
    [InlineData("He answered \"$\"", 15, 0, "Next.", " Next.")]
    [InlineData("Er sagte \u201eHallo\u201c", 16, 0, "N\u00e4chster Satz.", " N\u00e4chster Satz.")]
    [InlineData("Er sagte \u201e", 10, 0, "Hallo", "Hallo")]
    // A line break next to the caret ends the sentence on that side.
    [InlineData("I think \nNext paragraph", 8, 0, "This is right.", "this is right.")]
    [InlineData("Hello world\n", 12, 0, "Next one", "Next one")]
    [InlineData("Line\r\n", 6, 0, "Next", "Next")]
    [InlineData("Price \"\u20ac\"", 9, 0, "Next.", " Next.")]
    [InlineData("\u0532\u0561\u0580\u0587\u0589", 5, 0, "\u053b\u0576\u0579\u057a\u0565\u055e\u057d \u0565\u0584\u0589", " \u053b\u0576\u0579\u057a\u0565\u055e\u057d \u0565\u0584\u0589")]
    [InlineData("\u03a4\u03b9 \u03ba\u03ac\u03bd\u03b5\u03b9\u03c2\u037e", 10, 0, "\u039a\u03b1\u03bb\u03ac", " \u039a\u03b1\u03bb\u03ac")]
    [InlineData("\u1230\u120b\u121d\u1362", 4, 0, "\u12a5\u1295\u12f4\u1275 \u1290\u1205", " \u12a5\u1295\u12f4\u1275 \u1290\u1205")]
    [InlineData("What\u203d", 5, 0, "Really", " Really")]
    // Sentence punctuation of other space-separated scripts, and the ellipsis character.
    [InlineData("\u0645\u0631\u062d\u0628\u0627\u061f", 6, 0, "\u0643\u064a\u0641 \u062d\u0627\u0644\u0643\u061f", " \u0643\u064a\u0641 \u062d\u0627\u0644\u0643\u061f")]
    [InlineData("\u0645\u0631\u062d\u0628\u0627\u060c", 5, 0, "\u0635\u062f\u064a\u0642\u064a", " \u0635\u062f\u064a\u0642\u064a")]
    [InlineData("\u0928\u092e\u0938\u094d\u0924\u0947\u0964", 7, 0, "\u0906\u092a \u0915\u0948\u0938\u0947 \u0939\u0948\u0902", " \u0906\u092a \u0915\u0948\u0938\u0947 \u0939\u0948\u0902")]
    [InlineData("Wait\u2026", 5, 0, "Okay", " Okay")]
    // Spoken line breaks and tabs at either end stay; spaces there do not. A kept break also ends the sentence
    // context, so the dictation keeps its casing and final period.
    [InlineData("Hello.", 6, 0, "\nThanks", "\nThanks")]
    [InlineData("ab", 1, 0, "Hello\n", " hello\n")]
    [InlineData("ab", 1, 0, "Hello.\n", " hello.\n")]
    [InlineData("Intro", 5, 0, " \tNext point. \n ", "\tNext point.\n")]
    [InlineData("ab", 2, 0, "  \n", "\n")]
    public void FitsTextToTheCursorPosition(string value, int location, int length, string text, string expected) =>
        Assert.Equal(expected, DictationInsertionText.ForPaste(text, Context(value, location, length)));

    [Theory]
    [InlineData("de", "H\u00e4user.")]
    [InlineData("de-CH", "H\u00e4user.")]
    [InlineData("lb", "H\u00e4user.")]
    [InlineData("en", "h\u00e4user.")]
    [InlineData(null, "h\u00e4user.")]
    public void KeepsTheFirstWordsCasingInLanguagesThatCapitalizeNouns(string? language, string expected) =>
        Assert.Equal(expected, DictationInsertionText.ForPaste("H\u00e4user.", Context("Ich sehe ", 9), language: language));

    [Theory]
    [InlineData("name@example.com.", "name@example.com")]
    [InlineData("https://example.com.", "https://example.com")]
    [InlineData("www.example.com.", "www.example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("3.14.", "3.14")]
    [InlineData("1,5.", "1,5")]
    [InlineData("1,000.50.", "1,000.50")]
    [InlineData("1.000,50.", "1.000,50")]
    [InlineData("-3.14.", "-3.14")]
    [InlineData("+1,5.", "+1,5")]
    [InlineData("\u22122.5.", "\u22122.5")]
    [InlineData("+49 171 2345678.", "+49 171 2345678")]
    [InlineData("(030) 123456.", "(030) 123456")]
    [InlineData("1.2.3.", "1.2.3")]
    [InlineData("v2.10.4.", "v2.10.4")]
    [InlineData("Name@Example.COM.", "Name@Example.COM")]
    [InlineData("\u0661\u0662\u0663.\u0664\u0665.", "\u0661\u0662\u0663.\u0664\u0665")]
    [InlineData("example.com:8080.", "example.com:8080")]
    [InlineData("localhost:8080.", "localhost:8080.")]
    [InlineData("WAIT.", "WAIT.")]
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
