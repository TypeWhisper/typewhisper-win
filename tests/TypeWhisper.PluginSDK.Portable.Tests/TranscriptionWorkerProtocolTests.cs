using System.Text.Json;
using TypeWhisper.PluginHost;

public sealed class TranscriptionWorkerProtocolTests
{
    [Fact]
    public void OlderResultsWithoutSegmentConfidenceRemainReadable()
    {
        var result = JsonSerializer.Deserialize<TranscriptionWorkerResult>("""
            {"text":"probe","detectedLanguage":"en","durationSeconds":1,
             "segments":[{"text":"probe","start":0,"end":1}]}
            """, TranscriptionWorkerProtocol.Json)!;

        var decoded = TranscriptionWorkerProtocol.FromResult(result);

        Assert.Equal("probe", decoded.Text);
        var segment = Assert.Single(decoded.Segments);
        Assert.Equal(1, segment.End);
        Assert.Null(segment.NoSpeechProbability);
    }
}
