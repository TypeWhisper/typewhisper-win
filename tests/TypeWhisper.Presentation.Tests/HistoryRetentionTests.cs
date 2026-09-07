using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class HistoryRetentionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-retention-" + Guid.NewGuid());
    private string SettingsPath => Path.Combine(_directory, "retention.json");
    private string HistoryPath => Path.Combine(_directory, "history.json");
    private static HistoryRetentionPreferences Duration(int minutes) => new(HistoryRetentionMode.Duration, minutes);

    private HistoryService History()
    {
        Directory.CreateDirectory(_directory);
        var service = new HistoryService(HistoryPath) { ThrowOnLoadFailure = true };
        var now = DateTime.UtcNow;
        Assert.True(service.TryAddRecord(new TranscriptionRecord
        {
            Id = "old", Timestamp = now, CreatedAt = now.AddHours(-2), RawText = "old", FinalText = "old"
        }));
        Assert.True(service.TryAddRecord(new TranscriptionRecord
        {
            Id = "recent", Timestamp = now.AddDays(-100), CreatedAt = now.AddMinutes(-10), RawText = "recent", FinalText = "recent"
        }));
        return service;
    }

    [Fact]
    public async Task MissingSettingsDefaultToForeverWithoutReadingOrDeletingHistory()
    {
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        var store = new HistoryRetentionPreferencesStore(SettingsPath);
        Assert.Equal(HistoryRetentionMode.Forever, store.Current.HistoryRetentionMode);
        Assert.Null(await new HistoryRetentionController(history.Object, store).ApplyAsync());
        Assert.False(Directory.Exists(_directory));
        history.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"HistoryRetentionMode\":\"Duration\",\"HistoryRetentionMinutes\":0}")]
    [InlineData("{\"HistoryRetentionMode\":\"UntilAppCloses\",\"HistoryRetentionMinutes\":60}")]
    [InlineData("{\"HistoryRetentionMode\":\"Duration\",\"HistoryRetentionMinutes\":\"60\"}")]
    public async Task InvalidSettingsPauseDeletionAndExposeAnErrorWithoutRewriting(string json)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, json);
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        var store = new HistoryRetentionPreferencesStore(SettingsPath);
        Assert.False(store.CanApply);
        Assert.NotNull(await new HistoryRetentionController(history.Object, store).ApplyAsync());
        Assert.Equal(json, await File.ReadAllTextAsync(SettingsPath));
        history.VerifyNoOtherCalls();
    }

    [Fact]
    public void CutoffIsStrictAndUsesCreationTime()
    {
        var now = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(Duration(60).IsExpired(now.AddMinutes(-60), now));
        Assert.True(Duration(60).IsExpired(now.AddMinutes(-60).AddTicks(-1), now));
        Assert.False(new HistoryRetentionPreferences().IsExpired(now.AddYears(-50), now));
        Assert.False(Duration(0).IsExpired(now.AddYears(-50), now));
    }

    [Fact]
    public async Task ConfirmedDurationPurgesOldEntriesAndPersistsAcrossRestart()
    {
        var history = History();
        var store = new HistoryRetentionPreferencesStore(SettingsPath);
        var controller = new HistoryRetentionController(history, store);
        Assert.NotNull(await controller.ChangeAsync(Duration(60)));
        Assert.Equal(2, history.Records.Count);
        Assert.False(File.Exists(SettingsPath));
        Assert.Null(await controller.ChangeAsync(Duration(60), confirmedShortening: true));
        Assert.Equal("recent", Assert.Single(history.Records).Id);
        var restarted = new HistoryRetentionPreferencesStore(SettingsPath);
        Assert.Equal(Duration(60), restarted.Current);
        Assert.Equal("recent", Assert.Single(new HistoryService(HistoryPath).Records).Id);
        Assert.Null(await new HistoryRetentionController(new HistoryService(HistoryPath), restarted).ApplyAsync());
    }

    [Fact]
    public async Task FailedHistoryWriteIsReportedAndRetriedWithoutPretendingDeletionSucceeded()
    {
        var history = History();
        var store = new HistoryRetentionPreferencesStore(SettingsPath);
        Assert.Null(store.Save(Duration(60)));
        var bytes = await File.ReadAllBytesAsync(HistoryPath);
        File.Move(HistoryPath, HistoryPath + ".saved");
        Directory.CreateDirectory(HistoryPath);
        var controller = new HistoryRetentionController(history, store);
        Assert.NotNull(await controller.ApplyAsync());
        Assert.NotNull(controller.Error);
        Assert.Equal(2, history.Records.Count);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(HistoryPath + ".saved"));
        Directory.Delete(HistoryPath);
        File.Move(HistoryPath + ".saved", HistoryPath);
        Assert.Null(await controller.ApplyAsync());
        Assert.Null(controller.Error);
        Assert.Equal("recent", Assert.Single(new HistoryService(HistoryPath).Records).Id);
    }

    [Fact]
    public async Task FailedSettingsWriteDoesNotDisableThePreviouslySavedPolicy()
    {
        var history = History();
        var store = new HistoryRetentionPreferencesStore(SettingsPath);
        Assert.Null(store.Save(Duration(60)));
        File.Move(SettingsPath, SettingsPath + ".saved");
        Directory.CreateDirectory(SettingsPath);
        var controller = new HistoryRetentionController(history, store);
        Assert.NotNull(await controller.ChangeAsync(new HistoryRetentionPreferences()));
        Assert.Equal(Duration(60), store.Current);
        Assert.Equal(2, history.Records.Count);
        Assert.NotNull(await controller.ApplyAsync()); // The settings error remains visible even after a successful purge.
        Assert.Equal("recent", Assert.Single(history.Records).Id);
        Directory.Delete(SettingsPath);
        File.Move(SettingsPath + ".saved", SettingsPath);
        Assert.Equal(Duration(60), new HistoryRetentionPreferencesStore(SettingsPath).Current);
        Assert.Empty(Directory.GetFiles(_directory, ".history-retention-*.tmp"));
    }

    [Fact]
    public async Task SavingForeverStopsPurgingAndSurvivesRestart()
    {
        var history = History();
        var store = new HistoryRetentionPreferencesStore(SettingsPath);
        Assert.Null(store.Save(Duration(60)));
        Assert.Null(await new HistoryRetentionController(history, store).ChangeAsync(new HistoryRetentionPreferences()));
        var restarted = new HistoryRetentionPreferencesStore(SettingsPath);
        Assert.Null(await new HistoryRetentionController(history, restarted).ApplyAsync());
        Assert.Equal(2, history.Records.Count);
        Assert.Equal(HistoryRetentionMode.Forever, restarted.Current.HistoryRetentionMode);
    }

    [Fact]
    public void OnlyShorterValidDurationsRequireConfirmation()
    {
        Assert.True(Duration(60).RequiresConfirmationComparedTo(new()));
        Assert.True(Duration(60).RequiresConfirmationComparedTo(Duration(120)));
        Assert.False(Duration(120).RequiresConfirmationComparedTo(Duration(60)));
        Assert.False(Duration(60).RequiresConfirmationComparedTo(Duration(60)));
        Assert.False(new HistoryRetentionPreferences().RequiresConfirmationComparedTo(Duration(60)));
    }

    [Fact]
    public async Task ClosingDuringLoadDrainsActiveCallAndRejectsQueuedAndLateSettingsWrites()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        history.Setup(service => service.EnsureLoadedAsync()).Returns(() => { entered.SetResult(); return release.Task; });
        var store = new HistoryRetentionPreferencesStore(SettingsPath);
        Assert.Null(store.Save(Duration(60)));
        var original = File.ReadAllBytes(SettingsPath);
        var controller = new HistoryRetentionController(history.Object, store);
        var active = controller.ApplyAsync();
        await entered.Task;
        var queued = controller.ChangeAsync(new HistoryRetentionPreferences());
        var drain = controller.CloseAndDrainAsync();
        Assert.Same(drain, controller.CloseAndDrainAsync());
        Assert.False(drain.IsCompleted);
        Assert.NotNull(await controller.ChangeAsync(Duration(10), confirmedShortening: true));
        release.SetResult();
        Assert.NotNull(await active);
        Assert.NotNull(await queued);
        await drain;
        Assert.NotNull(await controller.ApplyAsync());
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        history.Verify(service => service.EnsureLoadedAsync(), Times.Once);
        history.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ClosingWaitsForAlreadyExecutingPurgeBeforeReturning()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        history.Setup(service => service.EnsureLoadedAsync()).Returns(Task.CompletedTask);
        history.SetupGet(service => service.Records).Returns(Array.Empty<TranscriptionRecord>());
        history.Setup(service => service.PurgeOldRecords(It.IsAny<TimeSpan?>())).Callback(() =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test purge barrier timed out.");
        });
        var store = new HistoryRetentionPreferencesStore(SettingsPath);
        Assert.Null(store.Save(Duration(60)));
        var controller = new HistoryRetentionController(history.Object, store);
        var active = Task.Run(controller.ApplyAsync);
        await entered.Task;
        var drain = controller.CloseAndDrainAsync();
        try { Assert.False(drain.IsCompleted); }
        finally { release.Set(); }
        await active;
        await drain;
        Assert.NotNull(await controller.ApplyAsync());
        history.Verify(service => service.PurgeOldRecords(It.IsAny<TimeSpan?>()), Times.Once);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
