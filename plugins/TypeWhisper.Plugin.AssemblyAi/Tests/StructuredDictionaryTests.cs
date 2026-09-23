using TypeWhisper.Plugin.AssemblyAi;
using TypeWhisper.PluginSDK.Models;
using Xunit;

public sealed class StructuredDictionaryTests
{
    [Fact]
    public void LiteralPunctuationStaysWithinEachProviderKeyword()
    {
        string[] terms = ["Washington, D.C.", "Äpfel; Öl", "quoted \"name\""];
        var prompt = PluginDictionaryTerms.CreateStructuredPrompt(terms);
        Assert.Equal(terms, AssemblyAiModels.Terms(prompt, AssemblyAiModels.Resolve(null)));
        prompt = "Alpha, Beta";
        Assert.Equal(new[] { "Alpha", "Beta" }, AssemblyAiModels.Terms(prompt, AssemblyAiModels.Resolve(null)));
    }
}
