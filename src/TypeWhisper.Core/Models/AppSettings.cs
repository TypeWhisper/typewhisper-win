using TypeWhisper.Core.Services.Sync;

namespace TypeWhisper.Core.Models;

public record AppSettings
{
    public const string DefaultSpokenFeedbackProviderId = "windows-sapi";
    public const int MinPreviewBubbleAutoHideMilliseconds = 0;
    public const int DefaultPreviewBubbleAutoHideMilliseconds = 1500;
    public const int MaxPreviewBubbleAutoHideMilliseconds = 5000;
    public const double MinLiveTranscriptionFontSize = 10d;
    public const double DefaultLiveTranscriptionFontSize = 12d;
    public const double MaxLiveTranscriptionFontSize = 18d;
    public const string LocalModelAccelerationAuto = "auto";
    public const string LocalModelAccelerationCpu = "cpu";
    public const string LocalModelAccelerationNvidiaCuda = "nvidia-cuda";
    public const string LocalModelAccelerationAmdVulkan = "amd-vulkan";
    public const string LocalModelAccelerationAmdRocm = "amd-rocm";

    public string ToggleHotkey { get; init; } = "Ctrl+Shift+F9";
    public string PushToTalkHotkey { get; init; } = "Ctrl+Shift";
    public string ToggleOnlyHotkey { get; init; } = "";
    public string HoldOnlyHotkey { get; init; } = "";
    public string RecentTranscriptionsHotkey { get; init; } = "";
    public string CopyLastTranscriptionHotkey { get; init; } = "";
    public string WorkflowPaletteHotkey { get; init; } = "";
    public IReadOnlyList<string> MainDictationHotkeys { get; init; } = [];
    public IReadOnlyList<string> ToggleOnlyHotkeys { get; init; } = [];
    public IReadOnlyList<string> HoldOnlyHotkeys { get; init; } = [];
    public IReadOnlyList<string> RecentTranscriptionsHotkeys { get; init; } = [];
    public IReadOnlyList<string> CopyLastTranscriptionHotkeys { get; init; } = [];
    public IReadOnlyList<string> WorkflowPaletteHotkeys { get; init; } = [];
    public string Language { get; init; } = "auto";
    public bool AutoPaste { get; init; } = true;
    public RecordingMode Mode { get; init; } = RecordingMode.Toggle;
    public HistoryRetentionMode HistoryRetentionMode { get; init; } = HistoryRetentionMode.Duration;
    public int HistoryRetentionMinutes { get; init; } = 90 * 24 * 60;
    public int? SelectedMicrophoneDevice { get; init; }

    // Model
    public string? SelectedModelId { get; init; }
    public string LocalModelAcceleration { get; init; } = LocalModelAccelerationAuto;

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
    public bool TranscribeShortQuietClipsAggressively { get; init; }

    // Live transcription (streaming preview while recording)
    public bool LiveTranscriptionEnabled { get; init; } = true;
    public bool OnlineAsrBatchLiveTranscriptionEnabled { get; init; }
    public double LiveTranscriptionFontSize { get; init; } = DefaultLiveTranscriptionFontSize;

    // Silence detection
    public bool SilenceAutoStopEnabled { get; init; }
    public int SilenceAutoStopSeconds { get; init; } = 10;

    // Internal diagnostics / experimental hardening
    public bool InternalParakeetTailDiagnosticsEnabled { get; init; }
    public bool InternalParakeetTailHardeningEnabled { get; init; }

    // Overlay
    public IndicatorStyle IndicatorStyle { get; init; } = IndicatorStyle.StatusIsland;
    public OverlayPosition OverlayPosition { get; init; } = OverlayPosition.Bottom;
    public OverlayWidget OverlayLeftWidget { get; init; } = OverlayWidget.Waveform;
    public OverlayWidget OverlayRightWidget { get; init; } = OverlayWidget.Timer;
    public int PreviewBubbleAutoHideMilliseconds { get; init; } = DefaultPreviewBubbleAutoHideMilliseconds;

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
    public bool ApiServerRequiresAuthentication { get; init; }

