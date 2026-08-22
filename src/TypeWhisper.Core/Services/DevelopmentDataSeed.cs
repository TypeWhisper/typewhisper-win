using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Represents generated development sample data.
/// </summary>
public sealed record DevelopmentDataSeed
{
    /// <summary>
    /// Gets the seeded app settings.
    /// </summary>
    public required AppSettings Settings { get; init; }
    /// <summary>
    /// Gets the seeded dictionary entries.
    /// </summary>
    public required IReadOnlyList<DictionaryEntry> DictionaryEntries { get; init; }
    /// <summary>
    /// Gets the seeded snippets.
    /// </summary>
    public required IReadOnlyList<Snippet> Snippets { get; init; }
    /// <summary>
    /// Gets the seeded workflows.
    /// </summary>
    public required IReadOnlyList<Workflow> Workflows { get; init; }
    /// <summary>
    /// Gets the seeded history records.
    /// </summary>
    public required IReadOnlyList<TranscriptionRecord> HistoryRecords { get; init; }
}

/// <summary>
/// Lists possible development seeding outcomes.
/// </summary>
public enum DevelopmentDataSeedResult
{
    /// <summary>
    /// Indicates that the current build is not allowed to seed development data.
    /// </summary>
    NotDevelopmentBuild,
    /// <summary>
    /// Indicates that development data was cleared and seeded.
    /// </summary>
    Seeded
}

/// <summary>
/// Creates deterministic sample data for local development builds.
/// </summary>
public static class DevelopmentDataSeedFactory
{
    /// <summary>
    /// Creates a fresh development seed.
    /// </summary>
    public static DevelopmentDataSeed CreateDefault(DateTime? referenceUtc = null)
    {
        var now = (referenceUtc ?? DateTime.UtcNow).Date.AddHours(10);
        var createdAt = now.AddDays(-2);

        return new DevelopmentDataSeed
        {
            Settings = AppSettings.Default with
            {
                HasCompletedOnboarding = true,
                PluginFirstRunCompleted = true,
                VocabularyBoostingEnabled = true,
                MainDictationHotkeys = ["Ctrl+Shift+F9"],
                ToggleHotkey = "Ctrl+Shift+F9",
                PushToTalkHotkey = "Ctrl+Shift+F9",
                RecentTranscriptionsHotkeys = ["Ctrl+Shift+H"],
                RecentTranscriptionsHotkey = "Ctrl+Shift+H",
                CopyLastTranscriptionHotkeys = ["Ctrl+Shift+C"],
                CopyLastTranscriptionHotkey = "Ctrl+Shift+C",
                WorkflowPaletteHotkeys = ["Ctrl+Shift+Space"],
                WorkflowPaletteHotkey = "Ctrl+Shift+Space",
                SaveToHistoryEnabled = true,
                LiveTranscriptionEnabled = true,
                TargetAppCorrectionLearningEnabled = true,
                PluginEnabledState = new Dictionary<string, bool>()
            },
            DictionaryEntries =
            [
                DictionaryTerm("dev-term-typewhisper", "TypeWhisper", createdAt),
                DictionaryTerm("dev-term-velopack", "Velopack", createdAt),
                DictionaryTerm("dev-term-whisper-cpp", "Whisper.cpp", createdAt),
                DictionaryCorrection("dev-correction-typewhisper", "type whisper", "TypeWhisper", createdAt),
                DictionaryCorrection("dev-correction-velopack", "velo pack", "Velopack", createdAt)
            ],
            Snippets =
            [
                new Snippet
                {
                    Id = "dev-snippet-standup",
                    Trigger = "standup",
                    Replacement = "Yesterday: {clipboard}\nToday:\nBlocked:",
                    Tags = "dev,daily",
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt
                },
                new Snippet
                {
                    Id = "dev-snippet-pr",
                    Trigger = "pr summary",
                    Replacement = "Summary:\n- \n\nTests:\n- ",
                    Tags = "dev,github",
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt
                }
            ],
            Workflows =
            [
                new Workflow
                {
                    Id = "dev-workflow-meeting-notes",
                    Name = "Dev Meeting Notes",
                    SortOrder = 0,
                    Template = WorkflowTemplate.MeetingNotes,
                    Trigger = WorkflowTrigger.Manual(),
                    Behavior = new WorkflowBehavior
                    {
                        FineTuning = "Use concise headings and end with action items."
                    },
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt
                },
                new Workflow
                {
                    Id = "dev-workflow-json-extract",
                    Name = "Dev JSON Extract",
                    SortOrder = 1,
                    Template = WorkflowTemplate.Json,
                    Trigger = WorkflowTrigger.Hotkey("Ctrl+Shift+J"),
                    Output = new WorkflowOutput { Format = "JSON" },
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt
                },
                new Workflow
                {
                    Id = "dev-workflow-cleanup",
                    Name = "Dev Cleanup",
                    SortOrder = 2,
                    Template = WorkflowTemplate.CleanedText,
                    Trigger = WorkflowTrigger.Global(),
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt
                }
            ],
            HistoryRecords = CreateHistoryRecords(now)
        };
    }

