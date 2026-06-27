using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class DictionaryServiceTests : IDisposable
{
    private readonly string _filePath;
    private readonly DictionaryService _sut;

    public DictionaryServiceTests()
    {
        _filePath = Path.GetTempFileName();
        _sut = new DictionaryService(_filePath);
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
    public void ApplyCorrections_ReplacesChinesePhraseWithoutWhitespace()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Correction,
            Original = "北京",
            Replacement = "上海"
        });

        var result = _sut.ApplyCorrections("我想去北京吃饭");

        Assert.Equal("我想去上海吃饭", result);
    }

    [Fact]
    public void ApplyCorrections_ReplacesCjkCompatibilityIdeographWithoutWhitespace()
    {
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Correction,
            Original = "\uF900",
            Replacement = "compat"
        });

        var result = _sut.ApplyCorrections("before\uF900after");

        Assert.Equal("beforecompatafter", result);
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
    public void SetTerms_AppendsNormalizedTerms()
    {
        _sut.SetTerms([" TypeWhisper ", "WhisperKit", "typewhisper"], replaceExisting: false);
        _sut.SetTerms(["Kubernetes"], replaceExisting: false);

        Assert.Equal(["TypeWhisper", "WhisperKit", "Kubernetes"], _sut.GetEnabledTerms());
    }

    [Fact]
    public void SetTerms_ReplacesExistingTerms()
    {
        _sut.SetTerms(["TypeWhisper", "WhisperKit"], replaceExisting: false);
        _sut.SetTerms(["Kubernetes"], replaceExisting: true);

        Assert.Equal(["Kubernetes"], _sut.GetEnabledTerms());
    }

    [Fact]
    public void RemoveAllTerms_LeavesCorrections()
    {
        _sut.SetTerms(["TypeWhisper"], replaceExisting: false);
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "correction",
            EntryType = DictionaryEntryType.Correction,
            Original = "typo",
            Replacement = "term"
        });

        _sut.RemoveAllTerms();

        Assert.Empty(_sut.GetEnabledTerms());
        Assert.Single(_sut.Entries);
        Assert.Equal(DictionaryEntryType.Correction, _sut.Entries[0].EntryType);
    }

    [Fact]
    public void DeleteTerm_RemovesSingleTermCaseInsensitively()
    {
        _sut.SetTerms(["TypeWhisper", "WhisperKit", "Raycast"], replaceExisting: true);

        var deleted = _sut.DeleteTerm("typewhisper");

        Assert.True(deleted);
        Assert.Equal(["WhisperKit", "Raycast"], _sut.GetEnabledTerms());
    }

    [Fact]
    public void UpsertCorrection_UpdatesExistingCaseInsensitivelyAndEnablesIt()
    {
        _sut.UpsertCorrection("teh", "the", caseSensitive: false);
        _sut.UpsertCorrection("TEH", "The", caseSensitive: true);

        var correction = Assert.Single(_sut.GetEnabledCorrections());
        Assert.Equal("TEH", correction.Original);
        Assert.Equal("The", correction.Replacement);
        Assert.True(correction.CaseSensitive);
        Assert.True(correction.IsEnabled);
    }

    [Fact]
    public void DeleteCorrection_RemovesSingleCorrectionCaseInsensitively()
    {
        _sut.UpsertCorrection("teh", "the", caseSensitive: false);
        _sut.UpsertCorrection("um", "", caseSensitive: false);

        var deleted = _sut.DeleteCorrection("TEH");

        Assert.True(deleted);
        var remaining = Assert.Single(_sut.GetEnabledCorrections());
        Assert.Equal("um", remaining.Original);
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
    public void LearnCorrections_AddsOnlyNewCorrectionsAndReturnsLearned()
    {
        var learned = _sut.LearnCorrections([
            new CorrectionSuggestion("teh", "the"),
            new CorrectionSuggestion("recieve", "receive")
        ]);

        Assert.Equal(2, learned.Count);
        Assert.Equal(["teh", "recieve"], learned.Select(c => c.Original).ToArray());
        Assert.Equal(["the", "receive"], learned.Select(c => c.Replacement).ToArray());
        Assert.Equal(2, _sut.Entries.Count);
        Assert.All(_sut.Entries, entry => Assert.Equal(DictionaryEntryType.Correction, entry.EntryType));
    }

    [Fact]
    public void LearnCorrections_AllowsInternalHyphenatedWords()
    {
        var learned = _sut.LearnCorrections([
            new CorrectionSuggestion("Premium-Funktionalität", "Premiun-Funktionalität")
        ]);

        var correction = Assert.Single(learned);
        Assert.Equal("Premium-Funktionalität", correction.Original);
        Assert.Equal("Premiun-Funktionalität", correction.Replacement);
        var entry = Assert.Single(_sut.Entries);
        Assert.Equal("Premium-Funktionalität", entry.Original);
        Assert.Equal("Premiun-Funktionalität", entry.Replacement);
    }

    [Fact]
    public void LearnCorrections_SkipsExistingAndDoesNotOverwrite()
    {
        _sut.LearnCorrection("teh", "THE");

        var learned = _sut.LearnCorrections([
            new CorrectionSuggestion("TEH", "the"),
            new CorrectionSuggestion("kubernets", "Kubernetes")
        ]);

        Assert.Single(learned);
        Assert.Equal("kubernets", learned[0].Original);
        Assert.Equal(2, _sut.Entries.Count);
        Assert.Equal("THE", _sut.Entries.First(e => e.Original == "teh").Replacement);
    }

    [Fact]
    public void LearnCorrections_SkipsDuplicateSuggestions()
    {
        var learned = _sut.LearnCorrections([
            new CorrectionSuggestion("teh", "the"),
            new CorrectionSuggestion("TEH", "the")
        ]);

        Assert.Single(learned);
        Assert.Single(_sut.Entries);
    }

    [Fact]
    public void LearnCorrections_SkipsUnsafeReplacementExpansion()
    {
        var learned = _sut.LearnCorrections([
            new CorrectionSuggestion("Premium", "Premium.\rhjk"),
            new CorrectionSuggestion("teh", "the")
        ]);

        var correction = Assert.Single(learned);
        Assert.Equal("teh", correction.Original);
        Assert.Equal("the", correction.Replacement);
        var entry = Assert.Single(_sut.Entries);
        Assert.Equal("teh", entry.Original);
    }

    [Fact]
    public void LearnCorrections_ReturnsLearnedWhenEntriesChangedSubscriberThrows()
    {
        _sut.EntriesChanged += () => throw new InvalidOperationException("UI thread mismatch");

        var learned = _sut.LearnCorrections([
            new CorrectionSuggestion("teh", "the")
        ]);

        var correction = Assert.Single(learned);
        Assert.Equal("teh", correction.Original);
        Assert.Equal("the", correction.Replacement);
    }

    [Fact]
    public void UndoLearnedCorrections_RemovesOnlyLearnedEntries()
    {
        _sut.LearnCorrection("existing", "Existing");
        var learned = _sut.LearnCorrections([
            new CorrectionSuggestion("teh", "the"),
            new CorrectionSuggestion("recieve", "receive")
        ]);

        _sut.UndoLearnedCorrections([learned[0], learned[1] with { Replacement = "different" }]);

        var remaining = Assert.Single(_sut.Entries);
        Assert.Equal("existing", remaining.Original);
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
    public void UpdateEntry_RefreshesUpdatedAt()
    {
        var originalUpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Term,
            Original = "React",
            UpdatedAt = originalUpdatedAt
        });

        _sut.UpdateEntry(_sut.Entries[0] with { Original = "React.js" });

        Assert.True(_sut.Entries[0].UpdatedAt > originalUpdatedAt);
    }

    [Fact]
    public void UpdateEntry_KeepsUpdatedAtMonotonic()
    {
        var futureUpdatedAt = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Term,
            Original = "React",
            UpdatedAt = futureUpdatedAt
        });

        _sut.UpdateEntry(_sut.Entries[0] with { Original = "React.js" });

        Assert.Equal(futureUpdatedAt.AddTicks(1), _sut.Entries[0].UpdatedAt);
    }

    [Fact]
    public void LegacyEntriesMissingUpdatedAtUseCreatedAt()
    {
        var createdAt = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.WriteAllText(_filePath, $$"""
        [
          {
            "Id": "legacy",
            "EntryType": 0,
            "Original": "React",
            "CreatedAt": "{{createdAt:O}}"
          }
        ]
        """);
        var sut = new DictionaryService(_filePath);

        var entry = Assert.Single(sut.Entries);

        Assert.Equal(createdAt, entry.UpdatedAt);
    }

    [Fact]
    public void ApplyCorrections_IncrementsUsageWithoutChangingUpdatedAt()
    {
        var updatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _sut.AddEntry(new DictionaryEntry
        {
            Id = "1",
            EntryType = DictionaryEntryType.Correction,
            Original = "teh",
            Replacement = "the",
            UpdatedAt = updatedAt
        });

        _sut.ApplyCorrections("teh");

        Assert.Equal(1, _sut.Entries[0].UsageCount);
        Assert.Equal(updatedAt, _sut.Entries[0].UpdatedAt);
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
        if (File.Exists(_filePath)) File.Delete(_filePath);
    }
}
