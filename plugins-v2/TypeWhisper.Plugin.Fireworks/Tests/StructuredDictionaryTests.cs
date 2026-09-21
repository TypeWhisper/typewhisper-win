using TypeWhisper.Plugin.Fireworks;
using TypeWhisper.PluginSDK.Models;
using Xunit;

public sealed class StructuredDictionaryTests
{
    [Fact]
    public void LiteralPunctuationStaysWithinEachProviderKeyword()
    {
        string[] terms = ["Washington, D.C.", "Äpfel; Öl", "quoted \"name\""];
        var prompt = PluginDictionaryTerms.CreateStructuredPrompt(terms);
        Assert.Equal(terms, ProviderConnection.Terms(prompt));
        prompt = "Alpha, Beta";
        Assert.Equal(new[] { "Alpha", "Beta" }, ProviderConnection.Terms(prompt));
    }
}