    private static IReadOnlyList<TranscriptionRecord> CreateHistoryRecords(DateTime now)
    {
        (string Name, string ProcessName)[] apps =
        [
            ("Notepad", "notepad"),
            ("Visual Studio Code", "code"),
            ("Microsoft Outlook", "outlook"),
            ("Slack", "slack"),
            ("Microsoft Teams", "ms-teams"),
            ("Google Chrome", "chrome"),
            ("Obsidian", "obsidian"),
            ("Linear", "linear")
        ];
        (string Engine, string Model)[] providers =
        [
            ("whisper", "large-v3"),
            ("parakeet", "parakeet-tdt-0.6b"),
            ("openai", "gpt-4o-mini-transcribe"),
            ("groq", "whisper-large-v3-turbo"),
            ("deepgram", "nova-3")
        ];
        (string Raw, string Final)[] samples =
        [
            (
                "summarize the release checklist and call out every risky item before the team starts the final validation",
                "Summarize the release checklist and call out every risky item before the team starts the final validation."),
            (
                "turn these rough meeting notes into clear decisions owners and action items for our next product review",
                "Turn these rough meeting notes into clear decisions, owners, and action items for our next product review."),
            (
                "draft a friendly customer response that explains the workaround and asks whether the issue still occurs",
                "Draft a friendly customer response that explains the workaround and asks whether the issue still occurs."),
            (
                "capture the exact reproduction steps expected result and actual behavior from this quick voice note",
                "Capture the exact reproduction steps, expected result, and actual behavior from this quick voice note."),
            (
                "rewrite this project update so it stays concise but includes the completed work and remaining blockers",
                "Rewrite this project update so it stays concise but includes the completed work and remaining blockers."),
            (
                "convert this brainstorm into three practical implementation options with benefits tradeoffs and open questions",
                "Convert this brainstorm into three practical implementation options with benefits, tradeoffs, and open questions."),
            (
                "prepare a neutral changelog entry describing the improved settings flow and the new keyboard shortcuts",
                "Prepare a neutral changelog entry describing the improved settings flow and the new keyboard shortcuts."),
            (
                "extract the customer request acceptance criteria and follow up questions from the conversation below",
                "Extract the customer request, acceptance criteria, and follow-up questions from the conversation below."),
            (
                "write a short launch announcement that highlights privacy local processing and a faster dictation workflow",
                "Write a short launch announcement that highlights privacy, local processing, and a faster dictation workflow."),
            (
                "clean up this email before I send the beta tester our latest build and installation instructions",
                "Clean up this email before I send the beta tester our latest build and installation instructions."),
            (
                "organize the weekly planning note into priorities active tasks waiting items and decisions we need",
                "Organize the weekly planning note into priorities, active tasks, waiting items, and decisions we need."),
            (
                "create a pull request summary with the user facing change technical notes and verification results",
                "Create a pull request summary with the user-facing change, technical notes, and verification results.")
        ];
        int[] hours = [10, 13, 16, 19, 8];
        var records = new List<TranscriptionRecord>();

        for (var dayOffset = 0; dayOffset < 30; dayOffset++)
        {
            if (dayOffset is 6 or 15 or 24)
                continue;

            var recordsForDay = 2 + dayOffset % 4;
            for (var recordIndex = 0; recordIndex < recordsForDay; recordIndex++)
            {
                var sequence = records.Count;
                var app = apps[(dayOffset + recordIndex * 3) % apps.Length];
                var provider = providers[(dayOffset * 2 + recordIndex) % providers.Length];
                var sample = samples[(dayOffset * 3 + recordIndex) % samples.Length];
                var timestamp = now.Date.AddDays(-dayOffset).AddHours(hours[recordIndex]);
                var isWorkflowSample = dayOffset == 29 && recordIndex == recordsForDay - 1;

                records.Add(HistoryRecord(
                    $"dev-history-{sequence + 1:000}",
                    timestamp,
                    sample.Raw,
                    sample.Final,
                    app.Name,
                    app.ProcessName,
                    8.5 + (dayOffset * 3 + recordIndex * 5) % 16,
                    provider.Engine,
                    provider.Model,
                    isWorkflowSample ? "Dev Meeting Notes" : null,
                    isWorkflowSample ? "dev-workflow-meeting-notes" : null));
            }
        }

        return records;
    }

