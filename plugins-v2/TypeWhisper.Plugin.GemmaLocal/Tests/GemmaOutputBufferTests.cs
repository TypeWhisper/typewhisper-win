using TypeWhisper.Plugin.GemmaLocal;
using TypeWhisper.PluginSDK;

namespace PortableMigration.Tests;

public sealed class GemmaOutputBufferTests
{
    [Theory]
    [InlineData("<end_of_turn>")]
    [InlineData("<eos>")]
    public void StopMarkerIsRemovedAcrossEveryFragmentBoundary(string marker)
    {
        for (var split = 0; split < marker.Length; split++)
        {
            var output = new GemmaOutputBuffer();
            Assert.False(output.Append("  Corrected text. " + marker[..split]));
            Assert.True(output.Append(marker[split..] + "ignored trailing text"));
            Assert.True(output.Append("also ignored"));
            Assert.Equal("Corrected text.", output.Finish(endOfGeneration: false));
        }
    }

    [Fact]
    public void CharacterFragmentsStopAtFirstMarker()
    {
        var output = new GemmaOutputBuffer();
        foreach (var character in "Hello<eos><end_of_turn>trailing") output.Append(character.ToString());
        Assert.Equal("Hello", output.Finish(endOfGeneration: false));
    }

    [Fact]
    public void NativeEndOfGenerationPreservesOrdinaryMarkup()
    {
        var output = new GemmaOutputBuffer();
        Assert.False(output.Append(" <div>A & B</div>; x < 5 "));
        Assert.Equal("<div>A & B</div>; x < 5", output.Finish(endOfGeneration: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("An unfinished sentence")]
    [InlineData("Looks complete.")]
    [InlineData("Answer<end_of_")]
    public void BudgetExhaustionNeverPublishesPartialOutput(string text)
    {
        var output = new GemmaOutputBuffer();
        Assert.False(output.Append(text));
        var error = Assert.Throws<PluginRequestException>(() => output.Finish(endOfGeneration: false));
        Assert.Equal(PluginRequestFailureKind.OutputTruncated, error.FailureKind);
    }
}
