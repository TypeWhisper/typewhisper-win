using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.WinUI;

// One acquisition/release pair per recording, including cancellation and failure.
internal sealed class RecordingAudioEffects(IAudioDuckingService ducking, IMediaPauseService media)
{
    private bool _active;
    private bool _pause;
    internal void Begin(DictationAudioPreferences preferences)
    {
        if (_active) return;
        _active = true;
        _pause = preferences.PauseMediaDuringRecording;
        try
        {
            if (preferences.AudioDuckingEnabled) ducking.DuckAudio(preferences.Validated().AudioDuckingLevel);
            if (_pause) media.PauseMedia();
        }
        catch { End(); throw; }
    }

    internal void End()
    {
        if (!_active) return;
        _active = false;
        try { ducking.RestoreAudio(); }
        finally { if (_pause) media.ResumeMedia(); }
    }
}
