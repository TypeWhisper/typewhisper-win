using TypeWhisper.Presentation;
using Xunit;

public sealed class FinalSpeechPolicyTests
{
    [Theory]
    [InlineData("Hello", null, false, false, false)]
    [InlineData("Hello", 0.8f, false, false, false)]
    [InlineData("Hello", 0.8001f, false, false, true)]
    [InlineData("Hello", 0.95f, true, false, false)]
    [InlineData("Hello", 0.95f, false, true, false)]
    [InlineData("Hello", 0.95f, true, true, false)]
    [InlineData("", null, false, false, true)]
    [InlineData(" ", null, false, true, true)]
    [InlineData(null, 0.1f, false, false, true)]
    [InlineData("", 0.95f, true, false, false)]
    public void PreservesWindowsFinalResultDecision(string? text, float? probability, bool preview,
        bool quietOverride, bool reject) =>
        Assert.Equal(reject, FinalSpeechPolicy.ShouldReject(text, probability, preview, quietOverride));
}
