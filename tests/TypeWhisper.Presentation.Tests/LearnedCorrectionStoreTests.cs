using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LearnedCorrectionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "learned-correction-" + Guid.NewGuid().ToString("N"));
    private string Store => Path.Combine(_root, "dictionary.json");
    public LearnedCorrectionStoreTests() => Directory.CreateDirectory(_root);
    [Fact]
    public void LearnedCorrectionIsAppliedOnNextSnapshotAndCanBeDeleted()
    {
        var learned = LearnedCorrectionStore.Save(Store, [new("teh", "the")]);
        Assert.Single(learned);
        Assert.Equal("We use the tool.", DictationDictionarySnapshot.Load(Store).ApplyCorrections("We use teh tool."));
        var dictionary = new DictionaryService(Store);
        Assert.Equal(DictionaryEntrySource.AutoLearned, Assert.Single(dictionary.Entries).Source);
        dictionary.DeleteEntry(learned[0].Id);
        Assert.Equal("We use teh tool.", DictationDictionarySnapshot.Load(Store).ApplyCorrections("We use teh tool."));
    }
    [Fact]
    public void ExistingDisabledAndManualCorrectionsAreNotReplaced()
    {
        new DictionaryService(Store).AddEntry(new() { Id = "manual", EntryType = DictionaryEntryType.Correction, Original = "teh", Replacement = "manual", IsEnabled = false });
        Assert.Empty(LearnedCorrectionStore.Save(Store, [new("TEH", "the")]));
        var entry = Assert.Single(new DictionaryService(Store).Entries);
        Assert.Equal("manual", entry.Replacement); Assert.False(entry.IsEnabled);
    }
    [Fact]
    public void CorruptDictionaryIsPreserved()
    {
        File.WriteAllText(Store, "broken");
        Assert.ThrowsAny<Exception>(() => LearnedCorrectionStore.Save(Store, [new("teh", "the")]));
        Assert.Equal("broken", File.ReadAllText(Store));
    }
    [Fact]
    public void FailedSaveDoesNotReportLearnedCorrection()
    {
        Directory.CreateDirectory(Store);
        Assert.Throws<IOException>(() => LearnedCorrectionStore.Save(Store, [new("teh", "the")]));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
