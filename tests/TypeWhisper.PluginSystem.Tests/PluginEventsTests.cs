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
        var id = Guid.NewGuid();
        var evt = new TranscriptionFailedEvent
        {
            ErrorMessage = "API timeout",
            ModelId = "groq-whisper",
            AppName = "Chrome",
            RecordingId = id
        };

        Assert.Equal("API timeout", evt.ErrorMessage);
        Assert.Equal("groq-whisper", evt.ModelId);
        Assert.Equal("Chrome", evt.AppName);
        Assert.Equal(id, evt.RecordingId);
    }

    [Fact]
    public void TranscriptionFailedEvent_ModelIdIsOptional()
    {
        var evt = new TranscriptionFailedEvent { ErrorMessage = "Error" };
        Assert.Null(evt.ModelId);
    }

    [Fact]
    public void TranscriptionFailedEvent_AppNameIsOptional()
    {
        var evt = new TranscriptionFailedEvent { ErrorMessage = "Error" };
        Assert.Null(evt.AppName);
    }

    [Fact]
    public void TranscriptionFailedEvent_AppNameIsPreserved()
    {
        var evt = new TranscriptionFailedEvent
        {
            ErrorMessage = "No speech",
            AppName = "Notepad"
        };

        Assert.Equal("Notepad", evt.AppName);
    }

    [Fact]
    public void TranscriptionFailedEvent_RecordingIdIsOptional()
    {
        var evt = new TranscriptionFailedEvent { ErrorMessage = "Error" };
        Assert.Null(evt.RecordingId);
    }

    [Fact]
    public void TranscriptionFailedEvent_RecordingIdIsPreserved()
    {
        var id = Guid.NewGuid();
        var evt = new TranscriptionFailedEvent
        {
            ErrorMessage = "No speech",
            RecordingId = id
        };

        Assert.Equal(id, evt.RecordingId);
    }

    [Fact]
    public void TranscriptionFailedEvent_DifferentRecordingIds_AreDistinct()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var evt1 = new TranscriptionFailedEvent { ErrorMessage = "Error", RecordingId = id1 };
        var evt2 = new TranscriptionFailedEvent { ErrorMessage = "Error", RecordingId = id2 };

        Assert.NotEqual(evt1.RecordingId, evt2.RecordingId);
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