    // Dictionary
    public string[] EnabledPackIds { get; init; } = [];
    public bool VocabularyBoostingEnabled { get; init; }
    public string SelectedIndustryPresetId { get; init; } = "general";

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
    public string SpokenFeedbackProviderId { get; init; } = DefaultSpokenFeedbackProviderId;
    public string? SpokenFeedbackVoiceId { get; init; }

    // Memory extraction
    public bool MemoryEnabled { get; init; }

    // Premium Cloud Folder Sync
    public string? CloudFolderSyncFolderPath { get; init; }
    public CloudFolderSyncState? CloudFolderSyncState { get; init; }

    // UI Language (null = auto-detect from system)
    public string? UiLanguage { get; init; }

    // Update channel preference (null = infer from installed version)
    public string? UpdateChannel { get; init; }

    public static AppSettings Default => new();

    public static int NormalizePreviewBubbleAutoHideMilliseconds(int milliseconds) =>
        Math.Clamp(
            milliseconds,
            MinPreviewBubbleAutoHideMilliseconds,
            MaxPreviewBubbleAutoHideMilliseconds);

    public static double NormalizeLiveTranscriptionFontSize(double fontSize) =>
        Math.Clamp(
            fontSize,
            MinLiveTranscriptionFontSize,
            MaxLiveTranscriptionFontSize);

    public IReadOnlyList<string> GetMainDictationHotkeys()
    {
        if (MainDictationHotkeys is { Count: > 0 })
            return CleanHotkeys(MainDictationHotkeys);

        var legacyHotkey = !string.IsNullOrWhiteSpace(PushToTalkHotkey)
            ? PushToTalkHotkey
            : ToggleHotkey;
        return CleanHotkeys([legacyHotkey]);
    }

    public IReadOnlyList<string> GetToggleOnlyHotkeys() =>
        ResolveHotkeys(ToggleOnlyHotkeys, ToggleOnlyHotkey);

    public IReadOnlyList<string> GetHoldOnlyHotkeys() =>
        ResolveHotkeys(HoldOnlyHotkeys, HoldOnlyHotkey);

    public IReadOnlyList<string> GetRecentTranscriptionsHotkeys() =>
        ResolveHotkeys(RecentTranscriptionsHotkeys, RecentTranscriptionsHotkey);

    public IReadOnlyList<string> GetCopyLastTranscriptionHotkeys() =>
        ResolveHotkeys(CopyLastTranscriptionHotkeys, CopyLastTranscriptionHotkey);

    public IReadOnlyList<string> GetWorkflowPaletteHotkeys() =>
        ResolveHotkeys(WorkflowPaletteHotkeys, WorkflowPaletteHotkey);

    public AppSettings NormalizeHotkeyLists()
    {
        var mainDictationHotkeys = GetMainDictationHotkeys();
        var toggleOnlyHotkeys = GetToggleOnlyHotkeys();
        var holdOnlyHotkeys = GetHoldOnlyHotkeys();
        var recentTranscriptionsHotkeys = GetRecentTranscriptionsHotkeys();
        var copyLastTranscriptionHotkeys = GetCopyLastTranscriptionHotkeys();
        var workflowPaletteHotkeys = GetWorkflowPaletteHotkeys();

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
            WorkflowPaletteHotkey = FirstOrEmpty(workflowPaletteHotkeys)
        };
    }

    private static IReadOnlyList<string> ResolveHotkeys(IReadOnlyList<string>? configured, string? legacyHotkey) =>
        configured is { Count: > 0 }
            ? CleanHotkeys(configured)
            : CleanHotkeys([legacyHotkey]);

    private static IReadOnlyList<string> CleanHotkeys(IEnumerable<string?> values) =>
        values.Select(static value => value?.Trim() ?? "")
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string FirstOrEmpty(IReadOnlyList<string> hotkeys) =>
        hotkeys.Count == 0 ? "" : hotkeys[0];

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

public enum IndicatorStyle
{
    StatusIsland,
    EdgeDock,
    CompactBadge
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
