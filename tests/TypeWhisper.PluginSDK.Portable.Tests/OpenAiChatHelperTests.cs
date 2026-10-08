using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using Xunit;

public sealed class OpenAiChatHelperTests
{
    [Fact]
    public void ReturnsTheTrimmedContentOfTheFirstChoice()
    {
        var text = OpenAiChatHelper.ParseChatCompletionResponse("""
            {"choices":[{"message":{"role":"assistant","content":"  Done. \n"},"finish_reason":"stop"},
                        {"message":{"content":"second"}}]}
            """);

        Assert.Equal("Done.", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"message":{"content":"   "}}]}""")]
    [InlineData("""{"choices":[{"message":{"content":null}}]}""")]
    public void RejectsEmptyResponses(string json)
    {
        var error = Assert.Throws<PluginRequestException>(() => OpenAiChatHelper.ParseChatCompletionResponse(json));

        Assert.Equal(PluginRequestFailureKind.EmptyResponse, error.FailureKind);
    }

    [Fact]
    public void RejectsAnswersCutOffAtTheOutputLimit()
    {
        var error = Assert.Throws<PluginRequestException>(() => OpenAiChatHelper.ParseChatCompletionResponse("""
            {"choices":[{"message":{"content":"partial"},"finish_reason":"length"}]}
            """));

        Assert.Equal(PluginRequestFailureKind.OutputTruncated, error.FailureKind);
    }
}
