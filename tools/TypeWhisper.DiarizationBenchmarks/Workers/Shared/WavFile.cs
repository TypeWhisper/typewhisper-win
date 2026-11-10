using System.Buffers.Binary;

namespace TypeWhisper.DiarizationBenchmarks.Workers;

/// <summary>Reads the set's 16 kHz mono 16-bit PCM WAV files as floats in [-1, 1).</summary>
internal static class WavFile
{
    public static float[] ReadMono16k(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(bytes) != 0x52494646 /* RIFF */)
        {
            throw new InvalidDataException($"{path} is not a RIFF file");
        }

        var offset = 12;
        int channels = 0, rate = 0, bits = 0, format = 0;
        while (offset + 8 <= bytes.Length)
        {
            var id = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4));
            var body = offset + 8;
            if (id == 0x666D7420 /* fmt  */)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 14));
            }
            else if (id == 0x64617461 /* data */)
            {
                if (format != 1 || channels != 1 || rate != 16000 || bits != 16)
                {
                    throw new InvalidDataException($"{path}: expected 16 kHz mono 16-bit PCM, got format {format}, {channels} ch, {rate} Hz, {bits} bit");
                }

                var count = Math.Min(size, bytes.Length - body) / 2;
                var samples = new float[count];
                for (var i = 0; i < count; i++)
                {
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + (2 * i))) / 32768f;
                }

                return samples;
            }

            offset = body + size + (size & 1);
        }

        throw new InvalidDataException($"{path} has no data chunk");
    }
}
