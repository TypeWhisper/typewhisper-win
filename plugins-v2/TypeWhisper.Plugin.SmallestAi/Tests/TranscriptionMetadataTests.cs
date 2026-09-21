using TypeWhisper.Plugin.SmallestAi;

namespace PortableMigration.Tests;

public sealed class TranscriptionMetadataTests
{
    [Theory]
    [InlineData("\"metadata\":{\"duration\":5.28}", 5.28)]
    [InlineData("\"duration\":2.5,\"metadata\":{\"duration\":5.28}", 2.5)]
    [InlineData("\"duration\":-1,\"metadata\":{\"duration\":5.28}", 5.28)]
    [InlineData("\"metadata\":null", 0)]
    [InlineData("\"metadata\":{\"duration\":-1}", 0)]
    public void Duration_PreservesRootValueAndFallsBackToMetadata(string properties, double expected)
    {
        var result = SmallestAiPlugin.ParseTranscriptionResponse("{\"transcription\":\"Hello\"," + properties + "}", "en");
        Assert.Equal(expected, result.DurationSeconds);
    }

    [Theory]
    [InlineData("\"language\":\"de\"")]
    [InlineData("\"languages\":[\"de\",\"en\"]")]
    public void Streaming_PublishesDetectedLanguage(string language)
    {
        var collector = new SmallestAiTranscriptCollector();
        var update = collector.ApplyEvent("{\"transcript\":\"Hallo\",\"is_final\":true," + language + "}");
        Assert.Equal("de", update!.DetectedLanguage);
    }

    [Theory]
    [InlineData("zh")][InlineData("ja")][InlineData("ko")][InlineData("multi-asian")]
    public void EastAsianStreaming_UsesUsRegion(string language)
    {
        var uri = SmallestAiStreamingSession.BuildStreamingUri(language, true);
        Assert.Equal("api.us.smallest.ai", uri.Host);
        Assert.Equal("/waves/v1/stt/live", uri.AbsolutePath);
        Assert.Contains("model=pulse", uri.Query);
        Assert.Contains("language=" + language, uri.Query);
    }
}
