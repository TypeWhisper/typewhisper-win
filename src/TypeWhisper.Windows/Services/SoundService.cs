using System.IO;
using NAudio.Wave;

namespace TypeWhisper.Windows.Services;

public sealed class SoundService
{
    private static readonly string SoundsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Sounds");

    private readonly byte[]? _start = LoadWav("start.wav");
    private readonly byte[]? _stop = LoadWav("stop.wav");
    private readonly byte[]? _success = LoadWav("success.wav");
    private readonly byte[]? _error = LoadWav("error.wav");

    public bool IsEnabled { get; set; } = true;

    public void PlayStartSound() => Play(_start);
    public void PlayStopSound() => Play(_stop);
    public void PlaySuccessSound() => Play(_success);
    public void PlayErrorSound() => Play(_error);

    private void Play(byte[]? wav)
    {
        if (!IsEnabled || wav is null) return;
        try
        {
            var ms = new MemoryStream(wav);
            var reader = new WaveFileReader(ms);
            var output = new WaveOutEvent();
            output.Init(reader);
            output.PlaybackStopped += (_, _) => { reader.Dispose(); output.Dispose(); };
            output.Play();
        }
        catch (Exception ex)
        {
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
