using System.Text.Json;
using TypeWhisper.Plugin.AssemblyAi;

public partial class AssemblyAiTests
{
    [Theory]
    [InlineData("")]
    [InlineData(",\"utterances\":[]")]
    [InlineData(",\"utterances\":[{\"text\":\"invalid timing\",\"start\":500,\"end\":100,\"speaker\":\"A\"}]")]
    public void DiarizationWordFallbackKeepsWordTextWithoutRepeatedSpeakerLabels(string utterances)
    {
        using var document = JsonDocument.Parse("""
            {"text":"Hello world.","words":[
              {"text":"Hello","start":100,"end":300,"speaker":"A"},
              {"text":"world.","start":300,"end":600,"speaker":"A"}]
            """ + utterances + "}");
        var result = AssemblyAiPlugin.ParseCompleted(document.RootElement, "en", diarization: true);
        Assert.Equal("Hello world.", result.Text);
        Assert.Equal(new[] { "Hello", "world." }, result.Segments.Select(s => s.Text));
        Assert.Equal(0.1, result.Segments[0].Start);
        Assert.Equal(0.6, result.Segments[1].End);
    }
}
