using System.Buffers.Binary;

namespace TypeWhisper.Core.Audio;

public static class WavEncoder
{
    public static byte[] Encode(float[] samples, int sampleRate = 16000, int channels = 1, int bitsPerSample = 16)
    {
        var bytesPerSample = bitsPerSample / 8;
        var dataLength = samples.Length * bytesPerSample;
        var buffer = new byte[44 + dataLength];

        // RIFF header
        "RIFF"u8.CopyTo(buffer.AsSpan(0));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), 36 + dataLength);
        "WAVE"u8.CopyTo(buffer.AsSpan(8));

        // fmt sub-chunk
        "fmt "u8.CopyTo(buffer.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), 16); // sub-chunk size
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(20), 1);  // PCM format
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28), sampleRate * channels * bytesPerSample); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(32), (short)(channels * bytesPerSample)); // block align
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(34), (short)bitsPerSample);

        // data sub-chunk
        "data"u8.CopyTo(buffer.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(40), dataLength);

        // Convert float samples to Int16 PCM
        var dataSpan = buffer.AsSpan(44);
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1.0f, 1.0f);
            var pcm = (short)(clamped * 32767);
            BinaryPrimitives.WriteInt16LittleEndian(dataSpan[(i * 2)..], pcm);
        }

        return buffer;
    }
}
