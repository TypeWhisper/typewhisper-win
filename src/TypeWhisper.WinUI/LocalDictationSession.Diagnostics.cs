using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;
using Report = TypeWhisper.Presentation.SupportDiagnosticsReport;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    // Read the session's live choices on its UI thread. No plugin calls or recording are started.
    internal Report.ModelInfo DiagnosticModel() => new(
        Report.Identifier(ActiveProviderId), Report.Identifier(ActiveEngineId), Report.Identifier(ActiveModelId),
        IsReady, !CanChangeProvider, SupportsLiveTranscription, SupportsTranslation);

    internal int DiagnosticHistoryCount => _history.TotalRecords;

    internal Report.AudioInfo DiagnosticAudio(string microphoneAccess)
    {
        var devices = GetMicrophones();
        var preferredDevices = _microphones.Select(item => MicrophoneFailure.ResolvePriorityDevice(item, devices)).ToArray();
        var test = MicrophoneTest;
        return new(microphoneAccess, _audio.HasDevice, _audio.CaptureFailure is not null, IsRecording,
            _microphones.Count == 0 || _microphones[0].Id == "default",
            _microphones.Where((item, index) => item.Id != "default" && preferredDevices[index] is null).Count(),
            devices.Select(device =>
            {
                var priority = Array.FindIndex(preferredDevices, preferred => preferred?.DeviceNumber == device.DeviceNumber);
                return new Report.InputDeviceInfo(device.Name, device.IsDefault, priority < 0 ? null : priority + 1);
            }).ToArray(), string.IsNullOrEmpty(AudioPreferences.OutputDeviceId),
            test is null ? null : new(test.Running, test.State.ToString(), test.HasWindowsFlags, test.Error is not null));
    }

    internal Report.SettingsInfo DiagnosticSettings()
    {
        var output = OutputPreferences.Current;
        var text = TextPreferences.Current;
        var audio = AudioPreferences;
        var retention = HistoryRetention.Preferences.Current;
        var recovery = RecoveryPreferences.Current;
        var unavailable = new List<string>();
        foreach (var (section, error) in new (string, string?)[]
        {
            ("audio", AudioPreferencesError), ("output", OutputPreferences.Error), ("text", TextPreferences.Error),
            ("recordingMode", RecordingModePreferences.Error), ("task", TranscriptionTaskPreferences.Error),
            ("historyRetention", HistoryRetention.Error), ("recovery", RecoveryPreferences.Error),
            ("modelMemory", ModelMemoryPreferences.Error), ("diagnosticLog", AppDiagnostics.Preferences?.Error)
        })
            if (error is not null) unavailable.Add(section);

        return new(RecordingModePreferences.Current.ToString(), Report.Identifier(Language),
            TranscriptionTaskPreferences.Current.ToString(), LivePreviewEnabled,
            output.AutoPaste, output.LockPasteToFocusedField, output.SaveToHistory, output.SaveHistoryAudio,
            retention.HistoryRetentionMode.ToString(), retention.HistoryRetentionMinutes,
            recovery.Enabled, recovery.RetentionDays, ModelMemoryPreferences.AutoUnloadSeconds,
            audio.WhisperModeEnabled, audio.AudioDuckingEnabled, audio.AudioDuckingLevel,
            audio.PauseMediaDuringRecording, audio.SoundFeedbackEnabled, audio.SpokenFeedbackEnabled,
            audio.SilenceAutoStopEnabled, audio.SilenceAutoStopSeconds, text.TranscribeShortQuietClipsAggressively,
            text.TranscriptionNumberNormalizationEnabled, text.ShortUtterancePunctuationEnabled,
            text.AppFormattingEnabled, unavailable);
    }
}
