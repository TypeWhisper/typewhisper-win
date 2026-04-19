using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public class HistoryRetentionCoordinatorTests
{
    [Fact]
    public void Initialize_DurationModePurgesOnStartup()
    {
        var history = new FakeHistoryService();
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            HistoryRetentionMode = HistoryRetentionMode.Duration,
            HistoryRetentionMinutes = 60
        });

        using var sut = new HistoryRetentionCoordinator(history, settings);
        sut.Initialize();

        Assert.Equal(1, history.PurgeCallCount);
        Assert.Equal(TimeSpan.FromHours(1), history.LastPurgeRetention);
    }

    [Fact]
    public void SettingsChange_DurationModePurgesImmediately()
    {
        var history = new FakeHistoryService();
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            HistoryRetentionMode = HistoryRetentionMode.Duration,
            HistoryRetentionMinutes = 60
        });

        using var sut = new HistoryRetentionCoordinator(history, settings);
        sut.Initialize();
        history.ResetCounters();

        settings.Save(settings.Current with { HistoryRetentionMinutes = 24 * 60 });

        Assert.Equal(1, history.PurgeCallCount);
        Assert.Equal(TimeSpan.FromDays(1), history.LastPurgeRetention);
    }

    [Fact]
    public void RecordsChanged_DurationModePurgesAfterHistoryWrite()
    {
        var history = new FakeHistoryService();
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            HistoryRetentionMode = HistoryRetentionMode.Duration,
            HistoryRetentionMinutes = 60
        });

        using var sut = new HistoryRetentionCoordinator(history, settings);
        sut.Initialize();
        history.ResetCounters();

        history.RaiseRecordsChanged();

        Assert.Equal(1, history.PurgeCallCount);
    }

    [Fact]
    public void ForeverMode_DoesNotPurgeOnStartupOrEvents()
    {
        var history = new FakeHistoryService();
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            HistoryRetentionMode = HistoryRetentionMode.Forever
        });

        using var sut = new HistoryRetentionCoordinator(history, settings);
        sut.Initialize();
        history.RaiseRecordsChanged();
        settings.Save(settings.Current with { HistoryRetentionMinutes = 60 });

        Assert.Equal(0, history.PurgeCallCount);
    }

    [Fact]
    public void UntilAppCloses_DoesNotClearImmediatelyWhenSelected()
    {
        var history = new FakeHistoryService();
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            HistoryRetentionMode = HistoryRetentionMode.Duration,
            HistoryRetentionMinutes = 60
        });

        using var sut = new HistoryRetentionCoordinator(history, settings);
        sut.Initialize();
        history.ResetCounters();

        settings.Save(settings.Current with { HistoryRetentionMode = HistoryRetentionMode.UntilAppCloses });

        Assert.Equal(0, history.ClearAllCallCount);
        Assert.Equal(0, history.PurgeCallCount);
    }

    [Fact]
    public void HandleShutdown_UntilAppCloses_ClearsHistory()
    {
        var history = new FakeHistoryService();
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            HistoryRetentionMode = HistoryRetentionMode.UntilAppCloses
        });

        using var sut = new HistoryRetentionCoordinator(history, settings);
        sut.Initialize();
        history.ResetCounters();

        sut.HandleShutdown();

        Assert.Equal(1, history.ClearAllCallCount);
    }

    [Fact]
    public void Initialize_UntilAppCloses_ClearsHistoryForCrashRecovery()
    {
        var history = new FakeHistoryService();
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            HistoryRetentionMode = HistoryRetentionMode.UntilAppCloses
        });

        using var sut = new HistoryRetentionCoordinator(history, settings);
        sut.Initialize();

        Assert.Equal(1, history.ClearAllCallCount);
    }

    [Fact]
    public void SelfTriggeredRecordsChanged_DoesNotReenterPurge()
    {
        var history = new FakeHistoryService { RaiseRecordsChangedOnPurge = true };
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            HistoryRetentionMode = HistoryRetentionMode.Duration,
            HistoryRetentionMinutes = 60
        });

        using var sut = new HistoryRetentionCoordinator(history, settings);
        sut.Initialize();

        Assert.Equal(1, history.PurgeCallCount);
    }

    private sealed class FakeSettingsService(AppSettings initialSettings) : ISettingsService
    {
        public AppSettings Current { get; private set; } = initialSettings;
        public event Action<AppSettings>? SettingsChanged;

        public AppSettings Load() => Current;

        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }
    }

    private sealed class FakeHistoryService : IHistoryService
    {
        public IReadOnlyList<TranscriptionRecord> Records => [];
        public event Action? RecordsChanged;
        public int TotalRecords => 0;
        public int TotalWords => 0;
        public double TotalDuration => 0;

        public int PurgeCallCount { get; private set; }
        public int ClearAllCallCount { get; private set; }
        public TimeSpan? LastPurgeRetention { get; private set; }
        public bool RaiseRecordsChangedOnPurge { get; init; }

        public void AddRecord(TranscriptionRecord record) => RecordsChanged?.Invoke();
        public void UpdateRecord(string id, string finalText) { }
        public void DeleteRecord(string id) => RecordsChanged?.Invoke();

        public void ClearAll()
        {
            ClearAllCallCount++;
            RecordsChanged?.Invoke();
        }

        public IReadOnlyList<TranscriptionRecord> Search(string query) => [];

        public void PurgeOldRecords(TimeSpan? retention)
        {
            PurgeCallCount++;
            LastPurgeRetention = retention;
            if (RaiseRecordsChangedOnPurge)
                RecordsChanged?.Invoke();
        }

        public Task EnsureLoadedAsync() => Task.CompletedTask;
        public IReadOnlyList<string> GetDistinctApps() => [];
        public string ExportToText(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null) => string.Empty;
        public string ExportToCsv(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null) => string.Empty;
        public string ExportToMarkdown(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null) => string.Empty;
        public string ExportToJson(IReadOnlyList<TranscriptionRecord> records) => "[]";

        public void RaiseRecordsChanged() => RecordsChanged?.Invoke();

        public void ResetCounters()
        {
            PurgeCallCount = 0;
            ClearAllCallCount = 0;
            LastPurgeRetention = null;
        }
    }
}
