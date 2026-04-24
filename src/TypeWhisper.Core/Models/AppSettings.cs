namespace TypeWhisper.Core.Models;

public record AppSettings
{
    public string ToggleHotkey { get; init; } = "Ctrl+Shift+F9";
    public string PushToTalkHotkey { get; init; } = "Ctrl+Shift";
    public string ToggleOnlyHotkey { get; init; } = "";
    public string HoldOnlyHotkey { get; init; } = "";
    public string Language { get; init; } = "auto";
    public bool AutoPaste { get; init; } = true;
    public RecordingMode Mode { get; init; } = RecordingMode.Toggle;
    public HistoryRetentionMode HistoryRetentionMode { get; init; } = HistoryRetentionMode.Duration;
    public int HistoryRetentionMinutes { get; init; } = 90 * 24 * 60;
    public int? SelectedMicrophoneDevice { get; init; }

    // Model
    public string? SelectedModelId { get; init; }

    // Manual file transcription
    public string? FileTranscriptionEngineOverride { get; init; }
    public string? FileTranscriptionModelOverride { get; init; }

    // Cloud Provider API Keys
    public string? GroqApiKey { get; init; }
    public string? OpenAiApiKey { get; init; }

    // Audio features
    public bool WhisperModeEnabled { get; init; }
    public bool AudioDuckingEnabled { get; init; }
    public float AudioDuckingLevel { get; init; } = 0.2f;
    public bool PauseMediaDuringRecording { get; init; }
    public bool SoundFeedbackEnabled { get; init; } = true;

    // Live transcription (streaming preview while recording)
    public bool LiveTranscriptionEnabled { get; init; } = true;

    // Silence detection
    public bool SilenceAutoStopEnabled { get; init; }
    public int SilenceAutoStopSeconds { get; init; } = 10;

    // Overlay
    public OverlayPosition OverlayPosition { get; init; } = OverlayPosition.Bottom;
    public OverlayWidget OverlayLeftWidget { get; init; } = OverlayWidget.Waveform;
    public OverlayWidget OverlayRightWidget { get; init; } = OverlayWidget.Timer;

    // Translation
    public string TranscriptionTask { get; init; } = "transcribe";
    public string? TranslationTargetLanguage { get; init; }

    // Watch folder automation
    public string? WatchFolderPath { get; init; }
    public string? WatchFolderOutputPath { get; init; }
    public string WatchFolderOutputFormat { get; init; } = "md";
    public bool WatchFolderAutoStart { get; init; }
    public bool WatchFolderDeleteSource { get; init; }
    public string WatchFolderLanguage { get; init; } = "auto";
    public string? WatchFolderEngineOverride { get; init; }
    public string? WatchFolderModelOverride { get; init; }

    // API Server
    public bool ApiServerEnabled { get; init; }
    public int ApiServerPort { get; init; } = 8978;

    // Dictionary
    public string[] EnabledPackIds { get; init; } = [];
    public bool VocabularyBoostingEnabled { get; init; }

    // Onboarding
    public bool HasCompletedOnboarding { get; init; }

    public string? DefaultLlmProvider { get; init; }

    // Plugin state
    public Dictionary<string, bool> PluginEnabledState { get; init; } = new();
    public bool PluginFirstRunCompleted { get; init; }

    // Model auto-unload (0 = disabled)
    public int ModelAutoUnloadSeconds { get; init; }

    // History
    public bool SaveToHistoryEnabled { get; init; } = true;

    // Spoken feedback (TTS readback after transcription)
    public bool SpokenFeedbackEnabled { get; init; }

    // Memory extraction
    public bool MemoryEnabled { get; init; }

    // UI Language (null = auto-detect from system)
    public string? UiLanguage { get; init; }

    // Update channel preference (null = infer from installed version)
    public string? UpdateChannel { get; init; }

    public static AppSettings Default => new();
}

public enum RecordingMode
{
    Toggle,
    PushToTalk,
    Hybrid
}

public enum HistoryRetentionMode
{
    Duration,
    Forever,
    UntilAppCloses
}

public enum OverlayPosition
{
    Top,
    Bottom
}

public enum OverlayWidget
{
    None,
    Indicator,
    Timer,
    Waveform,
    Clock,
    Profile,
    HotkeyMode,
    AppName
}
