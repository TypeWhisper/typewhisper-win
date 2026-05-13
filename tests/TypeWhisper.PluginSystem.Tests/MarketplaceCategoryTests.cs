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

    [Theory]
    [InlineData("post-processor")]
    [InlineData("postprocessor")]
    [InlineData("postprocessing")]
    [InlineData("processing")]
    public void Resolve_MapsPostProcessingAliases(string rawCategory)
    {
        var category = PluginMarketplaceCategories.Resolve(rawCategory);

        Assert.Equal("post-processing", category.Key);
    }

    [Fact]
    public void ResolveAll_CombinesPrimaryAndCategoriesWithPrimaryFirst()
    {
        var categories = PluginMarketplaceCategories.ResolveAll(
            "transcription",
            ["llm", "tts", "transcription", "text-to-speech"]);

        Assert.Equal(["transcription", "llm", "tts"], categories.Select(category => category.Key).ToArray());
    }

    [Fact]
    public void ResolveAll_FallsBackToPrimaryCategoryForLegacyMetadata()
    {
        var categories = PluginMarketplaceCategories.ResolveAll("LLM", null);

        Assert.Equal(["llm"], categories.Select(category => category.Key).ToArray());
    }

    [Fact]
    public void ResolveAll_FallsBackToUtilityWhenMetadataIsMissing()
    {
        var categories = PluginMarketplaceCategories.ResolveAll(null, []);

        Assert.Equal(["utility"], categories.Select(category => category.Key).ToArray());
    }
}
