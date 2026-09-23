using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Xai;

internal static class XaiTtsConfiguration
{
    internal const string DefaultVoiceId = "eve";
    internal const int SampleRate = 24_000;

    internal static IReadOnlyList<PluginVoiceInfo> FallbackVoices { get; } =
    [
        new("eve", "Eve"),
        new("ara", "Ara"),
        new("leo", "Leo"),
        new("rex", "Rex"),
        new("sal", "Sal"),
    ];

    internal static Dictionary<string, JsonElement> CreateRequestBody(
        string text,
        string? voice,
        string? language,
        bool lowLatency,
        bool textNormalization)
    {
        var selectedVoice = string.IsNullOrWhiteSpace(voice) ? DefaultVoiceId : voice.Trim();
        var selectedLanguage = string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim();

        return new Dictionary<string, JsonElement>
        {
            ["text"] = XaiJson.Element(text),
            ["voice_id"] = XaiJson.Element(selectedVoice),
            ["language"] = XaiJson.Element(selectedLanguage),
            ["output_format"] = XaiJson.Element(new
            {
                codec = "pcm",
                sample_rate = SampleRate,
            }),
            ["optimize_streaming_latency"] = XaiJson.Element(lowLatency ? 1 : 0),
            ["text_normalization"] = XaiJson.Element(textNormalization),
        };
    }
}

internal sealed class XaiPcmTtsPlaybackSession : ITtsPlaybackSession, IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiOut _player;
    private readonly RawSourceWaveStream _stream;
    private int _completed;
    public bool IsActive => Volatile.Read(ref _completed) == 0;
    public string? Error { get; private set; }
    public event EventHandler? Completed;

    public XaiPcmTtsPlaybackSession(byte[] pcm, int sampleRate, string? outputDeviceId = null)
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

internal sealed class XaiInactiveTtsPlaybackSession : ITtsPlaybackSession
{
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static XaiInactiveTtsPlaybackSession Instance { get; } = new();

    private XaiInactiveTtsPlaybackSession()
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
