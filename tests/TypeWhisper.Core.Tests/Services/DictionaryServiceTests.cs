using TypeWhisper.Core.Data;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class DictionaryServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TypeWhisperDatabase _db;
    private readonly DictionaryService _sut;

    public DictionaryServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tw_test_{Guid.NewGuid():N}.db");
        _db = new TypeWhisperDatabase(_dbPath);
        _db.Initialize();
        _sut = new DictionaryService(_db);
    }

    [Fact]
    public void AddEntry_AppearsInEntries()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Term,
            Original = "React"
        });

        Assert.Single(_sut.Entries);
        Assert.Equal("React", _sut.Entries[0].Original);
    }

    [Fact]
    public void DeleteEntry_RemovesFromEntries()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Term,
            Original = "React"
        });

        _sut.DeleteEntry("1");

        Assert.Empty(_sut.Entries);
    }

    [Fact]
    public void DeleteEntries_BatchRemove()
    {
        _sut.AddEntry(new DictionaryEntry { Id = "1", EntryType = DictionaryEntryType.Term, Original = "A" });
        _sut.AddEntry(new DictionaryEntry { Id = "2", EntryType = DictionaryEntryType.Term, Original = "B" });
        _sut.AddEntry(new DictionaryEntry { Id = "3", EntryType = DictionaryEntryType.Term, Original = "C" });

        _sut.DeleteEntries(["1", "3"]);

        Assert.Single(_sut.Entries);
        Assert.Equal("B", _sut.Entries[0].Original);
    }

    [Fact]
    public void ActivatePack_InsertsTerms()
    {
        var pack = new TermPack("test", "Test Pack", "T", ["React", "Vue", "Angular"]);

        _sut.ActivatePack(pack);

        Assert.Equal(3, _sut.Entries.Count);
        Assert.All(_sut.Entries, e => Assert.Equal(DictionaryEntryType.Term, e.EntryType));
        Assert.All(_sut.Entries, e => Assert.StartsWith("pack:test:", e.Id));
    }

    [Fact]
    public void ActivatePack_SkipsDuplicates()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "existing",
            EntryType = DictionaryEntryType.Term,
            Original = "React"
        });

        var pack = new TermPack("test", "Test Pack", "T", ["React", "Vue"]);
        _sut.ActivatePack(pack);

        // Should have 2 entries: the existing "React" and the new "Vue"
        Assert.Equal(2, _sut.Entries.Count);
    }

    [Fact]
    public void DeactivatePack_RemovesPackTerms()
    {
        var pack = new TermPack("test", "Test Pack", "T", ["React", "Vue"]);
        _sut.ActivatePack(pack);

        // Add a manual entry that shouldn't be removed
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "manual",
            EntryType = DictionaryEntryType.Term,
            Original = "TypeScript"
        });

        _sut.DeactivatePack("test");

        Assert.Single(_sut.Entries);
        Assert.Equal("TypeScript", _sut.Entries[0].Original);
    }

    [Fact]
    public void ApplyCorrections_ReplacesText()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Correction,
            Original = "kubernets",
            Replacement = "Kubernetes"
        });

        var result = _sut.ApplyCorrections("I deployed to kubernets");
        Assert.Equal("I deployed to Kubernetes", result);
    }

    [Fact]
    public void GetTermsForPrompt_ReturnsCommaSeparated()
    {
        _sut.AddEntry(new DictionaryEntry { Id = "1", EntryType = DictionaryEntryType.Term, Original = "React" });
        _sut.AddEntry(new DictionaryEntry { Id = "2", EntryType = DictionaryEntryType.Term, Original = "Vue" });

        var result = _sut.GetTermsForPrompt();
        Assert.Equal("React, Vue", result);
    }

    [Fact]
    public void GetTermsForPrompt_ReturnsNull_WhenNoTerms()
    {
        Assert.Null(_sut.GetTermsForPrompt());
    }

    [Fact]
    public void LearnCorrection_AddsNewCorrection()
    {
        _sut.LearnCorrection("kubernets", "Kubernetes");

        Assert.Single(_sut.Entries);
        Assert.Equal(DictionaryEntryType.Correction, _sut.Entries[0].EntryType);
        Assert.Equal("kubernets", _sut.Entries[0].Original);
        Assert.Equal("Kubernetes", _sut.Entries[0].Replacement);
    }

    [Fact]
    public void LearnCorrection_UpdatesExisting()
    {
        _sut.LearnCorrection("kubernets", "Kubernets");
        _sut.LearnCorrection("kubernets", "Kubernetes");

        Assert.Single(_sut.Entries);
        Assert.Equal("Kubernetes", _sut.Entries[0].Replacement);
        Assert.Equal(1, _sut.Entries[0].UsageCount);
    }

    [Fact]
    public void UpdateEntry_ModifiesEntry()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Term,
            Original = "React"
        });

        _sut.UpdateEntry(_sut.Entries[0] with { Original = "React.js", CaseSensitive = true });

        Assert.Equal("React.js", _sut.Entries[0].Original);
        Assert.True(_sut.Entries[0].CaseSensitive);
    }

    [Fact]
    public void EntriesChanged_FiresOnModification()
    {
        var fired = 0;
        _sut.EntriesChanged += () => fired++;

        _sut.AddEntry(new DictionaryEntry { Id = "1", EntryType = DictionaryEntryType.Term, Original = "React" });
        _sut.DeleteEntry("1");

        Assert.Equal(2, fired);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
