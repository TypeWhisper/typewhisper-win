using System.Text.Json;
using TypeWhisper.Plugin.ParakeetCtc;
using Xunit;

namespace TypeWhisper.PluginSDK.Portable.Tests;

public sealed class CtcMacPolicyTests
{
    [Fact]
    public void MacDefaultsAndAdaptiveBonus()
    {
        Assert.Equal(.52f, CtcBiasPolicy.MinimumSimilarity(1));
        Assert.Equal(.55f, CtcBiasPolicy.MinimumSimilarity(11));
        Assert.Equal(.60f, CtcBiasPolicy.MinimumSimilarity(101));
        Assert.Equal(4.5, CtcBiasPolicy.Bonus(3));
        Assert.Equal(5.85, CtcBiasPolicy.Bonus(6), 8);
        Assert.True(CtcBiasPolicy.Accept(-6, -8, 3));
        Assert.False(CtcBiasPolicy.Accept(-1, -20, 6));
        Assert.False(CtcBiasPolicy.Accept(double.NegativeInfinity, -1, 3));
    }

    [Fact]
    public void ScoresAreNormalizedByTokenCount()
    {
        var emission = new CtcEmission([-2, -90, -90, -90, -2, -90], 2, 3, .08);
        Assert.Equal(-2, CtcVocabularyScorer.Score(emission, [0, 1], 2, 0, 2, default));
    }

    [Fact]
    public void TokenizerUsesMergeRankNormalizationAndBoundaryVariants()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ctc-bpe-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var vocab = new Dictionary<string, int> { ["▁"] = 0, ["a"] = 1, ["b"] = 2, ["c"] = 3, ["bc"] = 4, ["ab"] = 5 };
            File.WriteAllLines(Path.Combine(folder, "tokens.txt"), vocab.Select(p => $"{p.Key} {p.Value}").Append("<blk> 6"));
            File.WriteAllText(Path.Combine(folder, "tokenizer.json"), JsonSerializer.Serialize(new { model = new { type = "BPE", vocab, merges = new[] { "b c", "a b" } } }));
            var tokenizer = new CtcTokenizer(Path.Combine(folder, "tokens.txt"));
            Assert.Equal(new[] { 0, 1, 4 }, tokenizer.Encode("ＡBC"));
            Assert.Equal(new[] { 1, 4 }, tokenizer.Encode("ABC", false));
            Assert.Empty(tokenizer.Encode("unknown"));
        }
        finally { Directory.Delete(folder, true); }
    }
}
