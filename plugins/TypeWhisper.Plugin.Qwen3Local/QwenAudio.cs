using System.Buffers.Binary;

namespace TypeWhisper.Plugin.Qwen3Local;

internal static class QwenAudio
{
    internal const int SampleRate = 16000;
    internal const int ChunkSamples = 10 * SampleRate;

    internal static float[] DecodeWav(byte[] wav)
    {
        var data = wav.AsSpan();
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new ArgumentException("Expected a PCM16 mono 16 kHz WAV file.", nameof(wav));
        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        if (riffSize < 4 || (long)riffSize + 8 != data.Length) throw new ArgumentException("Invalid WAV length.", nameof(wav));
        var formatFound = false;
        float[]? samples = null;
        var position = 12;
        while (position < data.Length)
        {
            if (data.Length - position < 8) throw new ArgumentException("Truncated WAV chunk.", nameof(wav));
            var id = data.Slice(position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position + 4, 4));
            if (size > data.Length - position - 8) throw new ArgumentException("Truncated WAV chunk.", nameof(wav));
            var chunk = data.Slice(position + 8, (int)size);
            if (id.SequenceEqual("fmt "u8))
            {
                if (formatFound || size < 16 || BinaryPrimitives.ReadUInt16LittleEndian(chunk) != 1 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]) != 1 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != SampleRate ||
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..]) != 2 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]) != 16)
                    throw new ArgumentException("Expected PCM16 mono audio sampled at 16 kHz.", nameof(wav));
                formatFound = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (samples is not null || size % 2 != 0) throw new ArgumentException("Invalid WAV sample data.", nameof(wav));
                samples = new float[size / 2];
                for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(chunk[(2 * i)..]) / 32768f;
            }
            position = checked(position + 8 + (int)size + (int)(size % 2));
        }
        if (!formatFound || samples is null || position != data.Length) throw new ArgumentException("Missing or invalid WAV chunks.", nameof(wav));
        return samples;
    }

    // The 1.7B export drops whole windows of quiet speech that it decodes correctly at the recorder's level.
    // Dictation arrives already raised to this peak; files and API audio are raised the same way here.
    internal const float NormalizationTarget = 0.707f;

    internal static float NormalizationGain(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples) peak = MathF.Max(peak, MathF.Abs(sample));
        // Same rule as the recorder: near-silence stays untouched and loud audio is never attenuated.
        return peak < 0.01f || peak >= NormalizationTarget ? 1 : NormalizationTarget / peak;
    }

    internal static int ChunkLength(ReadOnlySpan<float> remaining)
    {
        if (remaining.Length <= ChunkSamples) return remaining.Length;
        // Prefer a pause in the final three seconds of each bounded decoding window.
        const int window = SampleRate / 50;
        var best = ChunkSamples;
        var minimum = double.MaxValue;
        for (var start = ChunkSamples - 3 * SampleRate; start + window <= ChunkSamples; start += window)
        {
            double energy = 0;
            foreach (var sample in remaining.Slice(start, window)) energy += sample * sample;
            if (energy <= minimum) { minimum = energy; best = start + window / 2; }
        }
        return best;
    }
}
