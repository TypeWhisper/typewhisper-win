using System.Text.Json.Serialization;

#pragma warning disable CS1591 // Backup DTO member names intentionally mirror the public JSON schema.

namespace TypeWhisper.Core.Models.Backup;

/// <summary>
/// Selects portable data categories in a TypeWhisper backup.
/// </summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<BackupCategory>))]
public enum BackupCategory
{
    None = 0,
    Workflows = 1 << 0,
    Dictionary = 1 << 1,
    Snippets = 1 << 2,
    Hotkeys = 1 << 3,
    Plugins = 1 << 4,
    History = 1 << 5,
    Preferences = 1 << 6,
    All = Workflows | Dictionary | Snippets | Hotkeys | Plugins | History | Preferences
}

/// <summary>
/// Controls which categories are exported.
/// </summary>
public sealed record BackupExportOptions
{
    public BackupCategory Categories { get; init; } = BackupCategory.All;
}

/// <summary>
/// Controls which categories are merged during import.
/// </summary>
public sealed record BackupImportOptions
{
    public BackupCategory Categories { get; init; } = BackupCategory.All;
}

/// <summary>
/// Versioned, portable TypeWhisper backup envelope.
/// </summary>
public sealed record SettingsBackupDocument
{
    public const string CurrentFormat = "typewhisper-backup";
    public const int CurrentSchemaVersion = 1;

    public string Format { get; init; } = CurrentFormat;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string SourcePlatform { get; init; } = "windows";
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;
    public string AppVersion { get; init; } = "unknown";
    public required SettingsBackupData Data { get; init; }
}

/// <summary>
/// Portable data contained in a backup. Device-local state and credentials are deliberately absent.
/// </summary>
public sealed record SettingsBackupData
{
    public IReadOnlyList<BackupWorkflow> Workflows { get; init; } = [];
    public BackupDictionary Dictionary { get; init; } = new();
    public IReadOnlyList<BackupSnippet> Snippets { get; init; } = [];
    public BackupHotkeys Hotkeys { get; init; } = new();
    public IReadOnlyList<BackupPlugin> Plugins { get; init; } = [];
    public IReadOnlyList<BackupHistoryEntry> History { get; init; } = [];
    public BackupPreferences? Preferences { get; init; }
}

public sealed record BackupWorkflow
{
    public required string Name { get; init; }
    public bool IsEnabled { get; init; } = true;
    public required WorkflowTemplate Template { get; init; }
    public required WorkflowTrigger Trigger { get; init; }
    public WorkflowBehavior Behavior { get; init; } = new();
    public WorkflowOutput Output { get; init; } = new();
}

public sealed record BackupDictionary
{
    public IReadOnlyList<BackupDictionaryEntry> Entries { get; init; } = [];
    public IReadOnlyList<string> EnabledPackIds { get; init; } = [];
}

public sealed record BackupDictionaryEntry
{
    public required DictionaryEntryType EntryType { get; init; }
    public required string Original { get; init; }
    public string? Replacement { get; init; }
    public bool CaseSensitive { get; init; }
    public bool IsRegex { get; init; }
    public bool IsEnabled { get; init; } = true;
    public DictionaryEntrySource Source { get; init; } = DictionaryEntrySource.Manual;
}

