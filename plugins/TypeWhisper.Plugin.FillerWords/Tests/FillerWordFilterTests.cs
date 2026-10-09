namespace TypeWhisper.Plugin.FillerWords.Tests;

public sealed class FillerWordFilterTests
{
    private const string English = "en";

    [Theory]
    [InlineData("So um I think uh this works", "So I think this works")]
    [InlineData("Well, um, that's it.", "Well, that's it.")]
    [InlineData("Um, hello", "Hello")]
    [InlineData("hmm let me check", "let me check")]
    [InlineData("Das ist ähm nicht gut", "Das ist nicht gut")]
    public void Remove_StripsLatinFillerWords(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input, English));

    [Theory]
    [InlineData("The umbrella is uhh open", "The umbrella is open")]
    [InlineData("A hum in the room", "A hum in the room")]
    [InlineData("Rahm and Graham", "Rahm and Graham")]
    public void Remove_KeepsWordsThatMerelyContainAFiller(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input, English));

    [Fact]
    public void Remove_ReturnsInputUnchanged_WhenNothingMatches() =>
        Assert.Equal("A clean sentence.", FillerWordFilter.Remove("A clean sentence.", English));

    [Fact]
    public void Remove_ReturnsInputUnchanged_WhenWordListIsEmpty() =>
        Assert.Equal("So um I think", FillerWordFilter.Remove("So um I think", [], English));

    [Fact]
    public void Remove_ReturnsInputUnchanged_WhenTextIsEmpty() =>
        Assert.Equal(string.Empty, FillerWordFilter.Remove(string.Empty, English));

    [Fact]
    public void Remove_ReturnsLongWhitespaceOnlyInputUnchanged()
    {
        var input = new string(' ', 10_000);

        Assert.Equal(input, FillerWordFilter.Remove(input, English));
    }

    [Theory]
    [InlineData(" um hello", " hello")]
    [InlineData("  um hello", "  hello")]
    [InlineData("\tum hello", "\thello")]
    [InlineData(" hello um there", " hello there")]
    public void Remove_PreservesLeadingWhitespaceFromTheOriginal(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input, English));

    [Theory]
    [InlineData("um Intro\n  code  block", "Intro\n  code  block")]
    [InlineData("Intro  spaced um text", "Intro  spaced text")]
    [InlineData("hello\n  um there", "hello\n  there")]
    [InlineData("hello um\nworld", "hello\nworld")]
    [InlineData("  indented um line", "  indented line")]
    public void Remove_LeavesWhitespaceOutsideTheMatchUntouched(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input, English));

    [Theory]
    [InlineData("Um... hello", "Hello")]
    [InlineData("Um… hello", "Hello")]
    [InlineData("I said um... then", "I said then")]
    [InlineData("I said um…, then", "I said then")]
    [InlineData("Right?! um yes", "Right?! yes")]
    public void Remove_ConsumesPunctuationAttachedToTheFiller(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input, English));

    [Theory]
    [InlineData("um uh hello", "hello")]
    [InlineData("hello um uh there", "hello there")]
    [InlineData("Ähm, um uh hello?", "Hello?")]
    [InlineData("um um um hello", "hello")]
    [InlineData("hello um um um", "hello")]
    [InlineData("  um uh hello", "  hello")]
    [InlineData(" um uh ", "")]
    public void Remove_StripsConsecutiveLatinFillerWords(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input, English));

    [Theory]
    [InlineData("Wait... um, no", "Wait... no")]
    [InlineData("Wait… um, no", "Wait… no")]
    [InlineData("Well!!! um okay", "Well!!! okay")]
    [InlineData("Done. um Next.", "Done. Next.")]
    public void Remove_KeepsPunctuationBelongingToSurroundingText(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input, English));

    [Fact]
    public void Remove_DropsLeadingWhitespace_WhenEverythingElseWasFiller() =>
        Assert.Equal(string.Empty, FillerWordFilter.Remove(" um ", English));

    [Fact]
    public void Remove_StripsLeadingWhitespaceIntroducedByRemoval() =>
        Assert.Equal("hello", FillerWordFilter.Remove("um hello", English));

    [Fact]
    public void Remove_StripsLineLeadingWhitespaceIntroducedByRemoval() =>
        Assert.Equal("hello\nthere", FillerWordFilter.Remove("hello\num there", English));

    [Fact]
    public void Remove_PreservesLineStructure() =>
        Assert.Equal("first line\nsecond line", FillerWordFilter.Remove("first um line\nsecond uh line", English));

    [Fact]
    public void Remove_StripsJapaneseFillerWordsWithTheirTrailingComma() =>
        Assert.Equal("これはテストです。", FillerWordFilter.Remove("えっと、これはテストです。", English));

    [Fact]
    public void Remove_StripsJapaneseFillerWordsMidSentence() =>
        Assert.Equal("それは、いいですね", FillerWordFilter.Remove("それは、なんかいいですね", English));

    [Theory]
    [InlineData("えっと、なんかこれはテストです。", "これはテストです。")]
    [InlineData("なんか えっと、これはテストです。", "これはテストです。")]
    [InlineData("えっと、そのー、これはテストです。", "これはテストです。")]
    [InlineData("それは、なんか えっと、いいですね", "それは、いいですね")]
    public void Remove_StripsConsecutiveJapaneseFillerWords(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input, English));

    [Fact]
    public void Remove_KeepsRepeatedMaaDrawl() =>
        Assert.Equal("まあまあいいです", FillerWordFilter.Remove("まあまあいいです", English));

    [Fact]
    public void Remove_HandlesMixedScripts() =>
        Assert.Equal("So これはテストです。", FillerWordFilter.Remove("So um えっと、これはテストです。", English));

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

        Assert.Equal("you actually", FillerWordFilter.Remove(Input, ["actually you", "um"], English));
        Assert.Equal(string.Empty, FillerWordFilter.Remove(Input, ["actually", "you um"], English));
    }

    [Fact]
    public void Remove_PrefersTheLongestMatchingFiller() =>
        Assert.Equal("well then", FillerWordFilter.Remove("well umm then", ["um", "umm"], English));

    [Fact]
    public void DefaultFillerWords_AreAllNormalized() =>
        Assert.Equal(
            FillerWordFilter.DefaultFillerWords.Count,
            FillerWordFilter.NormalizeWords(FillerWordFilter.DefaultFillerWords).Count);

    [Theory]
    [InlineData("um 10 Uhr", "de", "um 10 Uhr")]
    [InlineData("Wir treffen uns um 10 Uhr", "de", "Wir treffen uns um 10 Uhr")]
    [InlineData("Das ist eh egal", "de", "Das ist eh egal")]
    [InlineData("Eu vi um carro", "pt", "Eu vi um carro")]
    [InlineData("um, I think", "en", "I think")]
    [InlineData("um, I think", "en-US", "I think")]
    [InlineData("um, I think", "EN", "I think")]
    [InlineData("Ah, jetzt verstehe ich", "de-DE", "Jetzt verstehe ich")]
    public void Remove_RemovesLanguageBoundFillersOnlyInTheirLanguage(string input, string language, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input, language));

    [Theory]
    [InlineData("Das ist ähm nicht gut", "Das ist nicht gut")]
    [InlineData("So uh hmm yes", "So yes")]
    public void Remove_StripsUniversalFillersWithoutALanguage(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Theory]
    [InlineData("um 10 Uhr")]
    [InlineData("um ok")]
    [InlineData("Wir treffen uns um 10 Uhr im Büro, das ist gut")]
    [InlineData("eu vi um carro na rua ontem de manhã quando fui ao mercado")]
    [InlineData("I think um das ist gut")]
    public void Remove_KeepsLanguageBoundFillers_WhenTheTextDoesNotShowEnglish(string input) =>
        Assert.Equal(input, FillerWordFilter.Remove(input));

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData(null)]
    public void Remove_FallsBackToTheTextLanguage_WhenTheLanguageIsUnknown(string? language) =>
        Assert.Equal("So I think this is what we need", FillerWordFilter.Remove("So um I think this is what we need", language));

    [Fact]
    public void Remove_TrustsTheGivenLanguageOverTheText() =>
        Assert.Equal("So um I think this is what we need", FillerWordFilter.Remove("So um I think this is what we need", "de"));

    [Fact]
    public void Remove_KeepsCustomWordsThatAreNotLanguageBound() =>
        Assert.Equal("Das ist gut", FillerWordFilter.Remove("Das ist sozusagen gut", ["sozusagen", "um"], "de"));

    [Theory]
    [InlineData("Gut. Äh, ich komme", "Gut. Ich komme")]
    [InlineData("Äh, ich komme", "Ich komme")]
    [InlineData("Fertig! Ähm äh ich komme", "Fertig! Ich komme")]
    [InlineData("Gut?\nÄh, ich komme", "Gut?\nIch komme")]
    [InlineData("Gut. Äh, 10 Uhr passt", "Gut. 10 Uhr passt")]
    public void Remove_RestoresTheCapitalOfASentenceOpenedByAFiller(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input, "de"));

    [Theory]
    [InlineData("Gut, äh, ich komme", "Gut, ich komme")]
    [InlineData("Gut. äh ich komme", "Gut. ich komme")]
    [InlineData("Ich glaube, Äh, das passt", "Ich glaube, das passt")]
    public void Remove_LeavesCaseAlone_WhenTheFillerDidNotOpenACapitalizedSentence(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input, "de"));

    [Theory]
    [InlineData("So um I think this is what we need", "en")]
    [InlineData("Wir treffen uns um 10 Uhr im Büro, das ist gut", "de")]
    [InlineData("eu vi um carro na rua ontem de manhã quando fui ao mercado", "pt")]
    [InlineData("Je pense que c'est très bien pour nous", "fr")]
    public void TextLanguageDetector_RecognizesClearSentences(string input, string expected) =>
        Assert.Equal(expected, TextLanguageDetector.Detect(input));

    [Theory]
    [InlineData("um ok")]
    [InlineData("um 10 Uhr")]
    [InlineData("I think das ist gut")]
    [InlineData("")]
    public void TextLanguageDetector_ReturnsNothingInDoubt(string input) =>
        Assert.Null(TextLanguageDetector.Detect(input));

    [Theory]
    [InlineData("ich ich ich komme", "ich komme")]
    [InlineData("Ich ich ICH komme", "Ich komme")]
    [InlineData("I I I I think so", "I think so")]
    [InlineData("Also wh wh wh where", "Also wh where")]
    [InlineData("first line\nno no no\nlast", "first line\nno\nlast")]
    [InlineData("Gut, ich, ich, ich, ich komme.", "Gut, ich komme.")]
    [InlineData("Gut, ich, ich, ich, dann komme ich.", "Gut, ich, dann komme ich.")]
    [InlineData("ich ich, ich komme", "ich komme")]
    public void RepeatedWordCollapser_CollapsesThreeOrMoreRepetitions(string input, string expected) =>
        Assert.Equal(expected, RepeatedWordCollapser.Collapse(input));

    [Theory]
    [InlineData("very very good")]
    [InlineData("ich, ich komme")]
    [InlineData("Ich. Ich. Ich komme")]
    [InlineData("ich; ich; ich komme")]
    [InlineData("no no\nno")]
    [InlineData("the the theory")]
    [InlineData("1 1 1 2")]
    [InlineData("I I I'm here")]
    [InlineData("")]
    public void RepeatedWordCollapser_KeepsDeliberateRepetitions(string input) =>
        Assert.Equal(input, RepeatedWordCollapser.Collapse(input));
}
