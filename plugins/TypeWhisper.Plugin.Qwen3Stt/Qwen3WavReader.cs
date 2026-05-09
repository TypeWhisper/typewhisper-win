using System.Buffers.Binary;

namespace TypeWhisper.Plugin.Qwen3Stt;

internal static class Qwen3WavReader
{
    public static (float[] Samples, int SampleRate) DecodeMono(byte[] wavData)
    {
        if (wavData.Length < 44)
            throw new ArgumentException("Invalid WAV data: too short.", nameof(wavData));
        if (ReadAscii(wavData, 0, 4) != "RIFF" || ReadAscii(wavData, 8, 4) != "WAVE")
            throw new ArgumentException("Invalid WAV data: missing RIFF/WAVE header.", nameof(wavData));

        var channels = 1;
        var sampleRate = 16000;
        var bitsPerSample = 16;
        var format = 1;
        ReadOnlySpan<byte> data = default;

        var offset = 12;
        while (offset + 8 <= wavData.Length)
        {
            var chunkId = ReadAscii(wavData, offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wavData.AsSpan(offset + 4, 4));
            var chunkStart = offset + 8;
            if (chunkStart + chunkSize > wavData.Length)
                break;

            if (chunkId == "fmt ")
            {
                var span = wavData.AsSpan(chunkStart, chunkSize);
                format = BinaryPrimitives.ReadInt16LittleEndian(span[..2]);
                channels = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(14, 2));
            }
            else if (chunkId == "data")
            {
                data = wavData.AsSpan(chunkStart, chunkSize);
            }

            offset = chunkStart + chunkSize + (chunkSize % 2);
        }

        if (data.IsEmpty)
            throw new ArgumentException("Invalid WAV data: no data chunk.", nameof(wavData));
        if (format != 1 || bitsPerSample != 16)
            throw new NotSupportedException("Qwen3 ASR currently supports PCM16 WAV audio.");

        var frameCount = data.Length / (channels * 2);
        var samples = new float[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                var sampleOffset = (frame * channels + channel) * 2;
                var sample = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(sampleOffset, 2));
                sum += sample / 32768f;
            }
            samples[frame] = Math.Clamp(sum / channels, -1f, 1f);
        }

        return (sampleRate == 16000 ? samples : ResampleLinear(samples, sampleRate, 16000), 16000);
    }

    private static float[] ResampleLinear(float[] input, int sourceRate, int targetRate)
    {
        if (input.Length == 0)
            return [];

        var targetLength = Math.Max(1, (int)Math.Round(input.Length * (double)targetRate / sourceRate));
        var output = new float[targetLength];
        var ratio = (double)sourceRate / targetRate;
        for (var i = 0; i < targetLength; i++)
        {
            var position = i * ratio;
            var index = (int)Math.Floor(position);
            var fraction = position - index;
            var a = input[Math.Min(index, input.Length - 1)];
            var b = input[Math.Min(index + 1, input.Length - 1)];
            output[i] = (float)(a + (b - a) * fraction);
        }
        return output;
    }

    private static string ReadAscii(byte[] data, int offset, int count) =>
        System.Text.Encoding.ASCII.GetString(data, offset, count);
}
