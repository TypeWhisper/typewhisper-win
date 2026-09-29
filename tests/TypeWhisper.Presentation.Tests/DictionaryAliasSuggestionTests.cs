using System.Text.Json;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class DictionaryAliasSuggestionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "alias-suggestions-" + Guid.NewGuid());
    private string DictionaryPath => Path.Combine(_root, "dictionary.json");
    public DictionaryAliasSuggestionTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task GeneratesFromStructuredTermAndLanguageWithoutSaving()
    {
        var aliases = await DictionaryAliasSuggestions.GenerateAsync(" TypeWhisper ", "German", (prompt, input, _) =>
        {
            using var data = JsonDocument.Parse(input);
            Assert.Equal("TypeWhisper", data.RootElement.GetProperty("word").GetString());
            Assert.Equal("German", data.RootElement.GetProperty("language").GetString());
            Assert.Contains("speech-recognition", prompt);
            return Task.FromResult("[\"Type Whisper\",\"Teip Wisper\"]");
        }, CancellationToken.None);
        Assert.Equal(new[] { "Type Whisper", "Teip Wisper" }, aliases);
        Assert.False(File.Exists(DictionaryPath));
    }

    [Fact]
    public void FiltersDuplicatesCorrectSpellingControlCharactersAndNonPhrases()
    {
        var response = JsonSerializer.Serialize(new[] { "TypeWhisper", "typewhisper", " Type  Whisper ", "type whisper",
            "Teip Wisper", "", "bad\ntrigger", "variant → TypeWhisper", "<script>", new string('x', 161) });
        Assert.Equal(new[] { "Type Whisper", "Teip Wisper" }, DictionaryAliasSuggestions.Parse("TypeWhisper", response));
    }

    [Theory]
    [InlineData("```json\n[\"groesse\", \"Grösse\"]\n```")]
    [InlineData("[\"groesse\", \"Grösse\", \"Größe\"]")]
    public void AcceptsUnicodeAndFencedJson(string response) =>
        Assert.Equal(new[] { "groesse", "Grösse" }, DictionaryAliasSuggestions.Parse("Größe", response));

    [Theory]
    [InlineData("Here are your suggestions: [\"wrong\"]")]
    [InlineData("{\"aliases\":[\"wrong\"]}")]
    [InlineData("[\"wrong\", null]")]
    [InlineData("[\"wrong\", 1]")]
    [InlineData("[\"wrong\"")]
    public void RejectsUnstructuredOrTruncatedOutput(string response) =>
        Assert.Throws<FormatException>(() => DictionaryAliasSuggestions.Parse("word", response));

    [Fact]
    public void CapsCandidatesAndRejectsOversizedResponses()
    {
        Assert.Equal(DictionaryAliasSuggestions.MaximumSuggestions,
            DictionaryAliasSuggestions.Parse("word", JsonSerializer.Serialize(Enumerable.Range(0, 20).Select(i => "variant " + i))).Count);
        Assert.Throws<FormatException>(() => DictionaryAliasSuggestions.Parse("word", new string(' ', 8193)));
        Assert.Empty(DictionaryAliasSuggestions.Parse("word", "[]"));
    }

    [Fact]
    public async Task CancellationDiscardsLateProviderOutput()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DictionaryAliasSuggestions.GenerateAsync("TypeWhisper", "German",
            (_, _, _) => { cancellation.Cancel(); return Task.FromResult("[\"Type Whisper\"]"); }, cancellation.Token));
    }

    [Fact]
    public async Task InvalidTermAndPriorCancellationNeverInvokeProvider()
    {
        Task<string> Unexpected(string prompt, string input, CancellationToken ct) => throw new Xunit.Sdk.XunitException("Provider was invoked.");
        await Assert.ThrowsAsync<ArgumentException>(() => DictionaryAliasSuggestions.GenerateAsync("bad\nword", "German", Unexpected, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => DictionaryAliasSuggestions.GenerateAsync("bad\u2028word", "German", Unexpected, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DictionaryAliasSuggestions.GenerateAsync("word", "German", Unexpected, new(true)));
    }

    [Fact]
    public void SavesOnlySelectedAliasesAndAppliesThemInCorrectDirectionAfterReload()
    {
        var store = new Lexicon(DictionaryPath);
        Assert.Null(store.SaveSuggestedAliases("TypeWhisper", ["Type Whisper", "type whisper"]));
        Assert.Equal(2, new Lexicon(DictionaryPath).Entries.Count);
        var dictionary = new DictionaryService(DictionaryPath);
        Assert.Equal("I use TypeWhisper daily.", dictionary.ApplyCorrections("I use Type Whisper daily."));
        Assert.Equal("Teip Wisper", dictionary.ApplyCorrections("Teip Wisper"));
    }

    [Fact]
    public void AllowsMultiwordTargetsAndKeepsExistingWordSettings()
    {
        var store = new Lexicon(DictionaryPath);
        Assert.Null(store.Save(new(Guid.NewGuid(), LexiconKind.Word, "Visual Studio") { Enabled = false, CtcMinSimilarity = .8f }));
        Assert.Null(store.SaveSuggestedAliases("Visual Studio", ["visuell studio"]));
        var word = new Lexicon(DictionaryPath).Entries.Single(e => e.Kind == LexiconKind.Word);
        Assert.False(word.Enabled);
        Assert.Equal(.8f, word.CtcMinSimilarity);
    }

    [Fact]
    public void ConcurrentConflictRejectsEntireBatchAndPreservesExistingData()
    {
        var stale = new Lexicon(DictionaryPath);
        var other = new Lexicon(DictionaryPath);
        Assert.Null(other.Save(new(Guid.NewGuid(), LexiconKind.Correction, "Type Whisper", "Another term") { Enabled = false }));
        var before = File.ReadAllBytes(DictionaryPath);
        Assert.NotNull(stale.SaveSuggestedAliases("TypeWhisper", ["Teip Wisper", "Type Whisper"]));
        Assert.Equal(before, File.ReadAllBytes(DictionaryPath));
    }

    [Fact]
    public void DoesNotReenableExistingAliasOrDuplicateIt()
    {
        var store = new Lexicon(DictionaryPath);
        Assert.Null(store.Save(new(Guid.NewGuid(), LexiconKind.Correction, "Type Whisper", "TypeWhisper") { Enabled = false }));
        Assert.Null(store.SaveSuggestedAliases("TypeWhisper", ["type whisper", "Teip Wisper"]));
        Assert.False(new Lexicon(DictionaryPath).Entries.Single(e => e.Key == "Type Whisper").Enabled);
        Assert.Equal(3, new Lexicon(DictionaryPath).Entries.Count);
    }

    [Fact]
    public void InvalidSelectionAndCorruptStorageNeverWrite()
    {
        var store = new Lexicon(DictionaryPath);
        Assert.NotNull(store.SaveSuggestedAliases("TypeWhisper", []));
        Assert.NotNull(store.SaveSuggestedAliases("TypeWhisper", ["TypeWhisper"]));
        Assert.NotNull(store.SaveSuggestedAliases("TypeWhisper", ["Teip Wisper", "bad\ntrigger"]));
        Assert.False(File.Exists(DictionaryPath));
        File.WriteAllText(DictionaryPath, "broken");
        Assert.NotNull(store.SaveSuggestedAliases("TypeWhisper", ["Type Whisper"]));
        Assert.Equal("broken", File.ReadAllText(DictionaryPath));
    }
}
