using System.Buffers.Binary;
using System.Text;
using TypeWhisper.Core.Audio;

namespace TypeWhisper.Core.Tests.Audio;

public class WavEncoderTests
{
    [Fact]
    public void Encode_EmptyArray_ProducesValidHeader()
    {
        var result = WavEncoder.Encode([]);

        Assert.Equal(44, result.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(result, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(result, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(result, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(result, 36, 4));

        // Data size should be 0
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(40)));
    }

    [Fact]
    public void Encode_SingleSample_CorrectLength()
    {
        var result = WavEncoder.Encode([0.5f]);

        Assert.Equal(46, result.Length); // 44 header + 2 bytes (1 sample * 16-bit)
    }

    [Fact]
    public void Encode_SampleValues_CorrectPcmConversion()
    {
        var result = WavEncoder.Encode([1.0f, -1.0f, 0.0f]);

        var sample0 = BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(44));
        var sample1 = BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(46));
        var sample2 = BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(48));

        Assert.Equal(32767, sample0);
        Assert.Equal(-32767, sample1);
        Assert.Equal(0, sample2);
    }

    [Fact]
    public void Encode_ClampsBeyondRange()
    {
        var result = WavEncoder.Encode([2.0f, -3.0f]);

        var sample0 = BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(44));
        var sample1 = BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(46));

        Assert.Equal(32767, sample0);
        Assert.Equal(-32767, sample1);
    }

    [Fact]
    public void Encode_HeaderFields_Correct()
    {
        float[] samples = [0.1f, 0.2f, 0.3f, 0.4f];
        var result = WavEncoder.Encode(samples, sampleRate: 16000);

        // File size - 8
        Assert.Equal(36 + 8, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(4)));

        // PCM format
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(20)));

        // Channels
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(22)));

        // Sample rate
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(24)));

        // Byte rate (16000 * 1 * 2)
        Assert.Equal(32000, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(28)));

        // Block align
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(32)));

        // Bits per sample
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(34)));

        // Data length
        Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(40)));
    }
}
