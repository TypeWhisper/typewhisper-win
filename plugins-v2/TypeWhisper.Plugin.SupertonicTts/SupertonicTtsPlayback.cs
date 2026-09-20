using TypeWhisper.PluginSDK;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TypeWhisper.Plugin.SupertonicTts;

internal sealed class SupertonicTtsPlaybackSession : ITtsPlaybackSession, IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiOut _player;
    private readonly RawSourceWaveStream _stream;
    private int _completed;
    public bool IsActive => Volatile.Read(ref _completed) == 0;
    public string? Error { get; private set; }
    public event EventHandler? Completed;

    public SupertonicTtsPlaybackSession(float[] samples, int sampleRate, string? outputDeviceId = null)
    {
        using var devices = new MMDeviceEnumerator();
        _device = string.IsNullOrEmpty(outputDeviceId)
            ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : devices.GetDevice(outputDeviceId);
        _stream = new RawSourceWaveStream(new MemoryStream(ToPcm(samples), writable: false), new WaveFormat(sampleRate, 16, 1));
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
    private static byte[] ToPcm(float[] samples)
    {
        var pcm = new byte[checked(samples.Length * 2)];
        for (int i = 0; i < samples.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue));
        return pcm;
    }
}

internal sealed class SupertonicInactiveTtsPlaybackSession : TypeWhisper.PluginSDK.ITtsPlaybackSession
{
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static SupertonicInactiveTtsPlaybackSession Instance { get; } = new();

    private SupertonicInactiveTtsPlaybackSession()
    {
    }

    /// <summary>
    /// Gets whether this item is currently active.
    /// </summary>
    public bool IsActive => false;

    /// <summary>
    /// Raised when playback or the asynchronous operation completes.
    /// </summary>
    public event EventHandler? Completed
    {
        add { value?.Invoke(this, EventArgs.Empty); }
        remove { }
    }

    /// <summary>
    /// Stops the service or session.
    /// </summary>
    public void Stop()
    {
    }
}
