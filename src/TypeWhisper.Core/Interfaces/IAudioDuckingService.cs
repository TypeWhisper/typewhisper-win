namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Defines the audio ducking service contract.
/// </summary>
public interface IAudioDuckingService
{
    /// <summary>
    /// Ducks audio.
    /// </summary>
    void DuckAudio(float factor);
    /// <summary>
    /// Restores audio.
    /// </summary>
    void RestoreAudio();
}
