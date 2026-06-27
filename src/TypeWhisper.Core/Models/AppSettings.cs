using TypeWhisper.Core.Services.Sync;

namespace TypeWhisper.Core.Models;

/// <summary>
/// Represents app settings data.
/// </summary>
public record AppSettings
{
    /// <summary>
    /// Defines the default spoken feedback provider id constant.
    /// </summary>
    public const string DefaultSpokenFeedbackProviderId = "windows-sapi";
    /// <summary>
    /// Defines the min preview bubble auto hide milliseconds constant.
    /// </summary>
    public const int MinPreviewBubbleAutoHideMilliseconds = 0;
    /// <summary>
    /// Defines the default preview bubble auto hide milliseconds constant.
    /// </summary>
    public const int DefaultPreviewBubbleAutoHideMilliseconds = 1500;
    /// <summary>
    /// Defines the max preview bubble auto hide milliseconds constant.
    /// </summary>
    public const int MaxPreviewBubbleAutoHideMilliseconds = 5000;
    /// <summary>
    /// Defines the min live transcription font size constant.
    /// </summary>
    public const double MinLiveTranscriptionFontSize = 10d;
    /// <summary>
    /// Defines the default live transcription font size constant.
    /// </summary>
    public const double DefaultLiveTranscriptionFontSize = 12d;
    /// <summary>
    /// Defines the max live transcription font size constant.
    /// </summary>
    public const double MaxLiveTranscriptionFontSize = 18d;
    /// <summary>
    /// Defines the local model acceleration auto constant.
    /// </summary>
    public const string LocalModelAccelerationAuto = "auto";
    /// <summary>
    /// Defines the local model acceleration cpu constant.
    /// </summary>
    public const string LocalModelAccelerationCpu = "cpu";
    /// <summary>
    /// Defines the local model acceleration nvidia cuda constant.
    /// </summary>
    public const string LocalModelAccelerationNvidiaCuda = "nvidia-cuda";
    /// <summary>
    /// Defines the local model acceleration amd vulkan constant.
    /// </summary>
    public const string LocalModelAccelerationAmdVulkan = "amd-vulkan";
    /// <summary>
    /// Defines the local model acceleration amd rocm constant.
    /// </summary>
    public const string LocalModelAccelerationAmdRocm = "amd-rocm";

    /// <summary>
    /// Gets or sets the toggle hotkey value.
    /// </summary>
    public string ToggleHotkey { get; init; } = "Ctrl+Shift+F9";
    /// <summary>
    /// Gets or sets the push to talk hotkey value.
    /// </summary>
    public string PushToTalkHotkey { get; init; } = "Ctrl+Shift";
    /// <summary>
    /// Gets or sets the toggle only hotkey value.
    /// </summary>
    public string ToggleOnlyHotkey { get; init; } = "";
    /// <summary>
    /// Gets or sets the hold only hotkey value.
    /// </summary>
    public string HoldOnlyHotkey { get; init; } = "";
    /// <summary>
    /// Gets or sets the recent transcriptions hotkey value.
    /// </summary>
    public string RecentTranscriptionsHotkey { get; init; } = "";
    /// <summary>
    /// Gets or sets the copy last transcription hotkey value.
    /// </summary>
    public string CopyLastTranscriptionHotkey { get; init; } = "";
    /// <summary>
    /// Gets or sets the workflow palette hotkey value.
    /// </summary>
    public string WorkflowPaletteHotkey { get; init; } = "";
    /// <summary>
    /// Gets or sets the recorder toggle hotkey value.
    /// </summary>
    public string RecorderToggleHotkey { get; init; } = "";

    /// <summary>
    /// Gets or sets the main dictation hotkeys in priority order.
    /// </summary>
    public IReadOnlyList<string> MainDictationHotkeys { get; init; } = [];

    /// <summary>
    /// Gets or sets hotkeys that always use toggle recording mode.
    /// </summary>
    public IReadOnlyList<string> ToggleOnlyHotkeys { get; init; } = [];

