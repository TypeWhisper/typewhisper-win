using System.IO;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Provides sound service behavior.
/// </summary>
public sealed class SoundService
{
    private static readonly string SoundsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Sounds");

    private readonly byte[]? _start = LoadWav("start.wav");
    private readonly byte[]? _stop = LoadWav("stop.wav");
    private readonly byte[]? _success = LoadWav("success.wav");
    private readonly byte[]? _error = LoadWav("error.wav");

    /// <summary>
    /// Gets or sets the is enabled value.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
    public string? OutputDeviceId { get; set; }

    /// <summary>
    /// Performs play start sound.
    /// </summary>
    public void PlayStartSound() => Play(_start);
    /// <summary>
    /// Performs play stop sound.
    /// </summary>
    public void PlayStopSound() => Play(_stop);
    /// <summary>
    /// Performs play success sound.
    /// </summary>
    public void PlaySuccessSound() => Play(_success);
    /// <summary>
    /// Performs play error sound.
    /// </summary>
    public void PlayErrorSound() => Play(_error);

    private void Play(byte[]? wav)
    {
        if (!IsEnabled || wav is null) return;
        MemoryStream? ms = null;
        WaveFileReader? reader = null;
        IWavePlayer? output = null;
        MMDevice? device = null;
        var cleaned = 0;
        void Cleanup()
        {
            if (Interlocked.Exchange(ref cleaned, 1) != 0) return;
            output?.Dispose(); reader?.Dispose(); ms?.Dispose(); device?.Dispose();
        }
        try
        {
            ms = new MemoryStream(wav);
            reader = new WaveFileReader(ms);
            {
                using var enumerator = new MMDeviceEnumerator();
                device = string.IsNullOrEmpty(OutputDeviceId)
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    : enumerator.GetDevice(OutputDeviceId);
                output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            }
            output.Init(reader);
            output.PlaybackStopped += (_, _) => Cleanup();
            output.Play();
        }
        catch (Exception ex)
        {
            Cleanup();
            System.Diagnostics.Debug.WriteLine($"Sound playback failed: {ex.Message}");
        }
    }

    private static byte[]? LoadWav(string fileName)
    {
        try
        {
            var path = Path.Combine(SoundsPath, fileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
