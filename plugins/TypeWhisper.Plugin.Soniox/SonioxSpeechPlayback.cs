using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Soniox;

// NAudio 3 obsoletes WasapiOut; moving to WasapiPlayer is tracked in #629.
#pragma warning disable CS0618
/// <summary>Plays Soniox's PCM16 speech through the selected output device.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class SonioxSpeechPlaybackSession : ITtsPlaybackSession, IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiOut _player;
    private readonly RawSourceWaveStream _stream;
    private int _completed;
    private int _disposed;

    public bool IsActive => Volatile.Read(ref _completed) == 0;

    public string? Error { get; private set; }

    public event EventHandler? Completed;

    public SonioxSpeechPlaybackSession(byte[] pcm, int sampleRate, string? outputDeviceId = null)
    {
        using var devices = new MMDeviceEnumerator();
        _device = string.IsNullOrEmpty(outputDeviceId)
            ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : devices.GetDevice(outputDeviceId);
        _stream = new RawSourceWaveStream(new MemoryStream(pcm, writable: false), new WaveFormat(sampleRate, 16, 1));
        WasapiOut? player = null;
        try
        {
            player = new WasapiOut(_device, AudioClientShareMode.Shared, true, 100);
            _player = player;
            _player.PlaybackStopped += OnStopped;
            _player.Init(_stream);
            _player.Play();
        }
        catch
        {
            if (player is not null)
            {
                player.PlaybackStopped -= OnStopped;
                player.Dispose();
            }

            _stream.Dispose();
            _device.Dispose();
            throw;
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        Error = e.Exception is null ? null : "Speech playback failed. Check the audio output.";
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            Completed?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        // WasapiOut.Stop joins its playback thread before returning.
        _player.Stop();
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            Completed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        Stop();
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _player.PlaybackStopped -= OnStopped;
        _player.Dispose();
        _stream.Dispose();
        _device.Dispose();
    }
}
#pragma warning restore CS0618

/// <summary>A finished session for requests without text.</summary>
internal sealed class SonioxInactivePlaybackSession : ITtsPlaybackSession
{
    public static SonioxInactivePlaybackSession Instance { get; } = new();

    private SonioxInactivePlaybackSession()
    {
    }

    public bool IsActive => false;

    public event EventHandler? Completed
    {
        add => value?.Invoke(this, EventArgs.Empty);
        remove { }
    }

    public void Stop()
    {
    }
}
