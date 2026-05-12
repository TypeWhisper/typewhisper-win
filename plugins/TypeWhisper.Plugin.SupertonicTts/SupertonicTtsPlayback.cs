using System.Buffers.Binary;
using System.IO;
using System.Media;

namespace TypeWhisper.Plugin.SupertonicTts;

internal sealed class SupertonicTtsPlaybackSession : TypeWhisper.PluginSDK.ITtsPlaybackSession, IDisposable
{
    private readonly SoundPlayer _player;
    private readonly MemoryStream _stream;
    private int _completed;

    public SupertonicTtsPlaybackSession(float[] samples, int sampleRate)
    {
        _stream = new MemoryStream(BuildWav(samples, sampleRate));
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

    public void Dispose() => Stop();

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

    private static byte[] BuildWav(float[] samples, int sampleRate)
    {
        var dataLength = samples.Length * 2;
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

        var destination = buffer.AsSpan(44);
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Max(-1.0f, Math.Min(1.0f, samples[i]));
            var sample = (short)Math.Round(clamped * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(i * 2, 2), sample);
        }

        return buffer;
    }
}
internal sealed class SupertonicInactiveTtsPlaybackSession : TypeWhisper.PluginSDK.ITtsPlaybackSession
{
    public static SupertonicInactiveTtsPlaybackSession Instance { get; } = new();

    private SupertonicInactiveTtsPlaybackSession()
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
