using TypeWhisper.PluginSDK;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TypeWhisper.Plugin.SmallestAi;

internal sealed class SmallestAiPlaybackSession : ITtsPlaybackSession, IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiOut _player;
    private readonly WaveFileReader _stream;
    private int _completed;
    public bool IsActive => Volatile.Read(ref _completed) == 0;
    public string? Error { get; private set; }
    public event EventHandler? Completed;

    public SmallestAiPlaybackSession(byte[] audio, string? outputDeviceId = null)
    {
        _stream = new WaveFileReader(new MemoryStream(audio, writable: false));
        using var devices = new MMDeviceEnumerator();
        try { _device = string.IsNullOrEmpty(outputDeviceId)
            ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : devices.GetDevice(outputDeviceId); }
        catch { _stream.Dispose(); throw; }
        WasapiOut? player = null;
        try
        {
            player = new WasapiOut(_device, AudioClientShareMode.Shared, true, 100);
            _player = player;
            _player.PlaybackStopped += OnStopped;
            _player.Init(_stream); _player.Play();
        }
        catch
        {
            if (player is not null) { player.PlaybackStopped -= OnStopped; player.Dispose(); }
            _stream.Dispose(); _device.Dispose(); throw;
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        Error = e.Exception is null ? null : "Speech playback failed. Check the audio output.";
        if (Interlocked.Exchange(ref _completed, 1) == 0) Completed?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        // WasapiOut.Stop joins its playback thread before returning.
        _player.Stop();
        if (Interlocked.Exchange(ref _completed, 1) == 0) Completed?.Invoke(this, EventArgs.Empty);
    }

    private int _disposed;
    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _player.PlaybackStopped -= OnStopped;
        _player.Dispose(); _stream.Dispose(); _device.Dispose();
    }
    public void Dispose() { if (Volatile.Read(ref _disposed) != 0) return; Stop(); DisposeResources(); }
}
