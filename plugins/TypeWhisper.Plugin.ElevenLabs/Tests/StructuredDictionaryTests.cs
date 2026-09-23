using TypeWhisper.Plugin.ElevenLabs;
using TypeWhisper.PluginSDK.Models;
using Xunit;

public sealed class StructuredDictionaryTests
{
    [Fact]
    public void LiteralPunctuationStaysWithinEachProviderKeyword()
    {
        string[] terms = ["Washington, D.C.", "Äpfel; Öl", "quoted \"name\""];
        var prompt = PluginDictionaryTerms.CreateStructuredPrompt(terms);
        Assert.Equal(terms, ElevenLabsPlugin.ExtractKeyterms(prompt));
        prompt = "Alpha, Beta";
        Assert.Equal(new[] { "Alpha", "Beta" }, ElevenLabsPlugin.ExtractKeyterms(prompt));
    }
}
