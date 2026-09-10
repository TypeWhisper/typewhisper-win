using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LocalApiDictationSessionsTests
{
    private static TranscriptionRecord Record(string text) => new()
    { Id = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow, RawText = text, FinalText = text };

    [Fact]
    public void StopReusesGenerationIdAndRetainsProcessingUntilCompletion()
    {
        var sessions = new LocalApiDictationSessions();
        var first = sessions.Register(1);
        Assert.Equal(first.Id, sessions.Register(1).Id);
        sessions.MarkProcessing(first.Id);
        sessions.Refresh(1, true, true, 0, null);
        Assert.Equal("processing", sessions.Find(first.Id)!.Status);
        sessions.Refresh(1, false, true, 0, null);
        Assert.Equal("processing", sessions.Find(first.Id)!.Status);
        var result = Record("first");
        sessions.Refresh(1, false, false, 1, result);
        Assert.Same(result, sessions.Find(first.Id)!.Transcription);
        Assert.Equal("completed", sessions.Find(first.Id)!.Status);
    }

    [Fact]
    public void CancelledApiSessionNeverReceivesLaterGuiTranscript()
    {
        var sessions = new LocalApiDictationSessions();
        var first = sessions.Register(1);
        // No polling happened while GUI canceled A, recorded B, and completed B.
        sessions.Refresh(2, false, false, 2, Record("private second recording"));
        var cancelled = sessions.Find(first.Id)!;
        Assert.Equal("failed", cancelled.Status);
        Assert.Null(cancelled.Transcription);
        Assert.NotEqual(first.Id, sessions.Register(2).Id);
    }

    [Fact]
    public void CompletedApiSessionIsFrozenBeforeFurtherGuiRecordings()
    {
        var sessions = new LocalApiDictationSessions();
        var first = sessions.Register(1);
        var result = Record("first");
        sessions.Refresh(1, false, false, 1, result);
        sessions.Refresh(2, true, false, 1, result);
        sessions.Refresh(2, false, false, 2, Record("second"));
        Assert.Same(result, sessions.Find(first.Id)!.Transcription);
        Assert.Equal("completed", sessions.Find(first.Id)!.Status);
    }

    [Fact]
    public void LaterRecordingGetsDistinctStopIdWithoutPollingPreviousResult()
    {
        var sessions = new LocalApiDictationSessions();
        var first = sessions.Register(1);
        sessions.Refresh(2, true, false, 1, Record("first"));
        var second = sessions.Register(2);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("completed", sessions.Find(first.Id)!.Status);
        Assert.Equal("recording", second.Status);
    }

    [Fact]
    public void FailureCannotBeReplacedWithLaterSuccessfulResult()
    {
        var sessions = new LocalApiDictationSessions();
        var first = sessions.Register(1);
        sessions.Fail(first.Id);
        sessions.Refresh(1, false, false, 1, Record("late result"));
        Assert.Equal("failed", sessions.Find(first.Id)!.Status);
        Assert.Null(sessions.Find(first.Id)!.Transcription);
    }

    [Fact]
    public void HistoryIsBoundedAndAcceptsEquivalentUuidRepresentations()
    {
        var sessions = new LocalApiDictationSessions();
        var first = sessions.Register(1);
        Assert.Equal(first, sessions.Find(Guid.Parse(first.Id).ToString("N")));
        Assert.Null(sessions.Find("invalid"));
        for (var generation = 2; generation <= 33; generation++) sessions.Register(generation);
        Assert.Null(sessions.Find(first.Id));
        Assert.Throws<ArgumentOutOfRangeException>(() => sessions.Register(0));
    }
}
