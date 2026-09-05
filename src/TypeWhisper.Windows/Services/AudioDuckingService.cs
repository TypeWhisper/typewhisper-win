using NAudio.CoreAudioApi;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Provides audio ducking service behavior.
/// </summary>
public sealed class AudioDuckingService : IAudioDuckingService
{
    private float _savedVolume;
    private bool _isDucked;
    private string? _duckedDeviceId;
    private float _duckedVolume;
    public string? OutputDeviceId { get; set; }

    /// <summary>
    /// Ducks audio.
    /// </summary>
    public void DuckAudio(float factor)
    {
        try
        {
            if (_isDucked) return;

            using var enumerator = new MMDeviceEnumerator();
            using var device = string.IsNullOrEmpty(OutputDeviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(OutputDeviceId);
            var volume = device.AudioEndpointVolume;

            _savedVolume = volume.MasterVolumeLevelScalar;
            _duckedVolume = Math.Clamp(_savedVolume * factor, 0f, 1f);
            _duckedDeviceId = device.ID;
            volume.MasterVolumeLevelScalar = _duckedVolume;
            _isDucked = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AudioDucking duck failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Restores audio.
    /// </summary>
    public void RestoreAudio()
    {
        if (!_isDucked) return;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(_duckedDeviceId!);
            // Respect volume adjustments made by the user during recording.
            if (Math.Abs(device.AudioEndpointVolume.MasterVolumeLevelScalar - _duckedVolume) < 0.001f)
                device.AudioEndpointVolume.MasterVolumeLevelScalar = _savedVolume;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AudioDucking restore failed: {ex.Message}");
        }
        finally
        {
            _isDucked = false;
            _duckedDeviceId = null;
        }
    }
}
