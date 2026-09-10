using TypeWhisper.Plugin.ParakeetCtc;
using TypeWhisper.PluginHost;
using Xunit;

namespace TypeWhisper.PluginSDK.Portable.Tests;

public sealed class CtcScoringTests
{
    [Fact]
    public void AcousticEvidencePrefersMatchingLabel()
    {
        var emission = new CtcEmission([-0.1f, -8f, -5f], 1, 3, .08);
        Assert.True(CtcVocabularyScorer.Score(emission, [0], 2, 0, 1, default)
            > CtcVocabularyScorer.Score(emission, [1], 2, 0, 1, default));
    }

    [Fact]
    public void RepeatedLabelsRequireBlankBetweenThem()
    {
        var twoFrames = new CtcEmission([0, -9, 0, -9], 2, 2, .08);
        Assert.Equal(double.NegativeInfinity, CtcVocabularyScorer.Score(twoFrames, [0, 0], 1, 0, 2, default));
        var separated = new CtcEmission([0, -9, -9, 0, 0, -9], 3, 2, .08);
        Assert.Equal(0, CtcVocabularyScorer.Score(separated, [0, 0], 1, 0, 3, default));
    }

    [Fact]
    public void WindowExcludesBetterEvidenceOutsideIt()
    {
        var emission = new CtcEmission([0, -9, -8, 0], 2, 2, .08);
        Assert.Equal(-8, CtcVocabularyScorer.Score(emission, [0], 1, 1, 2, default));
        Assert.Equal(double.NegativeInfinity, CtcVocabularyScorer.Score(emission, [1], 1, 0, 2, default));
    }

    [Fact]
    public void ScoringHonorsCancellation()
    {
        var emission = new CtcEmission([0, -9], 1, 2, .08);
        Assert.Throws<OperationCanceledException>(() => CtcVocabularyScorer.Score(emission, [0], 1, 0, 1, new CancellationToken(true)));
    }

    [Fact]
    public void HostPersistsSettingsAndPreservesOtherKeys()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TypeWhisper-Ctc-" + Guid.NewGuid());
        try
        {
            var host = new VocabularyHostServices(directory);
            Assert.False(host.GetSetting<bool>("Enabled"));
            host.SetSetting("ModelDirectory", "test-model");
            host.SetSetting("Enabled", true);
            var reloaded = new VocabularyHostServices(directory);
            Assert.True(reloaded.GetSetting<bool>("Enabled"));
            Assert.Equal("test-model", reloaded.GetSetting<string>("ModelDirectory"));
            File.WriteAllText(Path.Combine(directory, "settings.json"), "invalid");
            Assert.Throws<System.Text.Json.JsonException>(() => reloaded.SetSetting("Enabled", false));
            Assert.Equal("invalid", File.ReadAllText(Path.Combine(directory, "settings.json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
