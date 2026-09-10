using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LocalApiSegmentTranslationTests
{
    private static readonly TranscriptionSegment[] Source = [new("Hello.", 0.2, 1.3), new("Goodbye.", 2.1, 3.8)];

    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    [InlineData("json")]
    public async Task TranslationExportsTargetLanguageWithOriginalTiming(string format)
    {
        var result = await LocalApiTextProcessing.ProcessTranscriptAsync("Hello. Goodbye.", Source, true,
            (_, _) => Task.FromResult("hallo. Tschüss."),
            (input, _) =>
            {
                using var json = JsonDocument.Parse(input);
                Assert.Equal("Hello.", json.RootElement[0].GetProperty("text").GetString());
                return Task.FromResult("""[{"id":0,"text":"hallo."},{"id":1,"text":"Tschüss."}]""");
            }, text => text.Replace("hallo", "Hallo"), default);
        Assert.Equal("Hallo. Tschüss.", result.Text);
        Assert.Equal(Source.Select(s => (s.Start, s.End)), result.Segments.Select(s => (s.Start, s.End)));
        var response = LocalApiTranscription.FormatResponse(result.Text,
            result.Segments.Select(s => new LocalApiTranscriptSegment(s.Text, s.Start, s.End)), format);
        var output = Encoding.UTF8.GetString(response.Body);
        Assert.Contains("Hallo.", output);
        Assert.DoesNotContain("Hello", output);
        Assert.DoesNotContain("Goodbye", output);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""[{"id":1,"text":"Tschüss."},{"id":0,"text":"Hallo."}]""")]
    [InlineData("""[{"id":0,"text":"Hallo."},{"id":0,"text":"Tschüss."}]""")]
    [InlineData("""[{"id":0,"text":"Hallo."},{"id":1,"text":" "}]""")]
    [InlineData("""[{"id":0,"text":"Hallo."},{"id":1,"text":null}]""")]
    public async Task InvalidAlignmentFailsInsteadOfReturningSourceSubtitles(string response)
    {
        var error = await Assert.ThrowsAsync<LocalApiRequestException>(() =>
            LocalApiTextProcessing.ProcessTranscriptAsync("source", Source, false,
                (_, _) => Task.FromResult("Übersetzung"), (_, _) => Task.FromResult(response), null, default));
        Assert.Equal(502, error.StatusCode);
    }

    [Fact]
    public async Task BatchesKeepGlobalIdsAndTiming()
    {
        var source = Enumerable.Range(0, 65).Select(i => new TranscriptionSegment("Source " + i, i, i + 0.5)).ToArray();
        var calls = 0;
        var result = await LocalApiTextProcessing.ProcessTranscriptAsync("source", source, false,
            (_, _) => Task.FromResult("Übersetzung"), (input, _) =>
            {
                calls++;
                using var json = JsonDocument.Parse(input);
                Assert.InRange(json.RootElement.GetArrayLength(), 1, 32);
                return Task.FromResult(JsonSerializer.Serialize(json.RootElement.EnumerateArray().Select(e =>
                    new { id = e.GetProperty("id").GetInt32(), text = "Ziel " + e.GetProperty("id").GetInt32() })));
            }, null, default);
        Assert.Equal(3, calls);
        Assert.Equal("Ziel 64", result.Segments[^1].Text);
        Assert.Equal(64.5, result.Segments[^1].End);
    }

    [Fact]
    public async Task CancellationRejectsLateBatchWithoutReturningPartialOutput()
    {
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LocalApiTextProcessing.ProcessTranscriptAsync("source", Source, false,
                (_, _) => Task.FromResult("Übersetzung"), (_, _) =>
                {
                    cancel.Cancel();
                    return Task.FromResult("""[{"id":0,"text":"Hallo."},{"id":1,"text":"Tschüss."}]""");
                }, null, cancel.Token));
    }

    [Fact]
    public async Task LongSegmentsSplitBatchesBeforeTheItemLimit()
    {
        TranscriptionSegment[] source = [new(new string('a', 5000), 0, 1), new(new string('b', 5000), 1, 2)];
        var calls = 0;
        var result = await LocalApiTextProcessing.ProcessTranscriptAsync("source", source, false,
            (_, _) => Task.FromResult("Übersetzung"), (input, _) =>
            {
                using var json = JsonDocument.Parse(input);
                Assert.Equal(1, json.RootElement.GetArrayLength());
                Assert.Equal(calls, json.RootElement[0].GetProperty("id").GetInt32());
                return Task.FromResult(JsonSerializer.Serialize(new[] { new { id = calls++, text = "Übersetzung" } }));
            }, null, default);
        Assert.Equal(2, calls);
        Assert.Equal(2, result.Segments.Count);
    }

    [Fact]
    public async Task UntranslatedOutputRetainsProviderSegments()
    {
        var result = await LocalApiTextProcessing.ProcessTranscriptAsync("Refined text", Source, false,
            null, (_, _) => throw new Exception(), null, default);
        Assert.Equal("Refined text", result.Text);
        Assert.Same(Source, result.Segments);
    }

    [Fact]
    public async Task MainTextRetainsRefinementsAndParagraphsInsteadOfBeingRebuiltFromSegments()
    {
        var result = await LocalApiTextProcessing.ProcessTranscriptAsync("Refined name.\n\nContext.", Source, false,
            (text, _) =>
            {
                Assert.Equal("Refined name.\n\nContext.", text);
                return Task.FromResult("Korrigierter Name.\n\nKontext.");
            }, (_, _) => Task.FromResult("""[{"id":0,"text":"Hallo."},{"id":1,"text":"Tschüss."}]"""), null, default);
        Assert.Equal("Korrigierter Name.\n\nKontext.", result.Text);
        Assert.Equal("Hallo.", result.Segments[0].Text);
    }

    [Fact]
    public async Task UntimedTranslationRemainsTextOnly()
    {
        var result = await LocalApiTextProcessing.ProcessTranscriptAsync("Hello", [], false,
            (_, _) => Task.FromResult("Hallo"), (_, _) => throw new Exception(), null, default);
        Assert.Equal("Hallo", result.Text);
        Assert.Empty(result.Segments);
        Assert.Equal(422, Assert.Throws<LocalApiRequestException>(() =>
            LocalApiTranscription.FormatResponse(result.Text, [], "srt")).StatusCode);
    }
}
