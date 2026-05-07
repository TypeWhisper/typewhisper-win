using System.Buffers.Binary;
using System.IO;
using System.Media;
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
    private readonly SoundPlayer _player;
    private readonly MemoryStream _stream;
    private int _completed;

    public OpenAiPcmTtsPlaybackSession(byte[] pcm16Audio, int sampleRate)
    {
        _stream = new MemoryStream(BuildWav(pcm16Audio, sampleRate));
        _player = new SoundPlayer(_stream);
        _ = Task.Run(PlaySyncAndFinish);
    }

    public bool IsActive => Volatile.Read(ref _completed) == 0;
    public event EventHandler? Completed;

    public void Stop()
    {
        if (!IsActive)
            return;

        try { _player.Stop(); }
        catch { }
        Finish();
    }

    private void PlaySyncAndFinish()
    {
        try { _player.PlaySync(); }
        catch { }
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

internal sealed class OpenAiInactiveTtsPlaybackSession : ITtsPlaybackSession
{
    public static OpenAiInactiveTtsPlaybackSession Instance { get; } = new();

    private OpenAiInactiveTtsPlaybackSession()
    {
    }

    public bool IsActive => false;

    public event EventHandler? Completed
    {
        add { value?.Invoke(this, EventArgs.Empty); }
        remove { }
    }

    public void Stop()
    {
    }
}
