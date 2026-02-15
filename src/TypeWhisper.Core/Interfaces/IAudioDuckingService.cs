namespace TypeWhisper.Core.Interfaces;

public interface IAudioDuckingService
{
    void DuckAudio(float factor);
    void RestoreAudio();
}
