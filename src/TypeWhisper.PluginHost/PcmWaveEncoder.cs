using System.Text;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginHost;

/// <summary>Encodes mono 16 kHz samples without platform codecs.</summary>
public static class PcmWaveEncoder
{
    /// <summary>Validates the upload size before allocating the encoded WAV.</summary>
    public static byte[] Encode(float[] samples, int maximumBytes = int.MaxValue)
    {
        if (samples.Length == 0) throw new ArgumentException("No audio captured.");
        if (samples.LongLength * 2 + 44 > maximumBytes)
            throw new PluginRequestException("Recording exceeds the selected provider's upload limit. Use a shorter recording.", PluginRequestFailureKind.RequestTooLarge);
        using var stream = new MemoryStream(44 + samples.Length * 2);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(36 + samples.Length * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples.Length * 2);
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample)) throw new ArgumentException("Audio contains invalid samples.");
            writer.Write((short)Math.Clamp((int)Math.Round(Math.Clamp(sample, -1, 1) * 32768), short.MinValue, short.MaxValue));
        }
        writer.Flush(); return stream.ToArray();
    }

}
