using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class DictationOutputTests
{
    [Fact]
    public async Task CancelWhileHistoryLoadsPreventsHistoryCommitAndPaste()
    {
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        history.Setup(h => h.EnsureLoadedAsync()).Returns(loaded.Task);
        using var cancellation = new CancellationTokenSource();
        var pasted = false;
        var pending = new DictationOutputDelivery(history.Object).DeliverAsync(Record(), new(), () => new(),
            () => { pasted = true; return Task.FromResult(true); }, cancellation.Token);
        cancellation.Cancel(); loaded.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(pasted);
        history.Verify(h => h.TryAddRecord(It.IsAny<TranscriptionRecord>()), Times.Never());
    }

    private static TranscriptionRecord Record() => new() { Id = "output-test", Timestamp = DateTime.UtcNow, RawText = "raw", FinalText = "Reviewed text" };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HistoryAndPasteAreIndependent(bool save, bool autoPaste)
    {
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        var record = Record();
        if (save)
        {
            history.Setup(h => h.EnsureLoadedAsync()).Returns(Task.CompletedTask);
            history.Setup(h => h.TryAddRecord(record)).Returns(true);
        }
        var preferences = new DictationOutputPreferences { SaveToHistory = save, AutoPaste = autoPaste };
        var pasted = 0;
        var result = await new DictationOutputDelivery(history.Object).DeliverAsync(record, preferences,
            () => preferences, () => { pasted++; return Task.FromResult(true); });
        Assert.Equal(save, result.Saved);
        Assert.Equal(!autoPaste, result.NeedsReview);
        Assert.Equal(autoPaste ? 1 : 0, pasted);
        Assert.Same(record, result.Record);
        history.Verify(h => h.EnsureLoadedAsync(), save ? Times.Once() : Times.Never());
        history.Verify(h => h.TryAddRecord(record), save ? Times.Once() : Times.Never());
        history.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryFailurePreservesReviewAndPreventsPaste(bool loadFails)
    {
        var history = new Mock<IHistoryService>();
        history.Setup(h => h.EnsureLoadedAsync()).Returns(loadFails ? Task.FromException(new IOException()) : Task.CompletedTask);
        history.Setup(h => h.TryAddRecord(It.IsAny<TranscriptionRecord>())).Returns(false);
        var pasted = false;
        var result = await new DictationOutputDelivery(history.Object).DeliverAsync(Record(), new(), () => new(),
            () => { pasted = true; return Task.FromResult(true); });
        Assert.False(result.Saved);
        Assert.True(result.NeedsReview);
        Assert.Equal("Reviewed text", result.Record.FinalText);
        Assert.False(pasted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrThrowingPasteRetainsUnsavedReview(bool throws)
    {
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        var preferences = new DictationOutputPreferences { SaveToHistory = false };
        var result = await new DictationOutputDelivery(history.Object).DeliverAsync(Record(), preferences, () => preferences,
            () => throws ? Task.FromException<bool>(new IOException()) : Task.FromResult(false));
        Assert.True(result.NeedsReview);
        Assert.False(result.Saved);
        history.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DisablingWhileHistoryLoadsPreventsBothWritesAndPaste()
    {
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        history.Setup(h => h.EnsureLoadedAsync()).Returns(loading.Task);
        var current = new DictationOutputPreferences();
        var pasted = false;
        var delivery = new DictationOutputDelivery(history.Object).DeliverAsync(Record(), current, () => current,
            () => { pasted = true; return Task.FromResult(true); });
        current = new() { SaveToHistory = false, AutoPaste = false };
        loading.SetResult();
        var result = await delivery;
        Assert.True(result.NeedsReview);
        Assert.False(result.Saved);
        Assert.False(pasted);
        history.Verify(h => h.EnsureLoadedAsync(), Times.Once);
        history.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EnablingDoesNotGrantOldRecordingPermission()
    {
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        var result = await new DictationOutputDelivery(history.Object).DeliverAsync(Record(),
            new() { SaveToHistory = false, AutoPaste = false }, () => new(),
            () => throw new Exception("Must not paste"));
        Assert.True(result.NeedsReview);
        Assert.False(result.Saved);
        history.VerifyNoOtherCalls();
    }

    [Fact]
    public void PreferencesSurviveRestartWithoutWritingOnRead()
    {
        WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "output.json");
            var store = new DictationOutputPreferencesStore(path);
            Assert.False(File.Exists(path));
            Assert.Null(store.Save(new() { AutoPaste = false, SaveToHistory = false }));
            var bytes = File.ReadAllBytes(path);
            var restored = new DictationOutputPreferencesStore(path);
            Assert.False(restored.Current.AutoPaste);
            Assert.False(restored.Current.SaveToHistory);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        });
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"AutoPaste\":true,\"SaveToHistory\":\"invalid\"}")]
    public void CorruptPreferencesCannotSilentlyEnableWrites(string contents)
    {
        WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "output.json");
            File.WriteAllText(path, contents);
            var store = new DictationOutputPreferencesStore(path);
            Assert.NotNull(store.Error);
            Assert.False(store.Current.AutoPaste);
            Assert.False(store.Current.SaveToHistory);
            Assert.Equal(contents, File.ReadAllText(path));
            Assert.Null(store.Save(new() { AutoPaste = true, SaveToHistory = false }));
            Assert.True(new DictationOutputPreferencesStore(path).Current.AutoPaste);
        });
    }

    [Fact]
    public void FailedSaveKeepsPreviousChoicesAndCleansTemporaryFile()
    {
        WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "output.json");
            var store = new DictationOutputPreferencesStore(path);
            Directory.CreateDirectory(path);
            Assert.NotNull(store.Save(new() { AutoPaste = false, SaveToHistory = false }));
            Assert.True(store.Current.AutoPaste);
            Assert.True(store.Current.SaveToHistory);
            Assert.Empty(Directory.GetFiles(directory));
        });
    }

    [Fact]
    public void UnreadablePathDoesNotEnableOutput()
    {
        WithDirectory(directory =>
        {
            var store = new DictationOutputPreferencesStore(directory);
            Assert.NotNull(store.Error);
            Assert.False(store.Current.AutoPaste);
            Assert.False(store.Current.SaveToHistory);
        });
    }

    private static void WithDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TypeWhisper-output-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