public sealed record BackupSnippet
{
    public required string Trigger { get; init; }
    public required string Replacement { get; init; }
    public bool CaseSensitive { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string Tags { get; init; } = "";
}

public sealed record BackupHotkeys
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Bindings { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

public sealed record BackupPlugin
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public bool WasEnabled { get; init; }
}

public sealed record BackupHistoryEntry
{
    public required DateTime Timestamp { get; init; }
    public required string RawText { get; init; }
    public required string FinalText { get; init; }
    public string? AppName { get; init; }
    public string? AppProcessName { get; init; }
    public string? AppUrl { get; init; }
    public double DurationSeconds { get; init; }
    public string? Language { get; init; }
    public string? WorkflowName { get; init; }
    public TranscriptionRecordStatus Status { get; init; }
    public string EngineUsed { get; init; } = "whisper";
    public string? ModelUsed { get; init; }
    public string? TranscriptionTaskUsed { get; init; }
    public bool UsedTranscriptionFallback { get; init; }
}

/// <summary>
/// Explicit allowlist of settings that are safe and useful on another machine.
/// </summary>
public sealed record BackupPreferences
{
    public string Language { get; init; } = "auto";
    public IReadOnlyList<string> LanguageHints { get; init; } = [];
    public bool AutoPaste { get; init; }
    public RecordingMode Mode { get; init; }
    public HistoryRetentionMode HistoryRetentionMode { get; init; }
    public int HistoryRetentionMinutes { get; init; }
    public bool WhisperModeEnabled { get; init; }
    public bool AudioDuckingEnabled { get; init; }
    public float AudioDuckingLevel { get; init; }
    public bool PauseMediaDuringRecording { get; init; }
    public bool SoundFeedbackEnabled { get; init; }
    public bool TranscribeShortQuietClipsAggressively { get; init; }
    public bool TranscriptionNumberNormalizationEnabled { get; init; }
    public bool ShortUtterancePunctuationEnabled { get; init; }
    public EnglishOutputVariant EnglishOutputVariant { get; init; }
    public GermanOutputVariant GermanOutputVariant { get; init; }
    public bool LiveTranscriptionEnabled { get; init; }
    public bool OnlineAsrBatchLiveTranscriptionEnabled { get; init; }
    public double LiveTranscriptionFontSize { get; init; }
    public bool SilenceAutoStopEnabled { get; init; }
    public int SilenceAutoStopSeconds { get; init; }
    public IndicatorStyle IndicatorStyle { get; init; }
    public OverlayPosition OverlayPosition { get; init; }
    public OverlayWidget OverlayLeftWidget { get; init; }
    public OverlayWidget OverlayRightWidget { get; init; }
    public int PreviewBubbleAutoHideMilliseconds { get; init; }
    public string TranscriptionTask { get; init; } = "transcribe";
    public string? TranslationTargetLanguage { get; init; }
    public string? LastTranslationTargetLanguage { get; init; }
    public int DictationRecoveryRetentionDays { get; init; }
    public string DictationRecoveryLanguage { get; init; } = "auto";
    public string DictationRecoveryTask { get; init; } = "transcribe";
    public bool DictationRecoveryAutomaticFallbackEnabled { get; init; }
    public bool WorkflowRequestRecoveryEnabled { get; init; }
    public bool RecorderMicEnabled { get; init; }
    public bool RecorderSystemAudioEnabled { get; init; }
    public string RecorderOutputFormat { get; init; } = "wav";
    public string RecorderTrackMode { get; init; } = "mixed";
    public string RecorderMicDuckingMode { get; init; } = "aggressive";
    public bool RecorderTranscriptionEnabled { get; init; }
    public string RecorderTranscriptionTask { get; init; } = "transcribe";
    public string? RecorderTranslationTargetLanguage { get; init; }
    public string WatchFolderOutputFormat { get; init; } = "md";
    public bool WatchFolderDeleteSource { get; init; }
    public string WatchFolderLanguage { get; init; } = "auto";
    public bool VocabularyBoostingEnabled { get; init; }
    public string SelectedIndustryPresetId { get; init; } = "general";
    public bool SaveToHistoryEnabled { get; init; }
    public bool MemoryEnabled { get; init; }
    public bool TargetAppCorrectionLearningEnabled { get; init; }
    public string? UpdateChannel { get; init; }
}

public sealed record BackupImportPreview
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }
    public string? SourcePlatform { get; init; }
    public DateTimeOffset? ExportedAt { get; init; }
    public string? AppVersion { get; init; }
    public IReadOnlyDictionary<BackupCategory, int> Counts { get; init; } =
        new Dictionary<BackupCategory, int>();
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record BackupCategoryImportResult
{
    public int Imported { get; init; }
    public int Skipped { get; init; }
    public int Conflicts { get; init; }
}

public sealed record BackupPluginImportResult
{
    public BackupCategoryImportResult CategoryResult { get; init; } = new();
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool RestartRequired { get; init; }
}

public sealed record BackupImportResult
{
    public bool Success { get; init; }
    public bool RestartRequired { get; init; }
    public IReadOnlyDictionary<BackupCategory, BackupCategoryImportResult> Categories { get; init; } =
        new Dictionary<BackupCategory, BackupCategoryImportResult>();
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? Error { get; init; }
}

#pragma warning restore CS1591
