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
    public void PersonalWordsAndPacksFeedExistingBoostingOnlyWhenEnabled()
    {
        var store = new PrototypeLexicon(FilePath);
        store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Word, "TypeWhisper"));
        store.SetPackEnabled(new("test", "Test", "", ["Parakeet"]), true);
        var snapshot = DictationDictionarySnapshot.Load(FilePath);
        Assert.Equal("type whisper and parrakeet", snapshot.Apply("type whisper and parrakeet"));
        Assert.Equal("TypeWhisper and Parakeet", snapshot.Apply("type whisper and parrakeet", true));
        Assert.Contains("TypeWhisper", snapshot.EnabledTerms);
    }

    [Fact]
    public void CorrectionsDoNotOverwriteEditsMadeAfterSnapshot()
    {
        var store = new PrototypeLexicon(FilePath);
        var entry = new PrototypeLexiconEntry(Guid.NewGuid(), PrototypeLexiconKind.Correction, "hello", "Grüße");
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
        var store = new PrototypeLexicon(FilePath);
        store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Word, "TypeWhisper", Enabled: false));
        store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Correction, "hello", "Grüße", Enabled: false));
        Assert.Equal("hello type whisper", DictationDictionarySnapshot.Load(FilePath).Apply("hello type whisper", true));
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
