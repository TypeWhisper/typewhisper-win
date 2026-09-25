using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>Converts 1.0 preferences into an unpublished 1.1 profile. The original settings are never rewritten.</summary>
public static class LegacyApplicationSettings
{
    /// <summary>Reads the plugin/model identity used by 1.0 without guessing a replacement provider.</summary>
    public static (string PluginId, string ModelId)? SelectedModel(AppSettings settings)
    {
        var parts = settings.SelectedModelId?.Split(':', 3);
        return parts is ["plugin", var plugin, var model] && plugin.Length > 0 && model.Length > 0 ? (plugin, model) : null;
    }

    /// <summary>Writes only known equivalents; unsupported preferences remain in the preserved 1.0 profile.</summary>
    public static void Write(string destination, AppSettings settings)
    {
        Directory.CreateDirectory(destination);
        string PathFor(string name) => Path.Combine(destination, name);
        void Json(string name, object value) => File.WriteAllText(PathFor(name), JsonSerializer.Serialize(value));
        // 1.0 named the comma key ","; 1.1 separates shortcuts with commas and calls it "Comma".
        void Shortcuts(string name, IEnumerable<string> values) => File.WriteAllText(PathFor(name),
            string.Join(",", values.Select(value => value.EndsWith(',') ? value[..^1] + "Comma" : value)));
        void Check(string? error) { if (error is not null) throw new InvalidDataException(error); }

        Check(new DictationOutputPreferencesStore(PathFor("dictation-output.json")).Save(new()
        { AutoPaste = settings.AutoPaste, SaveToHistory = settings.SaveToHistoryEnabled, LockPasteToFocusedField = settings.LockPasteToFocusedField }));
        Check(new DictationTextPreferencesStore(PathFor("dictation-text.json")).Save(new()
        {
            PreferredLanguageHints = string.Join(",", settings.GetLanguageHints()),
            TranscribeShortQuietClipsAggressively = settings.TranscribeShortQuietClipsAggressively,
            TranscriptionNumberNormalizationEnabled = settings.TranscriptionNumberNormalizationEnabled,
            ShortUtterancePunctuationEnabled = settings.ShortUtterancePunctuationEnabled,
            EnglishOutputVariant = settings.EnglishOutputVariant, GermanOutputVariant = settings.GermanOutputVariant,
            SpokenFormattingProfiles = settings.SpokenFormattingProfiles
        }));
        var mode = settings.Mode.ToString() switch
        {
            "Toggle" => RecordingMode.Toggle, "PushToTalk" => RecordingMode.Hold, "Hybrid" => RecordingMode.Hybrid,
            _ => throw new InvalidDataException("Unknown legacy recording mode.")
        };
        Check(new RecordingModePreferencesStore(PathFor("recording-mode.json")).Save(mode));
        Check(new TranscriptionTaskPreferencesStore(PathFor("transcription-task.json")).Save(
            settings.TranscriptionTask == "translate" ? TranscriptionTask.Translate : TranscriptionTask.Transcribe));
        Check(new HistoryRetentionPreferencesStore(PathFor("history-retention.json")).Save(new(settings.HistoryRetentionMode, settings.HistoryRetentionMinutes)));
        Check(new RecorderPreferencesStore(PathFor("recorder.json")).Save(new()
        { MicrophoneEnabled = settings.RecorderMicEnabled, SystemAudioEnabled = settings.RecorderSystemAudioEnabled, OutputDeviceId = settings.RecorderSystemAudioDeviceId }));
        Shortcuts("dictation-hotkeys.txt", settings.GetMainDictationHotkeys());
        Shortcuts("ToggleOnlyHotkeys.txt", settings.GetToggleOnlyHotkeys());
        Shortcuts("HoldOnlyHotkeys.txt", settings.GetHoldOnlyHotkeys());
        Shortcuts("recent-transcriptions-hotkeys.txt", settings.GetRecentTranscriptionsHotkeys());
        Shortcuts("copy-last-transcription-hotkeys.txt", settings.GetCopyLastTranscriptionHotkeys());
        Shortcuts("workflow-palette-hotkeys.txt", settings.GetWorkflowPaletteHotkeys());
        Shortcuts("recorder-hotkeys.txt", settings.GetRecorderToggleHotkeys());
        Json("microphone.json", settings.MicrophonePriorityList);
        Json("audio.json", new
        {
            settings.SoundFeedbackEnabled, settings.SpokenFeedbackEnabled, settings.SpokenFeedbackVoiceId,
            settings.WhisperModeEnabled, settings.AudioDuckingEnabled, settings.AudioDuckingLevel,
            settings.PauseMediaDuringRecording, settings.SilenceAutoStopEnabled, settings.SilenceAutoStopSeconds
        });
        Directory.CreateDirectory(PathFor("Dictation"));
        var selected = SelectedModel(settings);
        // An unavailable legacy provider stays unavailable; never silently select another cloud service.
        Json("Dictation/settings.json", new { Provider = selected?.PluginId == "com.typewhisper.sherpa-onnx" ? "local" : selected?.PluginId ?? "legacy-unavailable" });
        File.WriteAllText(PathFor("correction-learning.txt"), settings.TargetAppCorrectionLearningEnabled ? "enabled" : "disabled");
        Json("dictionary-options.json", new { Enabled = settings.VocabularyBoostingEnabled });
        // Local integrations such as Raycast depend on the API staying on; 1.1 rejects ports below 1024.
        Json("http-api.json", new
        {
            Enabled = settings.ApiServerEnabled,
            Port = settings.ApiServerPort is >= 1024 and <= 65535 ? settings.ApiServerPort : 8978,
            RequireAuthentication = settings.ApiServerRequiresAuthentication
        });
        Check(new SetupPreferencesStore(PathFor("setup.json")).Save(settings.HasCompletedOnboarding ? 4 : 0, settings.HasCompletedOnboarding));
        Json("updates.json", "Daily");
    }
}
