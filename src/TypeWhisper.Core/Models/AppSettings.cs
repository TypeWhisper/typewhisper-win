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
    public int HistoryRetentionDays { get; init; } = 90;
    public int? SelectedMicrophoneDevice { get; init; }

    // Model
    public string? SelectedModelId { get; init; }

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

    // API Server
    public bool ApiServerEnabled { get; init; }
    public int ApiServerPort { get; init; } = 9876;

    // Dictionary
    public string[] EnabledPackIds { get; init; } = [];

    // Onboarding
    public bool HasCompletedOnboarding { get; init; }

    // Prompt Palette
    public string PromptPaletteHotkey { get; init; } = "";
    public string? DefaultLlmProvider { get; init; }

    // Plugin state
    public Dictionary<string, bool> PluginEnabledState { get; init; } = new();
    public bool PluginFirstRunCompleted { get; init; }

    // Model auto-unload (0 = disabled)
    public int ModelAutoUnloadSeconds { get; init; }

    // UI Language (null = auto-detect from system)
    public string? UiLanguage { get; init; }

    public static AppSettings Default => new();
}

public enum RecordingMode
{
    Toggle,
    PushToTalk,
    Hybrid
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
