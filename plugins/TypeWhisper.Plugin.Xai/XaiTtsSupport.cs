using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Media;
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
    private readonly SoundPlayer _player;
    private readonly MemoryStream _stream;
    private int _completed;

    /// <summary>
    /// Performs xai pcm tts playback session.
    /// </summary>
    public XaiPcmTtsPlaybackSession(byte[] pcm16Audio, int sampleRate)
    {
        _stream = new MemoryStream(BuildWav(pcm16Audio, sampleRate));
        _player = new SoundPlayer(_stream);
        _ = Task.Run(PlaySyncAndFinish);
    }

    /// <summary>
    /// Gets whether this item is currently active.
    /// </summary>
    public bool IsActive => Volatile.Read(ref _completed) == 0;
    /// <summary>
    /// Raised when playback or the asynchronous operation completes.
    /// </summary>
    public event EventHandler? Completed;

    /// <summary>
    /// Stops the service or session.
    /// </summary>
    public void Stop()
    {
        if (!IsActive)
            return;

        try { _player.Stop(); }
        catch (ObjectDisposedException ex)
        {
            Trace.TraceWarning($"xAI TTS playback stop skipped after disposal: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceWarning($"xAI TTS playback stop failed: {ex.Message}");
        }
        Finish();
    }

    private void PlaySyncAndFinish()
    {
        try { _player.PlaySync(); }
        catch (ObjectDisposedException ex)
        {
            Trace.TraceWarning($"xAI TTS playback stopped after disposal: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Trace.TraceWarning($"xAI TTS playback failed: {ex.Message}");
        }
        finally { Finish(); }
    }

    private void Finish()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        _player.Dispose();
        _stream.Dispose();
        Completed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose() => Stop();

    private static byte[] BuildWav(byte[] pcm16Audio, int sampleRate)
    {
        var dataLength = pcm16Audio.Length;
        var buffer = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(buffer);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), 36 + dataLength);
        "WAVE"u8.CopyTo(buffer.AsSpan(8));
        "fmt "u8.CopyTo(buffer.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28), sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(34), 16);
        "data"u8.CopyTo(buffer.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(40), dataLength);
        pcm16Audio.CopyTo(buffer.AsSpan(44));
        return buffer;
    }
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