    /// <summary>
    /// Gets or sets hotkeys that always use hold-to-record mode.
    /// </summary>
    public IReadOnlyList<string> HoldOnlyHotkeys { get; init; } = [];

    /// <summary>
    /// Gets or sets hotkeys that open the recent transcriptions palette.
    /// </summary>
    public IReadOnlyList<string> RecentTranscriptionsHotkeys { get; init; } = [];

    /// <summary>
    /// Gets or sets hotkeys that copy the most recent transcription to the clipboard.
    /// </summary>
    public IReadOnlyList<string> CopyLastTranscriptionHotkeys { get; init; } = [];

    /// <summary>
    /// Gets or sets hotkeys that open the workflow palette.
    /// </summary>
    public IReadOnlyList<string> WorkflowPaletteHotkeys { get; init; } = [];
    /// <summary>
    /// Gets or sets hotkeys that toggle the audio recorder.
    /// </summary>
    public IReadOnlyList<string> RecorderToggleHotkeys { get; init; } = [];

    /// <summary>
    /// Gets or sets the transcription language selection.
    /// </summary>
    public string Language { get; init; } = "auto";
    /// <summary>
    /// Gets or sets the auto paste value.
    /// </summary>
    public bool AutoPaste { get; init; } = true;
    /// <summary>
    /// Gets or sets the mode value.
    /// </summary>
    public RecordingMode Mode { get; init; } = RecordingMode.Toggle;
    /// <summary>
    /// Gets or sets the history retention mode value.
    /// </summary>
    public HistoryRetentionMode HistoryRetentionMode { get; init; } = HistoryRetentionMode.Duration;
    /// <summary>
    /// Gets or sets the history retention minutes value.
    /// </summary>
    public int HistoryRetentionMinutes { get; init; } = 90 * 24 * 60;
    /// <summary>
    /// Gets or sets the selected microphone device value.
    /// </summary>
    public int? SelectedMicrophoneDevice { get; init; }

    // Model
    /// <summary>
    /// Gets or sets the selected model id value.
    /// </summary>
    public string? SelectedModelId { get; init; }
    /// <summary>
    /// Gets or sets the local model acceleration value.
    /// </summary>
    public string LocalModelAcceleration { get; init; } = LocalModelAccelerationAuto;
    /// <summary>
    /// Gets or sets the custom storage path for large local model assets.
    /// </summary>
    public string? LocalModelStoragePath { get; init; }

    // Manual file transcription
    /// <summary>
    /// Gets or sets the file transcription engine override value.
    /// </summary>
    public string? FileTranscriptionEngineOverride { get; init; }
    /// <summary>
    /// Gets or sets the file transcription model override value.
    /// </summary>
    public string? FileTranscriptionModelOverride { get; init; }

    // Recorder
    /// <summary>
    /// Gets or sets whether recorder microphone capture is enabled.
    /// </summary>
    public bool RecorderMicEnabled { get; init; } = true;
    /// <summary>
    /// Gets or sets whether recorder system-audio capture is enabled.
    /// </summary>
    public bool RecorderSystemAudioEnabled { get; init; }
    /// <summary>
    /// Gets or sets the recorder system-audio output device id value.
    /// </summary>
    public string? RecorderSystemAudioDeviceId { get; init; }
    /// <summary>
    /// Gets or sets the recorder output format value.
    /// </summary>
    public string RecorderOutputFormat { get; init; } = "wav";
    /// <summary>
    /// Gets or sets the recorder track mode value.
    /// </summary>
    public string RecorderTrackMode { get; init; } = "mixed";
    /// <summary>
    /// Gets or sets the recorder microphone ducking mode value.
    /// </summary>
    public string RecorderMicDuckingMode { get; init; } = "aggressive";
    /// <summary>
    /// Gets or sets whether recorder transcription is enabled.
    /// </summary>
    public bool RecorderTranscriptionEnabled { get; init; } = true;
    /// <summary>
    /// Gets or sets the recorder transcription task value.
    /// </summary>
    public string RecorderTranscriptionTask { get; init; } = "transcribe";
    /// <summary>
    /// Gets or sets the recorder translation target language value.
    /// </summary>
    public string? RecorderTranslationTargetLanguage { get; init; }
    /// <summary>
    /// Gets or sets the recorder transcription engine override value.
    /// </summary>
    public string? RecorderTranscriptionEngineOverride { get; init; }
    /// <summary>
    /// Gets or sets the recorder transcription model override value.
    /// </summary>
    public string? RecorderTranscriptionModelOverride { get; init; }

