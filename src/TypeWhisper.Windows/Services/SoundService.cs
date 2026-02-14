using System.IO;
using System.Media;

namespace TypeWhisper.Windows.Services;

public sealed class SoundService
{
    private static readonly string SoundsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Sounds");

    public bool IsEnabled { get; set; } = true;

    public void PlayStartSound()
    {
        if (!IsEnabled) return;
        PlaySound("start.wav");
    }

    public void PlayStopSound()
    {
        if (!IsEnabled) return;
        PlaySound("stop.wav");
    }

    public void PlaySuccessSound()
    {
        if (!IsEnabled) return;
        PlaySound("success.wav");
    }

    public void PlayErrorSound()
    {
        if (!IsEnabled) return;
        PlaySound("error.wav");
    }

    private static void PlaySound(string fileName)
    {
        try
        {
            var path = Path.Combine(SoundsPath, fileName);
            if (!File.Exists(path)) return;

            using var player = new SoundPlayer(path);
            player.Play();
        }
        catch
        {
            // Sound playback is best-effort
        }
    }
}
