using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

public sealed class DictationDictionarySnapshotTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-dictionary-snapshot-" + Guid.NewGuid());
    private string FilePath => Path.Combine(_directory, "dictionary.json");
    public DictationDictionarySnapshotTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void LiteralAndRegexTiesHaveTheSameOrderDuringDictation()
    {
        foreach (var regexFirst in new[] { false, true })
        {
            var path = Path.Combine(_directory, regexFirst ? "regex-first.json" : "literal-first.json");
            var store = new Lexicon(path);
            foreach (var isRegex in new[] { regexFirst, !regexFirst })
                store.Save(new(Guid.NewGuid(), LexiconKind.Correction, "foo", isRegex ? "B" : "A", IsRegex: isRegex));
            var snapshot = DictationDictionarySnapshot.Load(path);
            Assert.Null(snapshot.Error);
            Assert.Equal("A", snapshot.ApplyCorrections("foo"));
        }
    }

    [Fact]
    public void PersonalWordsAndPacksFeedExistingBoostingOnlyWhenEnabled()
    {
        var store = new Lexicon(FilePath);
        store.Save(new(Guid.NewGuid(), LexiconKind.Word, "TypeWhisper"));
        store.SetPackEnabled(new("test", "Test", "", ["Parakeet"]), true);
        var snapshot = DictationDictionarySnapshot.Load(FilePath);
        Assert.Equal("type whisper and parrakeet", snapshot.Apply("type whisper and parrakeet"));
        Assert.Equal("TypeWhisper and Parakeet", snapshot.Apply("type whisper and parrakeet", true));
        Assert.Contains("TypeWhisper", snapshot.EnabledTerms);
    }

    [Fact]
    public void CorrectionsDoNotOverwriteEditsMadeAfterSnapshot()
    {
        var store = new Lexicon(FilePath);
        var entry = new LexiconEntry(Guid.NewGuid(), LexiconKind.Correction, "hello", "Grüße");
        store.Save(entry);
        var snapshot = DictationDictionarySnapshot.Load(FilePath);
        var saved = store.Entries.Single();
        store.Save(saved with { Value = "Guten Tag" });
        var updatedBytes = File.ReadAllBytes(FilePath);
        Assert.Equal("Grüße world", snapshot.Apply("hello world"));
        Assert.Equal(updatedBytes, File.ReadAllBytes(FilePath));
        Assert.Equal("Guten Tag world", DictationDictionarySnapshot.Load(FilePath).Apply("hello world"));
    }

    [Fact]
    public void DisabledTermsAndCorrectionsDoNotApply()
    {
        var store = new Lexicon(FilePath);
        store.Save(new(Guid.NewGuid(), LexiconKind.Word, "TypeWhisper", Enabled: false));
        store.Save(new(Guid.NewGuid(), LexiconKind.Correction, "hello", "Grüße", Enabled: false));
        Assert.Equal("hello type whisper", DictationDictionarySnapshot.Load(FilePath).Apply("hello type whisper", true));
    }

    [Fact]
    public void WhitespaceRegexKeepsDictionaryAvailableAndAppliesDuringDictation()
    {
        var store = new Lexicon(FilePath);
        store.Save(new(Guid.NewGuid(), LexiconKind.Word, "TypeWhisper"));
        store.Save(new(Guid.NewGuid(), LexiconKind.Correction, "teh", "the"));
        store.Save(new(Guid.NewGuid(), LexiconKind.Correction, " ", "-", IsRegex: true));
        var snapshot = DictationDictionarySnapshot.Load(FilePath);
        Assert.Null(snapshot.Error);
        Assert.Contains("TypeWhisper", snapshot.EnabledTerms);
        Assert.Equal("the-tool", snapshot.ApplyCorrections("teh tool"));
    }

    [Theory]
    [InlineData(false, " ")]
    [InlineData(true, "")]
    public void EmptyLiteralOrRegexPatternKeepsDictionaryUnavailable(bool isRegex, string original)
    {
        File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(new[] {
            new DictionaryEntry { Id = Guid.NewGuid().ToString(), EntryType = DictionaryEntryType.Correction,
                Original = original, Replacement = "-", IsRegex = isRegex }
        }));
        Assert.NotNull(DictationDictionarySnapshot.Load(FilePath).Error);
    }

    [Fact]
    public void MalformedDictionaryKeepsOriginalTextAndFile()
    {
        File.WriteAllText(FilePath, "{broken");
        var snapshot = DictationDictionarySnapshot.Load(FilePath);
        Assert.NotNull(snapshot.Error);
        Assert.Equal("hello", snapshot.Apply("hello", true));
        Assert.Equal("{broken", File.ReadAllText(FilePath));
    }
}