    // Cloud Provider API Keys
    /// <summary>
    /// Gets or sets the groq api key value.
    /// </summary>
    public string? GroqApiKey { get; init; }
    /// <summary>
    /// Gets or sets the open ai api key value.
    /// </summary>
    public string? OpenAiApiKey { get; init; }

    // Audio features
    /// <summary>
    /// Gets or sets the whisper mode enabled value.
    /// </summary>
    public bool WhisperModeEnabled { get; init; }
    /// <summary>
    /// Gets or sets the audio ducking enabled value.
    /// </summary>
    public bool AudioDuckingEnabled { get; init; }
    /// <summary>
    /// Gets or sets the audio ducking level value.
    /// </summary>
    public float AudioDuckingLevel { get; init; } = 0.2f;
    /// <summary>
    /// Gets or sets the pause media during recording value.
    /// </summary>
    public bool PauseMediaDuringRecording { get; init; }
    /// <summary>
    /// Gets or sets the sound feedback enabled value.
    /// </summary>
    public bool SoundFeedbackEnabled { get; init; } = true;
    /// <summary>
    /// Gets or sets the transcribe short quiet clips aggressively value.
    /// </summary>
    public bool TranscribeShortQuietClipsAggressively { get; init; }
    /// <summary>
    /// Gets or sets whether spoken number words are normalized in transcription output.
    /// </summary>
    public bool TranscriptionNumberNormalizationEnabled { get; init; } = true;

    // Live transcription (streaming preview while recording)
    /// <summary>
    /// Gets or sets the live transcription enabled value.
    /// </summary>
    public bool LiveTranscriptionEnabled { get; init; } = true;
    /// <summary>
    /// Gets or sets the online asr batch live transcription enabled value.
    /// </summary>
    public bool OnlineAsrBatchLiveTranscriptionEnabled { get; init; }
    /// <summary>
    /// Gets or sets the live transcription font size value.
    /// </summary>
    public double LiveTranscriptionFontSize { get; init; } = DefaultLiveTranscriptionFontSize;

    // Silence detection
    /// <summary>
    /// Gets or sets the silence auto stop enabled value.
    /// </summary>
    public bool SilenceAutoStopEnabled { get; init; }
    /// <summary>
    /// Gets or sets the silence auto stop seconds value.
    /// </summary>
    public int SilenceAutoStopSeconds { get; init; } = 10;

    // Internal diagnostics / experimental hardening
    /// <summary>
    /// Gets or sets the internal parakeet tail diagnostics enabled value.
    /// </summary>
    public bool InternalParakeetTailDiagnosticsEnabled { get; init; }
    /// <summary>
    /// Gets or sets the internal parakeet tail hardening enabled value.
    /// </summary>
    public bool InternalParakeetTailHardeningEnabled { get; init; }

    // Overlay
    /// <summary>
    /// Gets or sets the indicator style value.
    /// </summary>
    public IndicatorStyle IndicatorStyle { get; init; } = IndicatorStyle.StatusIsland;
    /// <summary>
    /// Gets or sets the overlay position value.
    /// </summary>
    public OverlayPosition OverlayPosition { get; init; } = OverlayPosition.Bottom;
    /// <summary>
    /// Gets or sets the overlay left widget value.
    /// </summary>
    public OverlayWidget OverlayLeftWidget { get; init; } = OverlayWidget.Waveform;
    /// <summary>
    /// Gets or sets the overlay right widget value.
    /// </summary>
    public OverlayWidget OverlayRightWidget { get; init; } = OverlayWidget.Timer;
    /// <summary>
    /// Gets or sets the preview bubble auto hide milliseconds value.
    /// </summary>
    public int PreviewBubbleAutoHideMilliseconds { get; init; } = DefaultPreviewBubbleAutoHideMilliseconds;

