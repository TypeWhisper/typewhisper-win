using Moq;
using TypeWhisper.Core.Interfaces;
using Xunit;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation.Tests;

public sealed class HistoryAudioOutputTests
{
    private static TranscriptionRecord Record(string? source = "dictation") => new()
    { Id = "audio-output", Timestamp = DateTime.UtcNow, SourceKind = source, RawText = "raw", FinalText = "text" };
    private static DictationOutputPreferences Enabled => new() { SaveHistoryAudio = true };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkerRechecksPrivacyAfterPreparationWithoutTreatingOptOutAsFailure(bool disableHistory)
    {
        var history = new Mock<IHistoryAudioService>(MockBehavior.Strict);
        history.Setup(h => h.EnsureLoadedAsync()).Returns(Task.CompletedTask);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var current = Enabled;
        history.Setup(h => h.TryAddRecordWithAudio(It.IsAny<TranscriptionRecord>(), It.IsAny<float[]>(), 16000,
            It.IsAny<Func<bool>>(), It.IsAny<CancellationToken>(), It.IsAny<Func<bool>>()))
            .Returns((TranscriptionRecord record, float[] samples, int rate, Func<bool> audio, CancellationToken ct, Func<bool> saveHistory) =>
            {
                Assert.True(audio()); Assert.True(saveHistory());
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test barrier timed out.");
                Assert.False(audio());
                Assert.Equal(!disableHistory, saveHistory());
                return new HistoryAudioSaveResult(record, !disableHistory, null) { Suppressed = disableHistory };
            });
        var delivery = new DictationOutputDelivery(history.Object).DeliverAsync(Record(), Enabled,
            () => Volatile.Read(ref current), () => Task.FromResult(true), samples: [0.1f]);
        try
        {
            await entered.Task;
            Assert.False(delivery.IsCompleted);
            Volatile.Write(ref current, Enabled with { SaveToHistory = !disableHistory, SaveHistoryAudio = false });
        }
        finally { release.Set(); }
        var result = await delivery;
        Assert.Equal(!disableHistory, result.Saved); Assert.False(result.Failed); Assert.False(result.NeedsReview);
        if (disableHistory) Assert.Contains("Not saved to History.", result.Message);
    }

