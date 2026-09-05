using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

public sealed class TermPackPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-pack-tests-" + Guid.NewGuid());
    private string FilePath => Path.Combine(_directory, "dictionary.json");
    public TermPackPersistenceTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void PacksSurviveReloadAndCanBeDisabled()
    {
        var store = new PrototypeLexicon(FilePath);
        var pack = TermPack.AllPacks[0];
        Assert.Null(store.SetPackEnabled(pack, true));
        store = new(FilePath);
        Assert.True(store.PackEnabled(pack.Id));
        Assert.Equal(pack.Terms.Length, store.Entries.Count);
        Assert.Null(store.SetPackEnabled(pack, false));
        Assert.Empty(new PrototypeLexicon(FilePath).Entries);
    }

    [Fact]
    public void DisablingPackPreservesPersonalAndOtherPackTerms()
    {
        var store = new PrototypeLexicon(FilePath);
        Assert.Null(store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Word, "TypeWhisper")));
        var a = new TermPack("a", "A", "", ["TypeWhisper", "Shared"]);
        var b = new TermPack("b", "B", "", ["Shared"]);
        Assert.Null(store.SetPackEnabled(a, true));
        Assert.Null(store.SetPackEnabled(b, true));
        Assert.Null(store.SetPackEnabled(a, false));
        store = new(FilePath);
        Assert.Contains(store.Entries, e => e.Key == "TypeWhisper" && !e.FromPack);
        Assert.Contains(store.Entries, e => e.Key == "Shared" && e.FromPack);
    }

    [Fact]
    public void RepeatedActivationIsIdempotentAndPackTermsCannotBeEditedDirectly()
    {
        var store = new PrototypeLexicon(FilePath);
        var pack = new TermPack("test", "Test", "", ["TypeWhisper"]);
        store.SetPackEnabled(pack, true); store.SetPackEnabled(pack, true);
        var entry = Assert.Single(store.Entries);
        Assert.NotNull(store.Save(entry with { Key = "Changed" }));
        Assert.False(store.Remove(entry.Id));
    }

    [Fact]
    public void InvalidDictionaryIsNeverOverwritten()
    {
        File.WriteAllText(FilePath, "broken JSON");
        var store = new PrototypeLexicon(FilePath);
        Assert.NotNull(store.SetPackEnabled(TermPack.AllPacks[0], true));
        Assert.NotNull(store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Word, "TypeWhisper")));
        Assert.Equal("broken JSON", File.ReadAllText(FilePath));
    }

    [Fact]
    public void PersonalWordEditsAndDeletionArePersisted()
    {
        var store = new PrototypeLexicon(FilePath);
        Assert.Null(store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Word, "TypeWhisper")));
        store = new(FilePath);
        var word = Assert.Single(store.Entries);
        Assert.Null(store.Save(word with { Enabled = false }));
        store = new(FilePath);
        Assert.False(Assert.Single(store.Entries).Enabled);
        Assert.True(store.Remove(word.Id));
        Assert.Empty(new PrototypeLexicon(FilePath).Entries);
    }
}