    // Translation
    /// <summary>
    /// Gets or sets the transcription task value.
    /// </summary>
    public string TranscriptionTask { get; init; } = "transcribe";
    /// <summary>
    /// Gets or sets the translation target language value.
    /// </summary>
    public string? TranslationTargetLanguage { get; init; }
    /// <summary>
    /// Gets or sets the last selected quick translation target language value.
    /// </summary>
    public string? LastTranslationTargetLanguage { get; init; }

    // Watch folder automation
    /// <summary>
    /// Gets or sets the watch folder path value.
    /// </summary>
    public string? WatchFolderPath { get; init; }
    /// <summary>
    /// Gets or sets the watch folder output path value.
    /// </summary>
    public string? WatchFolderOutputPath { get; init; }
    /// <summary>
    /// Gets or sets the watch folder output format value.
    /// </summary>
    public string WatchFolderOutputFormat { get; init; } = "md";
    /// <summary>
    /// Gets or sets the watch folder auto start value.
    /// </summary>
    public bool WatchFolderAutoStart { get; init; }
    /// <summary>
    /// Gets or sets the watch folder delete source value.
    /// </summary>
    public bool WatchFolderDeleteSource { get; init; }
    /// <summary>
    /// Gets or sets the watch folder language value.
    /// </summary>
    public string WatchFolderLanguage { get; init; } = "auto";
    /// <summary>
    /// Gets or sets the watch folder engine override value.
    /// </summary>
    public string? WatchFolderEngineOverride { get; init; }
    /// <summary>
    /// Gets or sets the watch folder model override value.
    /// </summary>
    public string? WatchFolderModelOverride { get; init; }

    // API Server
    /// <summary>
    /// Gets or sets the api server enabled value.
    /// </summary>
    public bool ApiServerEnabled { get; init; }
    /// <summary>
    /// Gets or sets the api server port value.
    /// </summary>
    public int ApiServerPort { get; init; } = 8978;
    /// <summary>
    /// Gets or sets the api server requires authentication value.
    /// </summary>
    public bool ApiServerRequiresAuthentication { get; init; }

    // Dictionary
    /// <summary>
    /// Gets or sets the enabled pack ids value.
    /// </summary>
    public string[] EnabledPackIds { get; init; } = [];
    /// <summary>
    /// Gets or sets the vocabulary boosting enabled value.
    /// </summary>
    public bool VocabularyBoostingEnabled { get; init; }
    /// <summary>
    /// Gets or sets the selected industry preset id value.
    /// </summary>
    public string SelectedIndustryPresetId { get; init; } = "general";

    // Onboarding
    /// <summary>
    /// Gets or sets the has completed onboarding value.
    /// </summary>
    public bool HasCompletedOnboarding { get; init; }

    /// <summary>
    /// Gets or sets the default llm provider value.
    /// </summary>
    public string? DefaultLlmProvider { get; init; }

    // Plugin state
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public Dictionary<string, bool> PluginEnabledState { get; init; } = new();
    /// <summary>
    /// Gets or sets the plugin first run completed value.
    /// </summary>
    public bool PluginFirstRunCompleted { get; init; }

    // Model auto-unload (0 = disabled)
    /// <summary>
    /// Gets or sets the model auto unload seconds value.
    /// </summary>
    public int ModelAutoUnloadSeconds { get; init; }

    // History
    /// <summary>
    /// Gets or sets the save to history enabled value.
    /// </summary>
    public bool SaveToHistoryEnabled { get; init; } = true;

