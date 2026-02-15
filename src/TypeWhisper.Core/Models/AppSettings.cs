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

    // Audio features
    public bool WhisperModeEnabled { get; init; }
    public bool AudioDuckingEnabled { get; init; }
    public float AudioDuckingLevel { get; init; } = 0.2f;
    public bool PauseMediaDuringRecording { get; init; }
    public bool SoundFeedbackEnabled { get; init; } = true;

    // Silence detection
    public bool SilenceAutoStopEnabled { get; init; }
    public int SilenceAutoStopSeconds { get; init; } = 10;

    // Overlay
    public OverlayPosition OverlayPosition { get; init; } = OverlayPosition.Bottom;

    // Translation
    public string TranscriptionTask { get; init; } = "transcribe";
    public string? TranslationTargetLanguage { get; init; }

    // API Server
    public bool ApiServerEnabled { get; init; }
    public int ApiServerPort { get; init; } = 9876;

    // Dictionary
    public string[] EnabledPackIds { get; init; } = [];

    // Update
    public string? UpdateUrl { get; init; }

    // Onboarding
    public bool HasCompletedOnboarding { get; init; }

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
