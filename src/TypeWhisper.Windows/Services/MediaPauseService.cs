using System.Runtime.InteropServices;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Windows.Services;

public sealed partial class MediaPauseService : IMediaPauseService
{
    private bool _didPause;

    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    public void PauseMedia()
    {
        try
        {
            if (_didPause) return;

            SendMediaPlayPause();
            _didPause = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MediaPause pause failed: {ex.Message}");
        }
    }

    public void ResumeMedia()
    {
        if (!_didPause) return;

        try
        {
            SendMediaPlayPause();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MediaPause resume failed: {ex.Message}");
        }
        finally
        {
            _didPause = false;
        }
    }

    private static void SendMediaPlayPause()
    {
        keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_EXTENDEDKEY, 0);
        keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, 0);
    }
}