    // Spoken feedback (TTS readback after transcription)
    /// <summary>
    /// Gets or sets the spoken feedback enabled value.
    /// </summary>
    public bool SpokenFeedbackEnabled { get; init; }
    /// <summary>
    /// Gets or sets the spoken feedback provider id value.
    /// </summary>
    public string SpokenFeedbackProviderId { get; init; } = DefaultSpokenFeedbackProviderId;
    /// <summary>
    /// Gets or sets the spoken feedback voice id value.
    /// </summary>
    public string? SpokenFeedbackVoiceId { get; init; }

    // Memory extraction
    /// <summary>
    /// Gets or sets the memory enabled value.
    /// </summary>
    public bool MemoryEnabled { get; init; }

    // Premium Cloud Folder Sync
    /// <summary>
    /// Gets or sets whether Premium target-app correction learning is enabled.
    /// </summary>
    public bool TargetAppCorrectionLearningEnabled { get; init; } = true;
    /// <summary>
    /// Gets or sets the cloud folder sync folder path value.
    /// </summary>
    public string? CloudFolderSyncFolderPath { get; init; }
    /// <summary>
    /// Gets or sets the cloud folder sync state value.
    /// </summary>
    public CloudFolderSyncState? CloudFolderSyncState { get; init; }

    // UI Language (null = auto-detect from system)
    /// <summary>
    /// Gets or sets the ui language value.
    /// </summary>
    public string? UiLanguage { get; init; }

    // Update channel preference (null = infer from installed version)
    /// <summary>
    /// Gets or sets the update channel value.
    /// </summary>
    public string? UpdateChannel { get; init; }

    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static AppSettings Default => new();

    /// <summary>
    /// Performs normalize preview bubble auto hide milliseconds.
    /// </summary>
    public static int NormalizePreviewBubbleAutoHideMilliseconds(int milliseconds) =>
        Math.Clamp(
            milliseconds,
            MinPreviewBubbleAutoHideMilliseconds,
            MaxPreviewBubbleAutoHideMilliseconds);

    /// <summary>
    /// Performs normalize live transcription font size.
    /// </summary>
    public static double NormalizeLiveTranscriptionFontSize(double fontSize) =>
        Math.Clamp(
            fontSize,
            MinLiveTranscriptionFontSize,
            MaxLiveTranscriptionFontSize);

    /// <summary>
    /// Returns normalized main dictation hotkeys, falling back to legacy single-hotkey settings.
    /// </summary>
    public IReadOnlyList<string> GetMainDictationHotkeys()
    {
        var configuredHotkeys = CleanHotkeys(MainDictationHotkeys);
        if (configuredHotkeys.Count > 0)
            return configuredHotkeys;

        return CleanHotkeys(ResolveLegacyMainHotkeys());
    }

    /// <summary>
    /// Returns normalized toggle-only hotkeys, falling back to the legacy setting.
    /// </summary>
    public IReadOnlyList<string> GetToggleOnlyHotkeys() =>
        ResolveHotkeys(ToggleOnlyHotkeys, ToggleOnlyHotkey);

    /// <summary>
    /// Returns normalized hold-only hotkeys, falling back to the legacy setting.
    /// </summary>
    public IReadOnlyList<string> GetHoldOnlyHotkeys() =>
        ResolveHotkeys(HoldOnlyHotkeys, HoldOnlyHotkey);

    /// <summary>
    /// Returns normalized recent-transcriptions hotkeys, falling back to the legacy setting.
    /// </summary>
    public IReadOnlyList<string> GetRecentTranscriptionsHotkeys() =>
        ResolveHotkeys(RecentTranscriptionsHotkeys, RecentTranscriptionsHotkey);

    /// <summary>
    /// Returns normalized copy-last-transcription hotkeys, falling back to the legacy setting.
    /// </summary>
    public IReadOnlyList<string> GetCopyLastTranscriptionHotkeys() =>
        ResolveHotkeys(CopyLastTranscriptionHotkeys, CopyLastTranscriptionHotkey);