    private static DictionaryEntry DictionaryTerm(string id, string term, DateTime createdAt) =>
        new()
        {
            Id = id,
            EntryType = DictionaryEntryType.Term,
            Original = term,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

    private static DictionaryEntry DictionaryCorrection(
        string id,
        string original,
        string replacement,
        DateTime createdAt) =>
        new()
        {
            Id = id,
            EntryType = DictionaryEntryType.Correction,
            Original = original,
            Replacement = replacement,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

    private static TranscriptionRecord HistoryRecord(
        string id,
        DateTime timestamp,
        string rawText,
        string finalText,
        string appName,
        string appProcessName,
        double durationSeconds,
        string engine,
        string model,
        string? profileName = null,
        string? workflowId = null) =>
        new()
        {
            Id = id,
            Timestamp = timestamp,
            RawText = rawText,
            FinalText = finalText,
            AppName = appName,
            AppProcessName = appProcessName,
            DurationSeconds = durationSeconds,
            Language = "en",
            ProfileName = profileName,
            WorkflowId = workflowId,
            EngineUsed = engine,
            ModelUsed = model,
            CreatedAt = timestamp
        };
}

/// <summary>
/// Clears core user data and writes development sample data for debug builds.
/// </summary>
public sealed class DevelopmentDataSeeder
{
    private readonly ISettingsService _settings;
    private readonly IHistoryService _history;
    private readonly IDictionaryService _dictionary;
    private readonly ISnippetService _snippets;
    private readonly IWorkflowService _workflows;
    private readonly IUsageStatisticsService? _usageStatistics;
    private readonly Func<bool> _isDevelopmentBuild;

    /// <summary>
    /// Initializes a new instance of the DevelopmentDataSeeder class.
    /// </summary>
    /// <param name="settings">The settings service to reset.</param>
    /// <param name="history">The history service to reset.</param>
    /// <param name="dictionary">The dictionary service to reset.</param>
    /// <param name="snippets">The snippet service to reset.</param>
    /// <param name="workflows">The workflow service to reset.</param>
    /// <param name="usageStatistics">Optional usage statistics store to reseed.</param>
    /// <param name="isDevelopmentBuild">Optional build gate override.</param>
    public DevelopmentDataSeeder(
        ISettingsService settings,
        IHistoryService history,
        IDictionaryService dictionary,
        ISnippetService snippets,
        IWorkflowService workflows,
        IUsageStatisticsService? usageStatistics = null,
        Func<bool>? isDevelopmentBuild = null)
    {
        _settings = settings;
        _history = history;
        _dictionary = dictionary;
        _snippets = snippets;
        _workflows = workflows;
        _usageStatistics = usageStatistics;
        _isDevelopmentBuild = isDevelopmentBuild ?? (() => TypeWhisperEnvironment.IsDevelopmentBuild);
    }

    /// <summary>
    /// Clears core user data and writes the default development seed.
    /// </summary>
    /// <param name="referenceUtc">Optional deterministic reference time for generated records.</param>
    public DevelopmentDataSeedResult ClearAndSeed(DateTime? referenceUtc = null)
    {
        if (!_isDevelopmentBuild())
            return DevelopmentDataSeedResult.NotDevelopmentBuild;

        var seed = DevelopmentDataSeedFactory.CreateDefault(referenceUtc);
        var current = _settings.Current;
        var seededSettings = seed.Settings with
        {
            UiLanguage = current.UiLanguage,
            UpdateChannel = current.UpdateChannel
        };

        _history.ClearAll();
        _dictionary.DeleteEntries(_dictionary.Entries.Select(entry => entry.Id).ToList());

        foreach (var snippet in _snippets.Snippets.ToList())
            _snippets.DeleteSnippet(snippet.Id);

        foreach (var workflow in _workflows.Workflows.ToList())
            _workflows.DeleteWorkflow(workflow.Id);

        _settings.Save(seededSettings);
        _dictionary.AddEntries(seed.DictionaryEntries);

        foreach (var snippet in seed.Snippets)
            _snippets.AddSnippet(snippet);

        foreach (var workflow in seed.Workflows)
            _workflows.AddWorkflow(workflow);

        foreach (var record in seed.HistoryRecords)
            _history.AddRecord(record);

        _usageStatistics?.ReplaceWithHistoryRecords(seed.HistoryRecords);

        return DevelopmentDataSeedResult.Seeded;
    }
}
