using System.Text;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class ChatMessageSplitterTests
{
    [Fact]
    public void Split_ShortText_IsOneFlattenedMessage()
    {
        Assert.Equal(["Hello team, push mid."], ChatMessageSplitter.Split("  Hello team,\r\n\tpush   mid.  ", 255));
    }

    [Theory]
    [InlineData("/dance now", "dance now")]
    [InlineData("  // quit", "quit")]
    [InlineData("and/or", "and/or")]
    [InlineData("/ /logout", "logout")]
    [InlineData(" /\t/\n/ dance", "dance")]
    public void Split_LeadingSlash_IsNeverSentAsCommand(string text, string expected)
    {
        Assert.Equal([expected], ChatMessageSplitter.Split(text, null));
    }

    [Fact]
    public void Split_LaterMessage_NeverStartsWithASlashEither()
    {
        // The second message would begin with "/ /logout" after the cut at the space.
        var parts = ChatMessageSplitter.Split("first part / /logout now", 12);

        Assert.All(parts, part => Assert.False(part.StartsWith('/'), part));
        Assert.Contains("logout now", string.Join(" ", parts));
    }

    [Theory]
    [InlineData("Tank | Heiler", "Tank / Heiler")]
    [InlineData("|cffff0000rot", "cffff0000rot")]
    public void Split_VerticalBar_NeverStartsAnEscapeCode(string text, string expected)
    {
        Assert.Equal([expected], ChatMessageSplitter.Split(text, 255));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n ")]
    [InlineData("/")]
    public void Split_NothingToSay_ReturnsNoMessage(string text)
    {
        Assert.Empty(ChatMessageSplitter.Split(text, 255));
    }

    [Fact]
    public void Split_PrefersSentenceBoundaries()
    {
        var parts = ChatMessageSplitter.Split("We go left now. Then we take the bridge and wait.", 30);

        Assert.Equal(["We go left now.", "Then we take the bridge and", "wait."], parts);
    }

    [Fact]
    public void Split_FallsBackToWordsAndHardCuts()
    {
        Assert.Equal(["one two", "three"], ChatMessageSplitter.Split("one two three", 8));
        Assert.Equal(["abcde", "fghij", "k"], ChatMessageSplitter.Split("abcdefghijk", 5));
    }

    [Fact]
    public void Split_CountsUtf8Bytes()
    {
        // "ü" and "ß" take two bytes each, so "Grüße" is 7 bytes long.
        Assert.Equal(["Grüße"], ChatMessageSplitter.Split("Grüße", 7));
        Assert.Equal(["Grüß", "e"], ChatMessageSplitter.Split("Grüße", 6));
    }

    [Fact]
    public void Split_UmlautText_StaysWithinTheByteLimit()
    {
        var text = string.Concat(Enumerable.Repeat("Wir müssen gleich zum Boss, äh, wer hat noch Fläschchen übrig? ", 12));

        var parts = ChatMessageSplitter.Split(text, 255);

        Assert.True(parts.Count > 1);
        Assert.All(parts, part => Assert.InRange(Encoding.UTF8.GetByteCount(part), 1, 255));
        Assert.Equal(text.Trim(), string.Join(" ", parts));
    }

    [Fact]
    public void Split_DoesNotCutSurrogatePairs()
    {
        // The emoji takes four bytes.
        Assert.Equal(["abc", "😀", "def"], ChatMessageSplitter.Split("abc😀def", 4));
    }

    [Fact]
    public void Split_FullWidthSentencesSplitWithoutSpaces()
    {
        // Each character takes three bytes.
        Assert.Equal(["左へ行く。", "橋を渡る。"], ChatMessageSplitter.Split("左へ行く。橋を渡る。", 15));
    }

    [Fact]
    public void Split_TinyLimit_StillMakesProgress()
    {
        Assert.Equal(["ä", "ö"], ChatMessageSplitter.Split("äö", 1));
    }

    [Fact]
    public void Split_EveryPartRespectsTheLimit()
    {
        var text = string.Concat(Enumerable.Repeat("This is a long chat test to check how messages are split. ", 10));

        var parts = ChatMessageSplitter.Split(text, 255);

        Assert.All(parts, part => Assert.InRange(part.Length, 1, 255));
        Assert.Equal(text.Trim(), string.Join(" ", parts));
    }
}
