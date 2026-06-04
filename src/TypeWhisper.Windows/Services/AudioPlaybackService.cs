using System.IO;
using NAudio.Wave;
using TypeWhisper.Core;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Plays back audio files from the history audio directory.
/// Uses NAudio for WAV playback with play/pause/stop controls.
/// </summary>
public sealed class AudioPlaybackService : IDisposable
{
    private WaveOutEvent? _waveOut;
    private WaveFileReader? _reader;
    private string? _currentFile;
    private System.Timers.Timer? _progressTimer;

    /// <summary>
    /// Gets whether is playing.
    /// </summary>
    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;
    /// <summary>
    /// Gets whether is paused.
    /// </summary>
    public bool IsPaused => _waveOut?.PlaybackState == PlaybackState.Paused;
    /// <summary>
    /// Gets the current position seconds.
    /// </summary>
    public double CurrentPositionSeconds => _reader?.CurrentTime.TotalSeconds ?? 0;
    /// <summary>
    /// Gets the total duration seconds.
    /// </summary>
    public double TotalDurationSeconds => _reader?.TotalTime.TotalSeconds ?? 0;

    /// <summary>
    /// Raised when progress changes.
    /// </summary>
    public event Action<double, double>? ProgressChanged; // current, total
    /// <summary>
    /// Raised when playback stopped.
    /// </summary>
    public event Action? PlaybackStopped;

    /// <summary>
    /// Starts playing the audio file with the given filename from the audio directory.
    /// If the same file is already playing, toggles pause/resume.
    /// </summary>
    public void Play(string audioFileName)
    {
        var path = Path.Combine(TypeWhisperEnvironment.AudioPath, audioFileName);
        if (!File.Exists(path)) return;

        // Toggle pause/resume for same file
        if (_currentFile == audioFileName && _waveOut is not null)
        {
            if (_waveOut.PlaybackState == PlaybackState.Playing)
            {
                _waveOut.Pause();
                return;
            }
            if (_waveOut.PlaybackState == PlaybackState.Paused)
            {
                _waveOut.Play();
                return;
            }
        }

        Stop();

        _reader = new WaveFileReader(path);
        _waveOut = new WaveOutEvent();
        _waveOut.Init(_reader);
        _waveOut.PlaybackStopped += (_, _) =>
        {
            _progressTimer?.Stop();
            PlaybackStopped?.Invoke();
        };

        _currentFile = audioFileName;

        _progressTimer = new System.Timers.Timer(100);
        _progressTimer.Elapsed += (_, _) =>
            ProgressChanged?.Invoke(CurrentPositionSeconds, TotalDurationSeconds);
        _progressTimer.Start();

        _waveOut.Play();
    }

    /// <summary>
    /// Stops the service or session.
    /// </summary>
    public void Stop()
    {
        _progressTimer?.Stop();
        _progressTimer?.Dispose();
        _progressTimer = null;

        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        _reader?.Dispose();
        _reader = null;

        _currentFile = null;
    }

    /// <summary>
    /// Performs seek.
    /// </summary>
    public void Seek(double seconds)
    {
        if (_reader is null) return;
        _reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, TotalDurationSeconds));
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        Stop();
    }
}
