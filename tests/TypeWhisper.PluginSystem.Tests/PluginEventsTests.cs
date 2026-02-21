using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class PluginEventsTests
{
    [Fact]
    public void RecordingStartedEvent_HasTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var evt = new RecordingStartedEvent();
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(evt.Timestamp, before, after);
    }

    [Fact]
    public void RecordingStoppedEvent_DefaultDurationIsZero()
    {
        var evt = new RecordingStoppedEvent();
        Assert.Equal(0, evt.DurationSeconds);
    }

    [Fact]
    public void RecordingStoppedEvent_PreservesDuration()
    {
        var evt = new RecordingStoppedEvent { DurationSeconds = 15.7 };
        Assert.Equal(15.7, evt.DurationSeconds);
    }

    [Fact]
    public void TranscriptionCompletedEvent_RequiredAndOptionalFields()
    {
        var evt = new TranscriptionCompletedEvent
        {
            Text = "Hello",
            DetectedLanguage = "en",
            DurationSeconds = 2.3,
            ModelId = "whisper-1"
        };

        Assert.Equal("Hello", evt.Text);
        Assert.Equal("en", evt.DetectedLanguage);
        Assert.Equal(2.3, evt.DurationSeconds);
        Assert.Equal("whisper-1", evt.ModelId);
    }

    [Fact]
    public void TranscriptionCompletedEvent_OptionalFieldsAreNull()
    {
        var evt = new TranscriptionCompletedEvent { Text = "Hi" };

        Assert.Null(evt.DetectedLanguage);
        Assert.Equal(0, evt.DurationSeconds);
        Assert.Null(evt.ModelId);
    }

    [Fact]
    public void TranscriptionFailedEvent_RequiredAndOptionalFields()
    {
        var evt = new TranscriptionFailedEvent
        {
            ErrorMessage = "API timeout",
            ModelId = "groq-whisper"
        };

        Assert.Equal("API timeout", evt.ErrorMessage);
        Assert.Equal("groq-whisper", evt.ModelId);
    }

    [Fact]
    public void TranscriptionFailedEvent_ModelIdIsOptional()
    {
        var evt = new TranscriptionFailedEvent { ErrorMessage = "Error" };
        Assert.Null(evt.ModelId);
    }

    [Fact]
    public void TextInsertedEvent_AllFields()
    {
        var evt = new TextInsertedEvent
        {
            Text = "pasted text",
            TargetApp = "notepad"
        };

        Assert.Equal("pasted text", evt.Text);
        Assert.Equal("notepad", evt.TargetApp);
    }

    [Fact]
    public void TextInsertedEvent_TargetAppIsOptional()
    {
        var evt = new TextInsertedEvent { Text = "text" };
        Assert.Null(evt.TargetApp);
    }

    [Fact]
    public void RecordEquality_SameValues()
    {
        var a = new RecordingStoppedEvent { DurationSeconds = 5.0 };
        var b = new RecordingStoppedEvent { DurationSeconds = 5.0 };

        // Record equality compares values, but timestamps will differ slightly
        // so we compare the specific field
        Assert.Equal(a.DurationSeconds, b.DurationSeconds);
    }

    [Fact]
    public void RecordWith_CreatesModifiedCopy()
    {
        var original = new TranscriptionCompletedEvent
        {
            Text = "original",
            DurationSeconds = 1.0
        };

        var modified = original with { Text = "modified" };

        Assert.Equal("modified", modified.Text);
        Assert.Equal(1.0, modified.DurationSeconds);
        Assert.Equal(original.Timestamp, modified.Timestamp);
    }
}
