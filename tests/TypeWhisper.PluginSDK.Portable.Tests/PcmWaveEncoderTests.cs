using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

public sealed class PcmWaveEncoderTests
{
    [Fact]
    public void EncodesPcmAndHonorsProviderLimitIncludingHeader()
    {
        var wav = PcmWaveEncoder.Encode([-1, 0, 1], 50);
        Assert.Equal(50, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(wav, 44));
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(wav, 48));
        var error = Assert.Throws<PluginRequestException>(() => PcmWaveEncoder.Encode([-1, 0, 1], 49));
        Assert.Equal(PluginRequestFailureKind.RequestTooLarge, error.FailureKind);
        Assert.DoesNotContain("Groq", error.Message);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidSamplesAreRejected(float value) =>
        Assert.Throws<ArgumentException>(() => PcmWaveEncoder.Encode([value]));
}
