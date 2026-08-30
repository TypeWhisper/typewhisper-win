using System.Text.Json.Nodes;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Models.Backup;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class BackupRestoreServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"typewhisper-backup-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Export_UsesAllowlistAndNeverIncludesSecretsPathsOrAudio()
    {
        var services = CreateProfile("source");
        services.Settings.Save(services.Settings.Current with
        {
            GroqApiKey = "secret-groq-value",
            OpenAiApiKey = "secret-openai-value",
            SelectedModelId = "private-model-id",
            LocalModelStoragePath = @"X:\private-models",
            WatchFolderPath = @"X:\private-watch",
            WatchFolderOutputPath = @"X:\private-output",
            CloudFolderSyncFolderPath = @"X:\private-cloud",
            RecorderSystemAudioDeviceId = "private-device",
            ApiServerEnabled = true,
            ApiServerPort = 12345,
            PluginEnabledState = new Dictionary<string, bool> { ["secret-plugin-state"] = true },
            HasCompletedOnboarding = true,
            Language = "de",
            LanguageHints = ["de"],
            AutoPaste = false
        });
        services.History.AddRecord(new TranscriptionRecord
        {
            Id = "history-1",
            Timestamp = DateTime.UtcNow,
            RawText = "portable raw text",
            FinalText = "portable final text",
            AudioFileName = "private-audio.wav",
            RecoveryAudioFileName = "private-recovery.wav"
        });

        var json = await services.Backup.ExportAsync();

        Assert.Contains("portable final text", json);
        Assert.Contains("\"language\": \"de\"", json);
        Assert.DoesNotContain("secret-groq-value", json);
        Assert.DoesNotContain("secret-openai-value", json);
        Assert.DoesNotContain("private-model-id", json);
        Assert.DoesNotContain("private-models", json);
        Assert.DoesNotContain("private-watch", json);
        Assert.DoesNotContain("private-output", json);
        Assert.DoesNotContain("private-cloud", json);
        Assert.DoesNotContain("private-device", json);
        Assert.DoesNotContain("private-audio.wav", json);
        Assert.DoesNotContain("private-recovery.wav", json);
        Assert.DoesNotContain("apiServer", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hasCompletedOnboarding", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pluginEnabledState", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RoundTrip_MergesEveryLocalCategoryAndIsIdempotent()
    {
        var source = CreateProfile("source-roundtrip");
        var now = DateTime.UtcNow;
        source.Settings.Save(source.Settings.Current with
        {
            MainDictationHotkeys = ["Ctrl+Alt+F8"],
            ToggleHotkey = "Ctrl+Alt+F8",
            PushToTalkHotkey = "Ctrl+Alt+F8",
            EnabledPackIds = ["medical"],
            Language = "de",
            LanguageHints = ["de", "en"],
            AutoPaste = false,
            UpdateChannel = "daily"
        });
        source.Workflows.AddWorkflow(new Workflow
        {
            Id = "source-workflow",
            Name = "Portable cleanup",
            Template = WorkflowTemplate.CleanedText,
            Trigger = WorkflowTrigger.Manual(),
            Behavior = new WorkflowBehavior { FineTuning = "Keep product names." },
            Output = new WorkflowOutput { AutoEnter = true }
        });
        source.Dictionary.AddEntry(new DictionaryEntry
        {
            Id = "manual-entry",
            EntryType = DictionaryEntryType.Correction,
            Original = "Type whisper",
            Replacement = "TypeWhisper",
            CreatedAt = now,
            UpdatedAt = now
        });
        source.Dictionary.AddEntry(new DictionaryEntry
        {
            Id = "pack:medical:excluded term",
            EntryType = DictionaryEntryType.Term,
            Original = "excluded term",
            CreatedAt = now,
            UpdatedAt = now
        });
        source.Snippets.AddSnippet(new Snippet
        {
            Id = "source-snippet",
            Trigger = "my address",
            Replacement = "Example Street 1",
            Tags = "personal"
        });
        source.History.AddRecord(new TranscriptionRecord
        {
            Id = "source-history",
            Timestamp = now,
            RawText = "raw round trip",
            FinalText = "final round trip",
            WorkflowId = "source-workflow",
            AudioFileName = "must-not-return.wav",
            RecoveryAudioFileName = "must-not-return-recovery.wav",
            DurationSeconds = 1.5,
            Language = "de"
        });

        var json = await source.Backup.ExportAsync();
        var destination = CreateProfile("destination-roundtrip");
        destination.Settings.Save(destination.Settings.Current with
        {
            MainDictationHotkeys = [],
            ToggleHotkey = "",
            PushToTalkHotkey = ""
        });

        var preview = destination.Backup.PreviewImport(json);
        var first = await destination.Backup.ImportAsync(json);
        var second = await destination.Backup.ImportAsync(json);

        Assert.True(preview.IsValid, preview.Error);
        Assert.Equal(1, preview.Counts[BackupCategory.Workflows]);
        Assert.True(first.Success, first.Error);
        Assert.Single(destination.Workflows.Workflows);
        Assert.Single(destination.Dictionary.Entries, entry =>
            !entry.Id.StartsWith("pack:", StringComparison.Ordinal));
        Assert.Single(destination.Snippets.Snippets);
        Assert.Single(destination.History.Records);
        Assert.Equal("Portable cleanup", destination.Workflows.Workflows[0].Name);
        Assert.Equal("TypeWhisper", destination.Dictionary.Entries.Single(entry =>
            !entry.Id.StartsWith("pack:", StringComparison.Ordinal)).Replacement);
        Assert.Equal("Example Street 1", destination.Snippets.Snippets[0].Replacement);
        Assert.Null(destination.History.Records[0].AudioFileName);
        Assert.Null(destination.History.Records[0].RecoveryAudioFileName);
        Assert.Equal(destination.Workflows.Workflows[0].Id, destination.History.Records[0].WorkflowId);
        Assert.Equal(["medical"], destination.Settings.Current.EnabledPackIds);
        Assert.Equal(["Ctrl+Alt+F8"], destination.Settings.Current.GetMainDictationHotkeys());
        Assert.Equal("de", destination.Settings.Current.Language);
        Assert.False(destination.Settings.Current.AutoPaste);
        Assert.True(second.Success, second.Error);
        Assert.Equal(0, second.Categories[BackupCategory.Workflows].Imported);
        Assert.Equal(0, second.Categories[BackupCategory.Dictionary].Imported);
        Assert.Equal(0, second.Categories[BackupCategory.Snippets].Imported);
        Assert.Equal(0, second.Categories[BackupCategory.History].Imported);
        Assert.Equal(0, second.Categories[BackupCategory.Preferences].Imported);
        Assert.Equal(1, second.Categories[BackupCategory.Preferences].Skipped);
        Assert.Single(destination.Workflows.Workflows);
        Assert.Single(destination.Dictionary.Entries, entry =>
            !entry.Id.StartsWith("pack:", StringComparison.Ordinal));
        Assert.Single(destination.Snippets.Snippets);
        Assert.Single(destination.History.Records);
    }

    [Fact]
    public async Task Import_PreservesMachineLocalDestinationSettings()
    {
        var source = CreateProfile("source-machine-local");
        source.Settings.Save(source.Settings.Current with
        {
            Language = "de",
            GroqApiKey = "source-secret",
            SelectedMicrophoneDevice = 42,
            LocalModelStoragePath = @"X:\source-models"
        });
        var json = await source.Backup.ExportAsync(new BackupExportOptions { Categories = BackupCategory.Preferences });
        var destination = CreateProfile("destination-machine-local");
        destination.Settings.Save(destination.Settings.Current with
        {
            GroqApiKey = "destination-secret",
            SelectedMicrophoneDevice = 7,
            LocalModelStoragePath = @"D:\destination-models",
            WatchFolderPath = @"D:\destination-watch"
        });

        var result = await destination.Backup.ImportAsync(json);

        Assert.True(result.Success, result.Error);
        Assert.Equal("de", destination.Settings.Current.Language);
        Assert.Equal("destination-secret", destination.Settings.Current.GroqApiKey);
        Assert.Equal(7, destination.Settings.Current.SelectedMicrophoneDevice);
        Assert.Equal(@"D:\destination-models", destination.Settings.Current.LocalModelStoragePath);
        Assert.Equal(@"D:\destination-watch", destination.Settings.Current.WatchFolderPath);
    }

    [Fact]
    public async Task Preview_RejectsUnknownSchemaBeforeImport()
    {
        var profile = CreateProfile("invalid-schema");
        var json = await profile.Backup.ExportAsync();
        var root = JsonNode.Parse(json)!.AsObject();
        root["schemaVersion"] = 999;

        var preview = profile.Backup.PreviewImport(root.ToJsonString());
        var result = await profile.Backup.ImportAsync(root.ToJsonString());

        Assert.False(preview.IsValid);
        Assert.Contains("newer", preview.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Import_RespectsDestinationHistoryRetention()
    {
        var source = CreateProfile("source-retention");
        source.History.AddRecord(new TranscriptionRecord
        {
            Id = "old",
            Timestamp = DateTime.UtcNow.AddDays(-3),
            RawText = "old",
            FinalText = "old"
        });
        source.History.AddRecord(new TranscriptionRecord
        {
            Id = "recent",
            Timestamp = DateTime.UtcNow,
            RawText = "recent",
            FinalText = "recent"
        });
        var json = await source.Backup.ExportAsync(new BackupExportOptions { Categories = BackupCategory.History });
        var destination = CreateProfile("destination-retention");
        destination.Settings.Save(destination.Settings.Current with
        {
            HistoryRetentionMode = HistoryRetentionMode.Duration,
            HistoryRetentionMinutes = 24 * 60
        });

        var result = await destination.Backup.ImportAsync(json);

        Assert.True(result.Success, result.Error);
        Assert.Single(destination.History.Records);
        Assert.Equal("recent", destination.History.Records[0].FinalText);
        Assert.Equal(1, result.Categories[BackupCategory.History].Imported);
        Assert.Equal(1, result.Categories[BackupCategory.History].Skipped);
    }

    [Fact]
    public async Task Import_RecordsOnlyNewSuccessfulHistoryInUsageStatistics()
    {
        var source = CreateProfile("source-statistics");
        source.History.AddRecord(new TranscriptionRecord
        {
            Id = "succeeded",
            Timestamp = DateTime.UtcNow,
            RawText = "one two three",
            FinalText = "one two three",
            DurationSeconds = 2,
            Status = TranscriptionRecordStatus.Succeeded
        });
        source.History.AddRecord(new TranscriptionRecord
        {
            Id = "failed",
            Timestamp = DateTime.UtcNow.AddSeconds(-1),
            RawText = "not counted",
            FinalText = "not counted",
            DurationSeconds = 1,
            Status = TranscriptionRecordStatus.WorkflowPostProcessingFailed
        });
        var json = await source.Backup.ExportAsync(new BackupExportOptions { Categories = BackupCategory.History });

        var destination = CreateProfile("destination-statistics");
        var statistics = new UsageStatisticsService(Path.Combine(_directory, "destination-statistics", "usage.json"));
        var backup = new BackupRestoreService(
            destination.Settings,
            destination.Workflows,
            destination.Dictionary,
            destination.Snippets,
            destination.History,
            usageStatisticsService: statistics);

        var first = await backup.ImportAsync(json);
        var second = await backup.ImportAsync(json);

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.Single(statistics.Days);
        Assert.Equal(1, statistics.Days[0].TranscriptionCount);
        Assert.Equal(3, statistics.Days[0].TotalWords);
    }

    [Fact]
    public async Task NullContainingJson_IsRejectedWithoutThrowing()
    {
        var source = CreateProfile("source-null-json");
        source.Workflows.AddWorkflow(new Workflow
        {
            Id = "workflow",
            Name = "Workflow",
            Template = WorkflowTemplate.Custom,
            Trigger = WorkflowTrigger.Manual()
        });
        source.Snippets.AddSnippet(new Snippet { Id = "snippet", Trigger = "trigger", Replacement = "replacement" });
        source.History.AddRecord(new TranscriptionRecord
        {
            Id = "history",
            Timestamp = DateTime.UtcNow,
            RawText = "raw",
            FinalText = "final"
        });
        var json = await source.Backup.ExportAsync();
        var baseline = JsonNode.Parse(json)!.AsObject();
        var variants = new List<JsonObject>();

        var nullWorkflows = baseline.DeepClone().AsObject();
        nullWorkflows["data"]!["workflows"] = null;
        variants.Add(nullWorkflows);
        var nullDictionary = baseline.DeepClone().AsObject();
        nullDictionary["data"]!["dictionary"] = null;
        variants.Add(nullDictionary);
        var nullTrigger = baseline.DeepClone().AsObject();
        nullTrigger["data"]!["workflows"]![0]!["trigger"] = null;
        variants.Add(nullTrigger);
        var nullReplacement = baseline.DeepClone().AsObject();
        nullReplacement["data"]!["snippets"]![0]!["replacement"] = null;
        variants.Add(nullReplacement);
        var nullHistoryText = baseline.DeepClone().AsObject();
        nullHistoryText["data"]!["history"]![0]!["rawText"] = null;
        variants.Add(nullHistoryText);
        var nullHotkeyBindings = baseline.DeepClone().AsObject();
        nullHotkeyBindings["data"]!["hotkeys"]!["bindings"] = null;
        variants.Add(nullHotkeyBindings);

        var destination = CreateProfile("destination-null-json");
        foreach (var variant in variants)
        {
            var malformed = variant.ToJsonString();
            var preview = destination.Backup.PreviewImport(malformed);
            var result = await destination.Backup.ImportAsync(malformed);
            Assert.False(preview.IsValid);
            Assert.False(result.Success);
            Assert.NotNull(preview.Error);
            Assert.NotNull(result.Error);
        }
    }

    [Fact]
    public async Task Import_RollsBackEarlierCategoriesWhenALaterLocalWriteFails()
    {
        var source = CreateProfile("source-rollback");
        source.Workflows.AddWorkflow(new Workflow
        {
            Id = "workflow",
            Name = "Rollback workflow",
            Template = WorkflowTemplate.Summary,
            Trigger = WorkflowTrigger.Manual()
        });
        source.History.AddRecord(new TranscriptionRecord
        {
            Id = "history",
            Timestamp = DateTime.UtcNow,
            RawText = "rollback history",
            FinalText = "rollback history"
        });
        var json = await source.Backup.ExportAsync();
        var destination = CreateProfile("destination-rollback");
        var failingHistory = new Mock<IHistoryService>();
        failingHistory.SetupGet(service => service.Records).Returns([]);
        failingHistory.Setup(service => service.EnsureLoadedAsync()).Returns(Task.CompletedTask);
        failingHistory.Setup(service => service.TryReplaceAll(It.IsAny<IReadOnlyList<TranscriptionRecord>>()))
            .Returns(false);
        var backup = new BackupRestoreService(
            destination.Settings,
            destination.Workflows,
            destination.Dictionary,
            destination.Snippets,
            failingHistory.Object);

        var result = await backup.ImportAsync(json);

        Assert.False(result.Success);
        Assert.Empty(destination.Workflows.Workflows);
        Assert.Contains("rollback was incomplete", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Warnings, warning => warning.Contains("history", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Import_MaterializesEnabledBuiltInPackBeforeAnySettingsViewModelExists()
    {
        var source = CreateProfile("source-pack");
        source.Settings.Save(source.Settings.Current with { EnabledPackIds = ["web-dev"] });
        var json = await source.Backup.ExportAsync(
            new BackupExportOptions { Categories = BackupCategory.Dictionary });
        var destination = CreateProfile("destination-pack");

        var result = await destination.Backup.ImportAsync(json);

        Assert.True(result.Success, result.Error);
        Assert.Contains(destination.Dictionary.Entries, entry =>
            entry.Id.StartsWith("pack:web-dev:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Import_AcceptsCaseInsensitiveHotkeyActionNames()
    {
        var source = CreateProfile("source-hotkey-casing");
        source.Settings.Save(source.Settings.Current with { MainDictationHotkeys = ["Ctrl+Alt+F7"] });
        var json = await source.Backup.ExportAsync(
            new BackupExportOptions { Categories = BackupCategory.Hotkeys });
        json = json.Replace("mainDictation", "maindictation", StringComparison.Ordinal);
        var destination = CreateProfile("destination-hotkey-casing");
        destination.Settings.Save(destination.Settings.Current with
        {
            MainDictationHotkeys = [],
            ToggleHotkey = "",
            PushToTalkHotkey = ""
        });

        var result = await destination.Backup.ImportAsync(json);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["Ctrl+Alt+F7"], destination.Settings.Current.GetMainDictationHotkeys());
    }

    [Fact]
    public async Task Import_SerializesConcurrentCollectionWritesWithoutLosingThem()
    {
        var source = CreateProfile("source-concurrent-write");
        source.Workflows.AddWorkflow(new Workflow
        {
            Id = "workflow",
            Name = "Concurrent restore",
            Template = WorkflowTemplate.Summary,
            Trigger = WorkflowTrigger.Manual()
        });
        source.Dictionary.AddEntry(new DictionaryEntry
        {
            Id = "restored-entry",
            EntryType = DictionaryEntryType.Term,
            Original = "restored"
        });
        var json = await source.Backup.ExportAsync();
        var destination = CreateProfile("destination-concurrent-write");
        using var workflowWriteStarted = new ManualResetEventSlim();
        using var releaseWorkflowWrite = new ManualResetEventSlim();
        var workflows = new Mock<IWorkflowService>();
        workflows.SetupGet(service => service.Workflows).Returns([]);
        workflows.Setup(service => service.TryReplaceAll(It.IsAny<IReadOnlyList<Workflow>>()))
            .Returns(() =>
            {
                workflowWriteStarted.Set();
                releaseWorkflowWrite.Wait(TimeSpan.FromSeconds(5));
                return true;
            });
        var backup = new BackupRestoreService(
            destination.Settings,
            workflows.Object,
            destination.Dictionary,
            destination.Snippets,
            destination.History);

        var import = Task.Run(() => backup.ImportAsync(json));
        Assert.True(workflowWriteStarted.Wait(TimeSpan.FromSeconds(5)));
        var concurrentWrite = Task.Run(() => destination.Dictionary.AddEntry(new DictionaryEntry
        {
            Id = "concurrent-entry",
            EntryType = DictionaryEntryType.Term,
            Original = "concurrent"
        }));
        await Task.Delay(100);
        Assert.False(concurrentWrite.IsCompleted);

        releaseWorkflowWrite.Set();
        var result = await import;
        await concurrentWrite;

        Assert.True(result.Success, result.Error);
        Assert.Contains(destination.Dictionary.Entries, entry => entry.Original == "restored");
        Assert.Contains(destination.Dictionary.Entries, entry => entry.Original == "concurrent");
    }

    private Profile CreateProfile(string name)
    {
        var root = Path.Combine(_directory, name);
        Directory.CreateDirectory(root);
        var settings = new SettingsService(Path.Combine(root, "settings.json"));
        var workflows = new WorkflowService(Path.Combine(root, "workflows.json"));
        var dictionary = new DictionaryService(Path.Combine(root, "dictionary.json"));
        var snippets = new SnippetService(Path.Combine(root, "snippets.json"));
        var history = new HistoryService(Path.Combine(root, "history.json"));
        var backup = new BackupRestoreService(settings, workflows, dictionary, snippets, history);
        return new Profile(settings, workflows, dictionary, snippets, history, backup);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private sealed record Profile(
        SettingsService Settings,
        WorkflowService Workflows,
        DictionaryService Dictionary,
        SnippetService Snippets,
        HistoryService History,
        BackupRestoreService Backup);
}
