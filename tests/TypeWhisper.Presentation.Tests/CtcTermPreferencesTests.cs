using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class CtcTermPreferencesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(.5f)]
    [InlineData(.65f)]
    [InlineData(.8f)]
    [InlineData(.73f)]
    public void SavedTermRetainsThresholdInRecordingSnapshot(float? value)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ctc-terms-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "dictionary.json");
            var store = new PrototypeLexicon(path);
            Assert.Null(store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Word, "TypeWhisper", CtcMinSimilarity: value)));
            Assert.Equal(value, Assert.Single(new PrototypeLexicon(path).Entries).CtcMinSimilarity);
            Assert.Equal(value, Assert.Single(DictationDictionarySnapshot.Load(path).EnabledCtcEntries).CtcMinSimilarity);
        }
        finally { Directory.Delete(folder, true); }
    }
}
