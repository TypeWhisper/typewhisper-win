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
            HistoryRecords =
            [
                HistoryRecord(
                    "dev-history-001",
                    now,
                    "type whisper dev seed should make the dashboard feel alive",
                    "TypeWhisper dev seed should make the dashboard feel alive.",
                    "Notepad",
                    "notepad",
                    7.5),
                HistoryRecord(
                    "dev-history-002",
                    now.AddHours(-2),
                    "summarize the release checklist and call out the risky items",
                    "Summarize the release checklist and call out the risky items.",
                    "Microsoft Outlook",
                    "outlook",
                    8.1),
                HistoryRecord(
                    "dev-history-003",
                    now.AddDays(-1).AddHours(2),
                    "turn this into meeting notes with action items",
                    "Turn this into meeting notes with action items.",
                    "Slack",
                    "slack",
                    5.8),
                HistoryRecord(
                    "dev-history-004",
                    now.AddDays(-1).AddHours(-1),
                    "capture the bug reproduction steps from this quick voice note",
                    "Capture the bug reproduction steps from this quick voice note.",
                    "Microsoft Teams",
                    "ms-teams",
                    6.4),
                HistoryRecord(
                    "dev-history-005",
                    now.AddDays(-2).AddHours(4),
                    "draft a short pull request summary and test notes",
                    "Draft a short pull request summary and test notes.",
                    "Visual Studio Code",
                    "code",
                    6.2),
                HistoryRecord(
                    "dev-history-006",
                    now.AddDays(-3).AddHours(1),
                    "rewrite this paragraph as a concise support response",
                    "Rewrite this paragraph as a concise support response.",
                    "Google Chrome",
                    "chrome",
                    4.9),
                HistoryRecord(
                    "dev-history-007",
                    now.AddDays(-4).AddHours(3),
                    "add the follow up tasks to the weekly planning note",
                    "Add the follow-up tasks to the weekly planning note.",
                    "Obsidian",
                    "obsidian",
                    5.5),
                HistoryRecord(
                    "dev-history-008",
                    now.AddDays(-5).AddHours(2),
                    "convert these rough notes into a launch announcement",
                    "Convert these rough notes into a launch announcement.",
                    "Notepad",
                    "notepad",
                    7.0),
                HistoryRecord(
                    "dev-history-009",
                    now.AddDays(-6).AddHours(4),
                    "make this status update warmer but keep it short",
                    "Make this status update warmer but keep it short.",
                    "Slack",
                    "slack",
                    4.4),
                HistoryRecord(
                    "dev-history-010",
                    now.AddDays(-7).AddHours(1),
                    "extract the customer request and the acceptance criteria",
                    "Extract the customer request and the acceptance criteria.",
                    "Linear",
                    "linear",
                    6.8),
                HistoryRecord(
                    "dev-history-011",
                    now.AddDays(-9).AddHours(5),
                    "turn this brainstorm into three concrete implementation options",
                    "Turn this brainstorm into three concrete implementation options.",
                    "Obsidian",
                    "obsidian",
                    8.6),
                HistoryRecord(
                    "dev-history-012",
                    now.AddDays(-10).AddHours(2),
                    "write a neutral changelog entry for the settings update",
                    "Write a neutral changelog entry for the settings update.",
                    "Visual Studio Code",
                    "code",
                    5.1),
                HistoryRecord(
                    "dev-history-013",
                    now.AddDays(-12).AddHours(3),
                    "clean up this email before I send it to the beta tester",
                    "Clean up this email before I send it to the beta tester.",
                    "Microsoft Outlook",
                    "outlook",
                    5.9),
                HistoryRecord(
                    "dev-history-014",
                    now.AddDays(-13).AddHours(1),
                    "make a short note about what changed in the prototype",
                    "Make a short note about what changed in the prototype.",
                    "Notepad",
                    "notepad",
                    4.7)
            ]
        };
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
        double durationSeconds) =>
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
            EngineUsed = "dev-seed",
            ModelUsed = "sample",
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

    /// <summary>
    /// Initializes a new instance of the DevelopmentDataSeeder class.
    /// </summary>
    /// <param name="settings">The settings service to reset.</param>
    /// <param name="history">The history service to reset.</param>
    /// <param name="dictionary">The dictionary service to reset.</param>
    /// <param name="snippets">The snippet service to reset.</param>
    /// <param name="workflows">The workflow service to reset.</param>
    public DevelopmentDataSeeder(
        ISettingsService settings,
        IHistoryService history,
        IDictionaryService dictionary,
        ISnippetService snippets,
        IWorkflowService workflows)
    {
        _settings = settings;
        _history = history;
        _dictionary = dictionary;
        _snippets = snippets;
        _workflows = workflows;
    }

    /// <summary>
    /// Clears core user data and writes the default development seed.
    /// </summary>
    public DevelopmentDataSeedResult ClearAndSeed()
    {
        if (!TypeWhisperEnvironment.IsDevelopmentBuild)
            return DevelopmentDataSeedResult.NotDevelopmentBuild;

        var seed = DevelopmentDataSeedFactory.CreateDefault();
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

        return DevelopmentDataSeedResult.Seeded;
    }
}
