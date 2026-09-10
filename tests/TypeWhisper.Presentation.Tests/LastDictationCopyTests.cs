using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LastDictationCopyTests
{
    private static LastCompletedDictationStore Completed()
    {
        var store = new LastCompletedDictationStore();
        store.TryPublish(new(new TranscriptionRecord
        {
            Id = "last", Timestamp = DateTime.UtcNow, SourceKind = "dictation", RawText = "raw text",
            FinalText = "Korrigierter Text\nZweite Zeile."
        }, false, true, "Review first, History off"));
        return store;
    }

    [Fact]
    public void CopiesExactFinalTextOnceWithoutHistoryOrPaste()
    {
        var store = Completed();
        var copied = new List<string>();
        Assert.Equal(LastDictationCopyResult.Copied, LastDictationCopy.Execute(store.Current, false, false, copied.Add));
        Assert.Equal("Korrigierter Text\nZweite Zeile.", Assert.Single(copied));
        Assert.NotNull(store.Current);
    }

    [Theory]
    [InlineData(true, false, LastDictationCopyResult.Ignored)]
    [InlineData(true, true, LastDictationCopyResult.Ignored)]
    [InlineData(false, true, LastDictationCopyResult.Busy)]
    public void ShutdownShortcutEditingAndActiveOperationsLeaveClipboardUntouched(bool blocked, bool busy, LastDictationCopyResult expected)
    {
        var calls = 0;
        Assert.Equal(expected, LastDictationCopy.Execute(Completed().Current, blocked, busy, _ => calls++));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void FreshSessionAndClosedSessionDoNotClearExistingClipboard()
    {
        var store = Completed();
        store.Close();
        var calls = 0;
        Assert.Equal(LastDictationCopyResult.Empty, LastDictationCopy.Execute(store.Current, false, false, _ => calls++));
        Assert.Equal(LastDictationCopyResult.Empty, LastDictationCopy.Execute(new LastCompletedDictationStore().Current, false, false, _ => calls++));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ClipboardFailureRetainsSnapshotForExplicitRetry()
    {
        var snapshot = Completed().Current;
        Assert.Equal(LastDictationCopyResult.ClipboardUnavailable,
            LastDictationCopy.Execute(snapshot, false, false, _ => throw new InvalidOperationException("Clipboard busy")));
        string? copied = null;
        Assert.Equal(LastDictationCopyResult.Copied, LastDictationCopy.Execute(snapshot, false, false, text => copied = text));
        Assert.Equal(snapshot!.Text, copied);
    }
}