    /// <summary>
    /// Returns normalized workflow-palette hotkeys, falling back to the legacy setting.
    /// </summary>
    public IReadOnlyList<string> GetWorkflowPaletteHotkeys() =>
        ResolveHotkeys(WorkflowPaletteHotkeys, WorkflowPaletteHotkey);

    /// <summary>
    /// Returns normalized recorder-toggle hotkeys, falling back to the legacy setting.
    /// </summary>
    public IReadOnlyList<string> GetRecorderToggleHotkeys() =>
        ResolveHotkeys(RecorderToggleHotkeys, RecorderToggleHotkey);

    /// <summary>
    /// Returns settings with all hotkey lists normalized and legacy single-hotkey fields synchronized.
    /// </summary>
    public AppSettings NormalizeHotkeyLists()
    {
        var mainDictationHotkeys = GetMainDictationHotkeys();
        var toggleOnlyHotkeys = GetToggleOnlyHotkeys();
        var holdOnlyHotkeys = GetHoldOnlyHotkeys();
        var recentTranscriptionsHotkeys = GetRecentTranscriptionsHotkeys();
        var copyLastTranscriptionHotkeys = GetCopyLastTranscriptionHotkeys();
        var workflowPaletteHotkeys = GetWorkflowPaletteHotkeys();
        var recorderToggleHotkeys = GetRecorderToggleHotkeys();

        return this with
        {
            MainDictationHotkeys = mainDictationHotkeys,
            ToggleHotkey = FirstOrEmpty(mainDictationHotkeys),
            PushToTalkHotkey = FirstOrEmpty(mainDictationHotkeys),
            ToggleOnlyHotkeys = toggleOnlyHotkeys,
            ToggleOnlyHotkey = FirstOrEmpty(toggleOnlyHotkeys),
            HoldOnlyHotkeys = holdOnlyHotkeys,
            HoldOnlyHotkey = FirstOrEmpty(holdOnlyHotkeys),
            RecentTranscriptionsHotkeys = recentTranscriptionsHotkeys,
            RecentTranscriptionsHotkey = FirstOrEmpty(recentTranscriptionsHotkeys),
            CopyLastTranscriptionHotkeys = copyLastTranscriptionHotkeys,
            CopyLastTranscriptionHotkey = FirstOrEmpty(copyLastTranscriptionHotkeys),
            WorkflowPaletteHotkeys = workflowPaletteHotkeys,
            WorkflowPaletteHotkey = FirstOrEmpty(workflowPaletteHotkeys),
            RecorderToggleHotkeys = recorderToggleHotkeys,
            RecorderToggleHotkey = FirstOrEmpty(recorderToggleHotkeys)
        };
    }

