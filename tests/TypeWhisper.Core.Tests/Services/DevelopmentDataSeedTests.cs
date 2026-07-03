using TypeWhisper.Core;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class DevelopmentDataSeedTests : IDisposable
{
    private readonly string _tempDir;

    public DevelopmentDataSeedTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tw_dev_seed_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void CreateDefault_ReturnsSafeDeterministicExamples()
    {
        var referenceUtc = new DateTime(2026, 1, 15, 8, 30, 0, DateTimeKind.Utc);
        var seed = DevelopmentDataSeedFactory.CreateDefault(referenceUtc);
        var repeatedSeed = DevelopmentDataSeedFactory.CreateDefault(referenceUtc);

        Assert.True(seed.Settings.HasCompletedOnboarding);
        Assert.True(seed.Settings.VocabularyBoostingEnabled);
        Assert.Null(seed.Settings.GroqApiKey);
        Assert.Null(seed.Settings.OpenAiApiKey);
        Assert.Null(seed.Settings.CloudFolderSyncFolderPath);
        Assert.Null(seed.Settings.CloudFolderSyncState);
        Assert.NotEmpty(seed.DictionaryEntries);
        Assert.NotEmpty(seed.Snippets);
        Assert.NotEmpty(seed.Workflows);
        Assert.NotEmpty(seed.HistoryRecords);
        Assert.Contains(seed.DictionaryEntries, entry =>
            entry.EntryType == DictionaryEntryType.Term &&
            entry.Original == "TypeWhisper");
        Assert.Contains(seed.DictionaryEntries, entry =>
            entry.EntryType == DictionaryEntryType.Correction &&
            entry.Original == "type whisper" &&
            entry.Replacement == "TypeWhisper");
        Assert.Equal(14, seed.HistoryRecords.Count);
        Assert.Equal(
            Enumerable.Range(1, 14).Select(index => $"dev-history-{index:000}"),
            seed.HistoryRecords.Select(record => record.Id));
        Assert.True(seed.HistoryRecords.Select(record => record.Timestamp.Date).Distinct().Count() >= 10);
        Assert.Contains(seed.HistoryRecords, record => record.AppProcessName == "outlook");
        Assert.Contains(seed.HistoryRecords, record => record.AppProcessName == "obsidian");
        Assert.Equal(
            seed.HistoryRecords.Select(record => record.Timestamp),
            repeatedSeed.HistoryRecords.Select(record => record.Timestamp));
        Assert.Equal(new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc), seed.HistoryRecords[0].Timestamp);
    }

    [Fact]
    public void ClearAndSeed_ReplacesCoreDataAndPreservesOnlySafePreferences()
    {
        Assert.True(TypeWhisperEnvironment.IsDevelopmentBuild);

        var settings = new SettingsService(Path.Combine(_tempDir, "settings.json"));
        var dictionary = new DictionaryService(Path.Combine(_tempDir, "Data", "dictionary.json"));
        var snippets = new SnippetService(Path.Combine(_tempDir, "Data", "snippets.json"));
        var workflows = new WorkflowService(Path.Combine(_tempDir, "Data", "workflows.json"));
        var history = new HistoryService(Path.Combine(_tempDir, "Data", "history.json"));
        var sut = new DevelopmentDataSeeder(settings, history, dictionary, snippets, workflows);

        settings.Save(AppSettings.Default with
        {
            UiLanguage = "de",
            UpdateChannel = "daily",
            GroqApiKey = "secret-groq",
            OpenAiApiKey = "secret-openai",
            SelectedModelId = "legacy-model",
            CloudFolderSyncFolderPath = @"C:\Users\marco\Secrets\TypeWhisper",
            PluginEnabledState = new Dictionary<string, bool> { ["com.example.secret"] = false }
        });
        dictionary.AddEntry(new DictionaryEntry
        {
            Id = "old-dictionary",
            EntryType = DictionaryEntryType.Term,
            Original = "old"
        });
        snippets.AddSnippet(new Snippet
        {
            Id = "old-snippet",
            Trigger = "old",
            Replacement = "old replacement"
        });
        workflows.AddWorkflow(new Workflow
        {
            Id = "old-workflow",
            Name = "Old workflow",
            Template = WorkflowTemplate.Summary,
            Trigger = WorkflowTrigger.Manual()
        });
        history.AddRecord(new TranscriptionRecord
        {
            Id = "old-history",
            Timestamp = DateTime.UtcNow,
            RawText = "old",
            FinalText = "old"
        });

        var result = sut.ClearAndSeed();

        Assert.Equal(DevelopmentDataSeedResult.Seeded, result);
        Assert.Equal("de", settings.Current.UiLanguage);
        Assert.Equal("daily", settings.Current.UpdateChannel);
        Assert.True(settings.Current.HasCompletedOnboarding);
        Assert.Null(settings.Current.GroqApiKey);
        Assert.Null(settings.Current.OpenAiApiKey);
        Assert.Null(settings.Current.SelectedModelId);
        Assert.Null(settings.Current.CloudFolderSyncFolderPath);
        Assert.Empty(settings.Current.PluginEnabledState);
        Assert.DoesNotContain(dictionary.Entries, entry => entry.Id == "old-dictionary");
        Assert.DoesNotContain(snippets.Snippets, snippet => snippet.Id == "old-snippet");
        Assert.DoesNotContain(workflows.Workflows, workflow => workflow.Id == "old-workflow");
        Assert.DoesNotContain(history.Records, record => record.Id == "old-history");
        Assert.Contains(dictionary.Entries, entry => entry.Id == "dev-term-typewhisper");
        Assert.Contains(snippets.Snippets, snippet => snippet.Id == "dev-snippet-standup");
        Assert.Contains(workflows.Workflows, workflow => workflow.Id == "dev-workflow-meeting-notes");
        Assert.Contains(history.Records, record => record.Id == "dev-history-001");
    }
}
