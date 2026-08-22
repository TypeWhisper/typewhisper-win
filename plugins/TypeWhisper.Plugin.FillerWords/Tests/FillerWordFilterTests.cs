namespace TypeWhisper.Plugin.FillerWords.Tests;

public sealed class FillerWordFilterTests
{
    [Theory]
    [InlineData("So um I think uh this works", "So I think this works")]
    [InlineData("Well, um, that's it.", "Well, that's it.")]
    [InlineData("Um, hello", "hello")]
    [InlineData("hmm let me check", "let me check")]
    [InlineData("Das ist ähm nicht gut", "Das ist nicht gut")]
    public void Remove_StripsLatinFillerWords(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Theory]
    [InlineData("The umbrella is uhh open", "The umbrella is open")]
    [InlineData("A hum in the room", "A hum in the room")]
    [InlineData("Rahm and Graham", "Rahm and Graham")]
    public void Remove_KeepsWordsThatMerelyContainAFiller(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Fact]
    public void Remove_ReturnsInputUnchanged_WhenNothingMatches() =>
        Assert.Equal("A clean sentence.", FillerWordFilter.Remove("A clean sentence."));

    [Fact]
    public void Remove_ReturnsInputUnchanged_WhenWordListIsEmpty() =>
        Assert.Equal("So um I think", FillerWordFilter.Remove("So um I think", []));

    [Fact]
    public void Remove_ReturnsInputUnchanged_WhenTextIsEmpty() =>
        Assert.Equal(string.Empty, FillerWordFilter.Remove(string.Empty));

    [Theory]
    [InlineData(" um hello", " hello")]
    [InlineData("  um hello", "  hello")]
    [InlineData("\tum hello", "\thello")]
    [InlineData(" hello um there", " hello there")]
    public void Remove_PreservesLeadingWhitespaceFromTheOriginal(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Theory]
    [InlineData("um Intro\n  code  block", "Intro\n  code  block")]
    [InlineData("Intro  spaced um text", "Intro  spaced text")]
    [InlineData("hello\n  um there", "hello\n  there")]
    [InlineData("hello um\nworld", "hello\nworld")]
    [InlineData("  indented um line", "  indented line")]
    public void Remove_LeavesWhitespaceOutsideTheMatchUntouched(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Theory]
    [InlineData("Um... hello", "hello")]
    [InlineData("Um… hello", "hello")]
    [InlineData("I said um... then", "I said then")]
    [InlineData("I said um…, then", "I said then")]
    [InlineData("Right?! um yes", "Right?! yes")]
    public void Remove_ConsumesPunctuationAttachedToTheFiller(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Theory]
    [InlineData("Wait... um, no", "Wait... no")]
    [InlineData("Wait… um, no", "Wait… no")]
    [InlineData("Well!!! um okay", "Well!!! okay")]
    [InlineData("Done. um Next.", "Done. Next.")]
    public void Remove_KeepsPunctuationBelongingToSurroundingText(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Fact]
    public void Remove_DropsLeadingWhitespace_WhenEverythingElseWasFiller() =>
        Assert.Equal(string.Empty, FillerWordFilter.Remove(" um "));

    [Fact]
    public void Remove_StripsLeadingWhitespaceIntroducedByRemoval() =>
        Assert.Equal("hello", FillerWordFilter.Remove("um hello"));

    [Fact]
    public void Remove_StripsLineLeadingWhitespaceIntroducedByRemoval() =>
        Assert.Equal("hello\nthere", FillerWordFilter.Remove("hello\num there"));

    [Fact]
    public void Remove_PreservesLineStructure() =>
        Assert.Equal("first line\nsecond line", FillerWordFilter.Remove("first um line\nsecond uh line"));

    [Fact]
    public void Remove_StripsJapaneseFillerWordsWithTheirTrailingComma() =>
        Assert.Equal("これはテストです。", FillerWordFilter.Remove("えっと、これはテストです。"));

    [Fact]
    public void Remove_StripsJapaneseFillerWordsMidSentence() =>
        Assert.Equal("それは、いいですね", FillerWordFilter.Remove("それは、なんかいいですね"));

    [Fact]
    public void Remove_KeepsRepeatedMaaDrawl() =>
        Assert.Equal("まあまあいいです", FillerWordFilter.Remove("まあまあいいです"));

    [Fact]
    public void Remove_HandlesMixedScripts() =>
        Assert.Equal("So これはテストです。", FillerWordFilter.Remove("So um えっと、これはテストです。"));

    [Fact]
    public void NormalizeWords_SplitsOnNewlinesCommasAndSemicolons()
    {
        var words = FillerWordFilter.NormalizeWords("um, uh;\r\nlike\n");

        Assert.Equal(["like", "uh", "um"], words);
    }

    [Fact]
    public void NormalizeWords_LowerCasesTrimsAndDeduplicates()
    {
        var words = FillerWordFilter.NormalizeWords("  Um \n um\nUH");

        Assert.Equal(["uh", "um"], words);
    }

    [Fact]
    public void NormalizeWords_OrdersLongestFirst()
    {
        var words = FillerWordFilter.NormalizeWords("um\nummm\numm");

        Assert.Equal(["ummm", "umm", "um"], words);
    }

    [Fact]
    public void Remove_DoesNotReuseAMatcherBuiltForADifferentWordList()
    {
        // Both lists hold the same words separated differently, so a cache key that
        // simply joins the entries cannot tell them apart.
        const string Input = "you um actually";

        Assert.Equal("you actually", FillerWordFilter.Remove(Input, ["actually you", "um"]));
        Assert.Equal(string.Empty, FillerWordFilter.Remove(Input, ["actually", "you um"]));
    }

    [Fact]
    public void Remove_PrefersTheLongestMatchingFiller() =>
        Assert.Equal("well then", FillerWordFilter.Remove("well umm then", ["um", "umm"]));

    [Fact]
    public void DefaultFillerWords_AreAllNormalized() =>
        Assert.Equal(
            FillerWordFilter.DefaultFillerWords.Count,
            FillerWordFilter.NormalizeWords(FillerWordFilter.DefaultFillerWords).Count);
}