    [Fact]
    public async Task CanceledWorkerMustDrainAndCannotPasteItsLateResult()
    {
        var history = new Mock<IHistoryAudioService>(MockBehavior.Strict);
        history.Setup(h => h.EnsureLoadedAsync()).Returns(Task.CompletedTask);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        history.Setup(h => h.TryAddRecordWithAudio(It.IsAny<TranscriptionRecord>(), It.IsAny<float[]>(), 16000,
            It.IsAny<Func<bool>>(), It.IsAny<CancellationToken>(), It.IsAny<Func<bool>>()))
            .Returns((TranscriptionRecord record, float[] samples, int rate, Func<bool> audio, CancellationToken ct, Func<bool> saveHistory) =>
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test barrier timed out.");
                Assert.True(ct.IsCancellationRequested); Assert.False(audio()); Assert.False(saveHistory());
                return new HistoryAudioSaveResult(record, false, null) { Suppressed = true };
            });
        var pasted = false;
        var delivery = new DictationOutputDelivery(history.Object).DeliverAsync(Record(), Enabled, () => Enabled,
            () => { pasted = true; return Task.FromResult(true); }, cancellation.Token, samples: [0.1f]);
        try { await entered.Task; cancellation.Cancel(); Assert.False(delivery.IsCompleted); }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        Assert.False(pasted);
    }

    [Fact]
    public async Task SuccessfulAudioCommitReturnsOwnedReferenceAndPermitsPaste()
    {
        var history = new Mock<IHistoryAudioService>(MockBehavior.Strict);
        history.Setup(h => h.EnsureLoadedAsync()).Returns(Task.CompletedTask);
        var actual = Record() with { AudioFileName = "owned.wav" };
        history.Setup(h => h.TryAddRecordWithAudio(It.IsAny<TranscriptionRecord>(), It.IsAny<float[]>(), 16000, It.IsAny<Func<bool>>(), default, It.IsAny<Func<bool>>()))
            .Returns(new HistoryAudioSaveResult(actual, true, null));
        var pasted = false;
        var result = await new DictationOutputDelivery(history.Object).DeliverAsync(Record(), Enabled, () => Enabled,
            () => { pasted = true; return Task.FromResult(true); }, samples: [0.1f]);
        Assert.Same(actual, result.Record); Assert.True(result.Saved); Assert.True(pasted);
        Assert.False(result.Failed); Assert.False(result.NeedsReview);
    }

    [Fact]
    public async Task RetentionReportsAudioCleanupFailureAfterTextDeletionAndRetriesEvenWithForever()
    {
        var root = Path.Combine(Path.GetTempPath(), "audio-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var preferences = new HistoryRetentionPreferencesStore(Path.Combine(root, "retention.json"));
            Assert.Null(preferences.Save(new(HistoryRetentionMode.Duration, 60)));
            var old = Record() with { CreatedAt = DateTime.UtcNow.AddDays(-2) };
            IReadOnlyList<TranscriptionRecord> records = [old];
            string? cleanupError = null;
            var history = new Mock<IHistoryAudioService>(MockBehavior.Strict);
            history.Setup(h => h.EnsureLoadedAsync()).Returns(Task.CompletedTask);
            history.SetupGet(h => h.Records).Returns(() => records);
            history.SetupGet(h => h.AudioCleanupError).Returns(() => cleanupError);
            history.Setup(h => h.PurgeOldRecords(It.IsAny<TimeSpan?>())).Callback(() =>
            { records = []; cleanupError = "Owned audio cleanup remains pending."; });
            history.Setup(h => h.RetryAudioCleanup()).Returns(() => { cleanupError = null; return (string?)null; });
            var controller = new HistoryRetentionController(history.Object, preferences);
            Assert.Contains("audio cleanup", (await controller.ApplyAsync())!);
            Assert.Empty(records);
            Assert.NotNull(controller.Error);
            Assert.Null(preferences.Save(new(HistoryRetentionMode.Forever, 60)));
            Assert.Null(await controller.ApplyAsync()); Assert.Null(controller.Error);
            history.Verify(h => h.RetryAudioCleanup(), Times.Once());
            history.Verify(h => h.PurgeOldRecords(It.IsAny<TimeSpan?>()), Times.Once());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AudioWarningRetainsActualSavedRecordAndPreventsPaste()
    {
        var history = new Mock<IHistoryAudioService>(MockBehavior.Strict);
        history.Setup(h => h.EnsureLoadedAsync()).Returns(Task.CompletedTask);
        var original = Record(); var actual = original with { AudioFileName = null };
        var samples = new float[] { 0.1f };
        history.Setup(h => h.TryAddRecordWithAudio(original, samples, 16000, It.IsAny<Func<bool>>(), default, It.IsAny<Func<bool>>()))
            .Returns(new HistoryAudioSaveResult(actual, true, "Audio could not be retained."));
        var pasted = false;
        var result = await new DictationOutputDelivery(history.Object).DeliverAsync(original, Enabled, () => Enabled,
            () => { pasted = true; return Task.FromResult(true); }, samples: samples);
        Assert.Same(actual, result.Record); Assert.True(result.Saved);
        Assert.True(result.Failed); Assert.True(result.NeedsReview); Assert.False(pasted);
        Assert.Contains("Audio could not be retained.", result.Message);
        history.Verify(h => h.TryAddRecord(It.IsAny<TranscriptionRecord>()), Times.Never());
    }

    [Fact]
    public async Task SuccessfulAudioUsesReturnedReferenceAndRechecksPermissionAtCommit()
    {
        var history = new Mock<IHistoryAudioService>(MockBehavior.Strict);
        history.Setup(h => h.EnsureLoadedAsync()).Returns(Task.CompletedTask);
        var current = Enabled;
        var original = Record();
        history.Setup(h => h.TryAddRecordWithAudio(original, It.IsAny<float[]>(), 16000, It.IsAny<Func<bool>>(), default, It.IsAny<Func<bool>>()))
            .Returns((TranscriptionRecord record, float[] samples, int rate, Func<bool> permission, CancellationToken ct, Func<bool> historyPermission) =>
            {
                Assert.True(permission());
                current = current with { SaveHistoryAudio = false };
                Assert.False(permission());
                return new HistoryAudioSaveResult(record with { AudioFileName = null }, true, null);
            });
        var result = await new DictationOutputDelivery(history.Object).DeliverAsync(original, Enabled, () => current,
            () => Task.FromResult(true), samples: [0.1f]);
        Assert.True(result.Saved); Assert.False(result.Failed); Assert.Null(result.Record.AudioFileName);
    }

    [Theory]
    [InlineData("file", true, true)]
    [InlineData(null, true, true)]
    [InlineData("dictation", false, true)]
    [InlineData("dictation", true, false)]
    public async Task IneligibleSourceOrEitherOptOutNeverCallsAudio(string? source, bool start, bool current)
    {
        var history = new Mock<IHistoryAudioService>(MockBehavior.Strict);
        history.Setup(h => h.EnsureLoadedAsync()).Returns(Task.CompletedTask);
        history.Setup(h => h.TryAddRecord(It.IsAny<TranscriptionRecord>())).Returns(true);
        var result = await new DictationOutputDelivery(history.Object).DeliverAsync(Record(source),
            new() { SaveHistoryAudio = start }, () => new() { SaveHistoryAudio = current },
            () => Task.FromResult(true), samples: [0.1f]);
        Assert.True(result.Saved); Assert.False(result.Failed);
        history.Verify(h => h.TryAddRecordWithAudio(It.IsAny<TranscriptionRecord>(), It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<Func<bool>>(), It.IsAny<CancellationToken>(), It.IsAny<Func<bool>>()), Times.Never());
    }

    [Fact]
    public void AudioRestrictionRequiresBothHistoryAndAudioPermissions()
    {
        foreach (var startHistory in new[] { false, true })
        foreach (var currentHistory in new[] { false, true })
        foreach (var startAudio in new[] { false, true })
        foreach (var currentAudio in new[] { false, true })
        {
            var result = new DictationOutputPreferences { SaveToHistory = startHistory, SaveHistoryAudio = startAudio }
                .RestrictedBy(new() { SaveToHistory = currentHistory, SaveHistoryAudio = currentAudio });
            Assert.Equal(startHistory && currentHistory && startAudio && currentAudio, result.SaveHistoryAudio);
        }
    }

    [Fact]
    public void LegacyPreferencesDefaultAudioOffAndExplicitChoiceSurvivesReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "history-audio-prefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "output.json");
            File.WriteAllText(path, "{\"AutoPaste\":true,\"SaveToHistory\":true}");
            var store = new DictationOutputPreferencesStore(path);
            Assert.False(store.Current.SaveHistoryAudio); Assert.Null(store.Error);
            Assert.Null(store.Save(Enabled));
            Assert.True(new DictationOutputPreferencesStore(path).Current.SaveHistoryAudio);
            File.WriteAllText(path, "{\"AutoPaste\":true,\"SaveToHistory\":true,\"SaveHistoryAudio\":false,\"SaveHistoryAudio\":true}");
            var invalid = new DictationOutputPreferencesStore(path);
            Assert.NotNull(invalid.Error); Assert.False(invalid.Current.SaveHistoryAudio); Assert.False(invalid.Current.SaveToHistory);
        }
        finally { Directory.Delete(root, true); }
    }
}
