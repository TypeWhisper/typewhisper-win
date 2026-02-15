using NAudio.CoreAudioApi;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Windows.Services;

public sealed class AudioDuckingService : IAudioDuckingService
{
    private float _savedVolume;
    private bool _isDucked;

    public void DuckAudio(float factor)
    {
        try
        {
            if (_isDucked) return;

            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var volume = device.AudioEndpointVolume;

            _savedVolume = volume.MasterVolumeLevelScalar;
            volume.MasterVolumeLevelScalar = Math.Clamp(_savedVolume * factor, 0f, 1f);
            _isDucked = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AudioDucking duck failed: {ex.Message}");
        }
    }

    public void RestoreAudio()
    {
        if (!_isDucked) return;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = _savedVolume;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AudioDucking restore failed: {ex.Message}");
        }
        finally
        {
            _isDucked = false;
        }
    }
}
