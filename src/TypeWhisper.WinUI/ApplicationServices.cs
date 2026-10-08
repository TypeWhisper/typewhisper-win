using Microsoft.UI.Dispatching;
using TypeWhisper.Core.Services;

namespace TypeWhisper.WinUI;

// Application composition root. MainWindow connects these services to its views and keeps
// the existing ordered shutdown: stop API admission, drain UI work, then release dictation.
internal sealed class ApplicationServices
{
    internal HistoryService History { get; }
    internal LocalDictationSession Dictation { get; }
    internal WinUIHttpApi HttpApi { get; }

    internal ApplicationServices(IntPtr owner, DispatcherQueue dispatcher)
    {
        var historyPath = WinUIProfile.DataPath("history.json");
#if DEBUG
        // Opt-in fixture uses an ephemeral history store, never the development profile's history.
        if (Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_HISTORY_FIXTURE") == "1")
            historyPath = Path.Combine(Path.GetTempPath(), "TypeWhisper-WinUI-HistoryFixture", Guid.NewGuid().ToString("N"), "history.json");
#endif
        var historyAudio = new TypeWhisper.Core.Services.HistoryAudioStore(Path.Combine(Path.GetDirectoryName(historyPath)!, "history-audio"));
        var historyService = new TypeWhisper.Core.Services.HistoryService(historyPath, audioStore: historyAudio) { ThrowOnLoadFailure = true };
#if DEBUG
        if (Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_HISTORY_FIXTURE") == "1")
        {
            var fixtureRecord = new TypeWhisper.Core.Models.TranscriptionRecord
            {
                Id = "history-ui-fixture", Timestamp = DateTime.UtcNow, SourceKind = "dictation",
                RawText = "Synthetic history test.\nSecond paragraph.",
                FinalText = "Synthetic history test.\n\nSecond paragraph for editing and export.",
                AppName = "UI test fixture", AppProcessName = "fixture", Language = "en",
                EngineUsed = "fixture", ModelUsed = "synthetic-model", TranscriptionTaskUsed = "transcribe"
            };
            if (Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_HISTORY_AUDIO_FIXTURE") == "1")
            {
                // A quiet one-second generated tone, only in the ephemeral History fixture.
                // This exercises the real store and detail actions without recording a microphone.
                var samples = Enumerable.Range(0, 16000).Select(index => (float)(0.03 * Math.Sin(2 * Math.PI * 440 * index / 16000))).ToArray();
                historyService.TryAddRecordWithAudio(fixtureRecord with
                {
                    RawText = "Synthetic history audio test.", FinalText = "Synthetic history audio test.\n\nA generated tone was saved with this entry. No microphone was recorded.",
                    DurationSeconds = 1
                }, samples, 16000, () => true);
                var missing = historyService.TryAddRecordWithAudio(fixtureRecord with
                {
                    Id = "history-missing-audio-fixture", RawText = "Missing audio test.", FinalText = "Missing audio test.\n\nThe generated test audio was removed. This transcript remains available.", DurationSeconds = 1
                }, samples, 16000, () => true);
                if (historyService.ResolveAudioPath(missing.Record.AudioFileName) is { } missingPath) File.Delete(missingPath);
            }
            else historyService.TryAddRecord(fixtureRecord);
        }
#endif
        History = historyService;
        WinUICloudSync.History = historyService;
        Dictation = new LocalDictationSession(historyService, owner);
        HttpApi = new WinUIHttpApi(Dictation, dispatcher);
    }
}
