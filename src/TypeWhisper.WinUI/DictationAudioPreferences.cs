namespace TypeWhisper.WinUI;

internal sealed record DictationAudioPreferences
{
    public bool SoundFeedbackEnabled { get; init; } = true;
    public bool WhisperModeEnabled { get; init; }
    public bool AudioDuckingEnabled { get; init; }
    public float AudioDuckingLevel { get; init; } = 0.2f;
    public bool PauseMediaDuringRecording { get; init; }
    public bool SilenceAutoStopEnabled { get; init; }
    public int SilenceAutoStopSeconds { get; init; } = 10;
    // Shared by feedback, ducking and future TTS playback.
    public string? OutputDeviceId { get; init; }

    internal DictationAudioPreferences Validated() => this with
    {
        AudioDuckingLevel = float.IsFinite(AudioDuckingLevel) ? Math.Clamp(AudioDuckingLevel, 0, 1) : 0.2f,
        SilenceAutoStopSeconds = Math.Clamp(SilenceAutoStopSeconds, 1, 3600),
        OutputDeviceId = string.IsNullOrWhiteSpace(OutputDeviceId) ? null : OutputDeviceId
    };
}
