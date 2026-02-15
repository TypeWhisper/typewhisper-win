using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class TextDiffServiceTests
{
    [Fact]
    public void NoChanges_ReturnsEmpty()
    {
        var result = TextDiffService.ExtractCorrections("hello world", "hello world");
        Assert.Empty(result);
    }

    [Fact]
    public void SingleWordCorrection_ReturnsSuggestion()
    {
        var result = TextDiffService.ExtractCorrections(
            "I used kubernets today",
            "I used Kubernetes today");

        Assert.Single(result);
        Assert.Equal("kubernets", result[0].Original);
        Assert.Equal("Kubernetes", result[0].Replacement);
    }

    [Fact]
    public void MultipleCorrections_ReturnsAll()
    {
        var result = TextDiffService.ExtractCorrections(
            "the tenser flow and pie torch are great",
            "the TensorFlow and PyTorch are great");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void MajorRewrite_ReturnsEmpty()
    {
        var result = TextDiffService.ExtractCorrections(
            "this is a completely different sentence about cats",
            "the weather is nice today for a walk outside");

        Assert.Empty(result);
    }

    [Fact]
    public void EmptyStrings_ReturnsEmpty()
    {
        Assert.Empty(TextDiffService.ExtractCorrections("", ""));
    }

    [Fact]
    public void IdenticalText_ReturnsEmpty()
    {
        Assert.Empty(TextDiffService.ExtractCorrections("React Vue Angular", "React Vue Angular"));
    }

    [Fact]
    public void HasChanges_DetectsChanges()
    {
        Assert.True(TextDiffService.HasChanges("hello", "world"));
        Assert.False(TextDiffService.HasChanges("hello", "hello"));
    }

    [Fact]
    public void NearbyInsertion_PairsCorrectly()
    {
        var result = TextDiffService.ExtractCorrections(
            "the react native app",
            "the React Native app");

        Assert.Equal(2, result.Count);
    }
}
