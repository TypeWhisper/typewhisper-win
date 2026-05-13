using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class MarketplaceCategoryTests
{
    [Theory]
    [InlineData("tts")]
    [InlineData("TTS")]
    [InlineData("text-to-speech")]
    [InlineData("texttospeech")]
    [InlineData("text to speech")]
    public void Resolve_MapsTextToSpeechCategory(string rawCategory)
    {
        var category = PluginMarketplaceCategories.Resolve(rawCategory);

        Assert.Equal("tts", category.Key);
        Assert.Equal(2, category.SortOrder);
    }

    [Fact]
    public void Resolve_OrdersTextToSpeechBetweenAiAndPostProcessors()
    {
        var llm = PluginMarketplaceCategories.Resolve("llm");
        var tts = PluginMarketplaceCategories.Resolve("tts");
        var postProcessing = PluginMarketplaceCategories.Resolve("post-processing");

        Assert.True(llm.SortOrder < tts.SortOrder);
        Assert.True(tts.SortOrder < postProcessing.SortOrder);
    }
}
