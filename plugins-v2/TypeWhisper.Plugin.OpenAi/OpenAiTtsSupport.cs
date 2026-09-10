using System.Buffers.Binary;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAi;

internal static class OpenAiTtsConfiguration
{
    internal const string ModelId = "gpt-4o-mini-tts";
    internal const string DefaultVoiceId = "marin";
    internal const int SampleRate = 24_000;

    internal static IReadOnlyList<PluginVoiceInfo> AvailableVoices { get; } =
    [
        new("alloy", "Alloy"),
        new("ash", "Ash"),
        new("ballad", "Ballad"),
        new("coral", "Coral"),
        new("echo", "Echo"),
        new("fable", "Fable"),
        new("nova", "Nova"),
        new("onyx", "Onyx"),
        new("sage", "Sage"),
        new("shimmer", "Shimmer"),
        new("verse", "Verse"),
        new("marin", "Marin"),
        new("cedar", "Cedar"),
    ];

    internal static Dictionary<string, JsonElement> CreateRequestBody(
        string text,
        string? voice,
        string? instructions)
    {
        var selectedVoice = string.IsNullOrWhiteSpace(voice) ? DefaultVoiceId : voice;
        var body = new Dictionary<string, JsonElement>
        {
            ["model"] = OpenAiJson.Element(ModelId),
            ["input"] = OpenAiJson.Element(text),
            ["voice"] = OpenAiJson.Element(selectedVoice),
            ["response_format"] = OpenAiJson.Element("pcm"),
        };

        if (!string.IsNullOrWhiteSpace(instructions))
            body["instructions"] = OpenAiJson.Element(instructions.Trim());

        return body;
    }
}

internal sealed class OpenAiPcmTtsPlaybackSession : ITtsPlaybackSession, IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiOut _player;
    private readonly RawSourceWaveStream _stream;
    private int _completed;
    public bool IsActive => Volatile.Read(ref _completed) == 0;
    public string? Error { get; private set; }
    public event EventHandler? Completed;

    public OpenAiPcmTtsPlaybackSession(byte[] pcm, int sampleRate, string? outputDeviceId = null)
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
        Error = e.Exception is null ? null : "OpenAI speech playback failed. Check the audio output.";
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

internal sealed class OpenAiInactiveTtsPlaybackSession : ITtsPlaybackSession
{
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static OpenAiInactiveTtsPlaybackSession Instance { get; } = new();

    private OpenAiInactiveTtsPlaybackSession()
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