    private IEnumerable<string?> ResolveLegacyMainHotkeys()
    {
        var pushToTalkHotkey = HotkeyText(PushToTalkHotkey);
        var toggleHotkey = HotkeyText(ToggleHotkey);

        if (!string.IsNullOrWhiteSpace(pushToTalkHotkey))
            yield return pushToTalkHotkey;

        if (string.IsNullOrWhiteSpace(toggleHotkey))
            yield break;

        if (string.Equals(pushToTalkHotkey, toggleHotkey, StringComparison.OrdinalIgnoreCase))
            yield break;

        if (!string.IsNullOrWhiteSpace(pushToTalkHotkey)
            && string.Equals(toggleHotkey, Default.ToggleHotkey, StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        yield return toggleHotkey;
    }

    private static IReadOnlyList<string> ResolveHotkeys(IReadOnlyList<string>? configured, string? legacyHotkey)
    {
        var configuredHotkeys = CleanHotkeys(configured ?? []);
        return configuredHotkeys.Count > 0
            ? configuredHotkeys
            : CleanHotkeys([legacyHotkey]);
    }

    private static IReadOnlyList<string> CleanHotkeys(IEnumerable<string?> values) =>
        values.Select(static value => value?.Trim() ?? "")
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string FirstOrEmpty(IReadOnlyList<string> hotkeys) =>
        hotkeys.Count == 0 ? "" : hotkeys[0];

    private static string HotkeyText(string? value) =>
        value?.Trim() ?? "";

    /// <summary>
    /// Normalizes a local model acceleration storage value to a supported option.
    /// </summary>
    public static string NormalizeLocalModelAcceleration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return LocalModelAccelerationAuto;

        return value.Trim().ToLowerInvariant() switch
        {
            LocalModelAccelerationAuto => LocalModelAccelerationAuto,
            LocalModelAccelerationCpu => LocalModelAccelerationCpu,
            LocalModelAccelerationNvidiaCuda => LocalModelAccelerationNvidiaCuda,
            LocalModelAccelerationAmdVulkan => LocalModelAccelerationAmdVulkan,
            LocalModelAccelerationAmdRocm => LocalModelAccelerationAmdRocm,
            "cuda" => LocalModelAccelerationNvidiaCuda,
            "nvidia cuda" => LocalModelAccelerationNvidiaCuda,
            "nvidia_cuda" => LocalModelAccelerationNvidiaCuda,
            "vulkan" => LocalModelAccelerationAmdVulkan,
            "amd vulkan" => LocalModelAccelerationAmdVulkan,
            "amd_vulkan" => LocalModelAccelerationAmdVulkan,
            "rocm" => LocalModelAccelerationAmdRocm,
            "hip" => LocalModelAccelerationAmdRocm,
            "amd rocm" => LocalModelAccelerationAmdRocm,
            "amd_rocm" => LocalModelAccelerationAmdRocm,
            _ => LocalModelAccelerationAuto
        };
    }

    /// <summary>
    /// Normalizes a local model storage path value.
    /// </summary>
    public static string? NormalizeLocalModelStoragePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }
}

/// <summary>
/// Lists the supported recording mode values.
/// </summary>
public enum RecordingMode
{
    /// <summary>
    /// Represents the toggle option.
    /// </summary>
    Toggle,
    /// <summary>
    /// Represents the push to talk option.
    /// </summary>
    PushToTalk,
    /// <summary>
    /// Represents the hybrid option.
    /// </summary>
    Hybrid
}

/// <summary>
/// Lists the supported history retention mode values.
/// </summary>
public enum HistoryRetentionMode
{
    /// <summary>
    /// Represents the duration option.
    /// </summary>
    Duration,
    /// <summary>
    /// Represents the forever option.
    /// </summary>
    Forever,
    /// <summary>
    /// Represents the until app closes option.
    /// </summary>
    UntilAppCloses
}

/// <summary>
/// Lists the supported overlay position values.
/// </summary>
public enum OverlayPosition
{
    /// <summary>
    /// Represents the top option.
    /// </summary>
    Top,
    /// <summary>
    /// Represents the bottom option.
    /// </summary>
    Bottom
}

/// <summary>
/// Lists the supported indicator style values.
/// </summary>
public enum IndicatorStyle
{
    /// <summary>
    /// Represents the status island option.
    /// </summary>
    StatusIsland,
    /// <summary>
    /// Represents the edge dock option.
    /// </summary>
    EdgeDock,
    /// <summary>
    /// Represents the compact badge option.
    /// </summary>
    CompactBadge
}

/// <summary>
/// Lists the supported overlay widget values.
/// </summary>
public enum OverlayWidget
{
    /// <summary>
    /// Represents the none option.
    /// </summary>
    None,
    /// <summary>
    /// Represents the indicator option.
    /// </summary>
    Indicator,
    /// <summary>
    /// Represents the timer option.
    /// </summary>
    Timer,
    /// <summary>
    /// Represents the waveform option.
    /// </summary>
    Waveform,
    /// <summary>
    /// Represents the clock option.
    /// </summary>
    Clock,
    /// <summary>
    /// Represents the profile option.
    /// </summary>
    Profile,
    /// <summary>
    /// Represents the hotkey mode option.
    /// </summary>
    HotkeyMode,
    /// <summary>
    /// Represents the app name option.
    /// </summary>
    AppName
}
