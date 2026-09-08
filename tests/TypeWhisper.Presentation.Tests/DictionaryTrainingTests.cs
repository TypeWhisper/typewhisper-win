using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class DictionaryTrainingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "word-training-" + Guid.NewGuid());
    private string PathName => Path.Combine(_root, "dictionary.json");
    public DictionaryTrainingTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("CodeRabbit", true)]
    [InlineData("Größe", true)]
    [InlineData("O'Neill", true)]
    [InlineData("E-Mail", true)]
    [InlineData("", false)]
    [InlineData("two words", false)]
    [InlineData("word.", false)]
    [InlineData("word\nnext", false)]
    [InlineData("-word", false)]
    public void ValidatesSingleWord(string word, bool expected) =>
        Assert.Equal(expected, DictionaryTrainingPlan.IsWord(word));

    [Theory]
    [InlineData("Heute möchte ich CodeWebbit deutlich sagen.", "CodeWebbit")]
    [InlineData("Heute möchte ich CodeRabbit deutlich sagen.", null)]
    [InlineData("Heute will ich CodeWebbit deutlich sagen.", null)]
    [InlineData("Heute möchte ich Code Webbit deutlich sagen.", null)]
    [InlineData("", null)]
    [InlineData("heute möchte ich CodeWebbit deutlich sagen!", "CodeWebbit")]
    public void ExtractsOnlyUnambiguousTargetChange(string actual, string? expected) =>
        Assert.Equal(expected, DictionaryTrainingPlan.Candidate("CodeRabbit",
            DictionaryTrainingPlan.Sentences("CodeRabbit", true)[0], actual));

    [Fact]
    public void AddsWordAndReviewedUniqueVariantsAndSurvivesRestart()
    {
        var store = new Lexicon(PathName);
        Assert.Null(store.SaveTraining("CodeRabbit", ["CodeWebbit", "codewebbit", "CodeWebit"]));
        var reloaded = new Lexicon(PathName);
        Assert.Equal(3, reloaded.Entries.Count);
        Assert.Single(reloaded.Entries, entry => entry.Kind == LexiconKind.Word);
        Assert.Equal(2, reloaded.Entries.Count(entry => entry.Kind == LexiconKind.Correction && entry.Value == "CodeRabbit"));
        Assert.Null(reloaded.SaveTraining("CodeRabbit", ["CodeWebbit"]));
        Assert.Equal(3, new Lexicon(PathName).Entries.Count);
    }

    [Fact]
    public void ConflictRejectsEntireCommitWithoutAddingWordOrOtherVariants()
    {
        var store = new Lexicon(PathName);
        Assert.Null(store.Save(new(Guid.NewGuid(), LexiconKind.Correction, "CodeWebbit", "Existing") { Enabled = false }));
        var bytes = File.ReadAllBytes(PathName);
        Assert.NotNull(store.SaveTraining("CodeRabbit", ["NewVariant", "CodeWebbit"]));
        Assert.Equal(bytes, File.ReadAllBytes(PathName));
    }

    [Fact]
    public void ConcurrentNewConflictIsReadBeforeSaving()
    {
        var stale = new Lexicon(PathName);
        var other = new Lexicon(PathName);
        Assert.Null(other.Save(new(Guid.NewGuid(), LexiconKind.Correction, "CodeWebbit", "Existing")));
        Assert.NotNull(stale.SaveTraining("CodeRabbit", ["CodeWebbit"]));
        Assert.Single(new Lexicon(PathName).Entries);
    }

    [Fact]
    public void ExistingDisabledVariantIsNotReenabled()
    {
        var store = new Lexicon(PathName);
        Assert.Null(store.Save(new(Guid.NewGuid(), LexiconKind.Correction, "CodeWebbit", "CodeRabbit") { Enabled = false }));
        Assert.Null(store.SaveTraining("CodeRabbit", ["CodeWebbit"]));
        Assert.False(new Lexicon(PathName).Entries.Single(entry => entry.Kind == LexiconKind.Correction).Enabled);
    }

    [Fact]
    public void CorruptDictionaryIsNotOverwritten()
    {
        File.WriteAllText(PathName, "broken");
        Assert.NotNull(new Lexicon(PathName).SaveTraining("CodeRabbit", []));
        Assert.Equal("broken", File.ReadAllText(PathName));
    }

    [Fact]
    public void CorrectRecognitionCanSaveOnlyTheWord()
    {
        var store = new Lexicon(PathName);
        Assert.Null(store.SaveTraining("Größe", []));
        Assert.Equal(LexiconKind.Word, Assert.Single(new Lexicon(PathName).Entries).Kind);
    }
}
