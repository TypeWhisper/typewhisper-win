using System.IO;
using TypeWhisper.Core.Services;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class StatisticsViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "TypeWhisper-StatisticsViewModelTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Refresh_BuildsMacParitySectionsFromPersistentStatistics()
    {
        var now = DateTime.SpecifyKind(new DateTime(2026, 8, 8, 16, 0, 0), DateTimeKind.Local);
        var service = CreateService();
        Record(service, now.AddDays(-2).Date.AddHours(9), 45, "notepad", "whisper", "large-v3");
        Record(service, now.AddDays(-1).Date.AddHours(10), 90, "code", "parakeet", "parakeet-fast");
        Record(service, now.Date.AddHours(10), 135, "code", "parakeet", "parakeet-fast");

        var viewModel = new StatisticsViewModel(service, () => now)
        {
            SelectedPeriod = StatisticsPeriod.Week
        };

        Assert.True(viewModel.HasAnyData);
        Assert.True(viewModel.HasPeriodActivity);
        Assert.Equal(3, viewModel.TotalDaysActive);
        Assert.Equal(3, viewModel.CurrentStreak);
        Assert.Equal(3, viewModel.LongestStreak);
        Assert.Equal(3, viewModel.TotalTranscriptions);
        Assert.Equal(270, viewModel.WordsCount);
        Assert.Equal(2, viewModel.AppsUsed);
        Assert.Equal(7, viewModel.ChartData.Count);
        Assert.Equal(2, viewModel.AppUsageStats.Count);
        Assert.Equal(2, viewModel.ModelUsageStats.Count);
        Assert.Equal(7, viewModel.HeatmapRows.Count);
        Assert.Equal(3, viewModel.HeatmapRows.SelectMany(row => row.Cells).Sum(cell => cell.Count));
        Assert.Equal(2, viewModel.HeatmapRows.SelectMany(row => row.Cells).Count(cell => cell.Hour == 10 && cell.Count == 1));
    }

    [Fact]
    public void ClickCommands_PinAndToggleDisplayedDetails()
    {
        var now = DateTime.SpecifyKind(new DateTime(2026, 8, 8, 16, 0, 0), DateTimeKind.Local);
        var service = CreateService();
        Record(service, now.Date.AddHours(14), 80, "notepad", "whisper", "large-v3");
        var viewModel = new StatisticsViewModel(service, () => now)
        {
            SelectedPeriod = StatisticsPeriod.Week
        };

        var activity = viewModel.ChartData.Single(point => point.WordCount > 0);
        viewModel.SelectActivityPointCommand.Execute(activity);
        Assert.True(activity.IsSelected);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.SelectedActivityDetail));
        viewModel.SelectActivityPointCommand.Execute(activity);
        Assert.False(activity.IsSelected);
        Assert.Empty(viewModel.SelectedActivityDetail);

        var heatmap = viewModel.HeatmapRows.SelectMany(row => row.Cells).Single(cell => cell.Count == 1);
        viewModel.SelectHeatmapCellCommand.Execute(heatmap);
        Assert.True(heatmap.IsSelected);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.SelectedHeatmapDetail));

        viewModel.SelectedAppStat = Assert.Single(viewModel.AppUsageStats);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.SelectedAppDetail));

        viewModel.ShowMetricDetailCommand.Execute("TimeSaved");
        Assert.False(string.IsNullOrWhiteSpace(viewModel.SelectedMetricDetail));
    }

    [Fact]
    public void AllTimeChart_BucketsLongRangesWithoutHorizontalScrolling()
    {
        var now = DateTime.SpecifyKind(new DateTime(2026, 8, 8, 16, 0, 0), DateTimeKind.Local);
        var service = CreateService();
        for (var offset = 0; offset < 120; offset++)
            Record(service, now.Date.AddDays(-offset).AddHours(8), 10, "notepad", "whisper", "large-v3");

        var viewModel = new StatisticsViewModel(service, () => now);

        Assert.InRange(viewModel.ChartData.Count, 1, 48);
        Assert.Equal(1200, viewModel.ChartData.Sum(point => point.WordCount));
    }

    private UsageStatisticsService CreateService() =>
        new(Path.Combine(_directory, "usage-statistics.json"));

    private static void Record(
        UsageStatisticsService service,
        DateTime timestamp,
        int words,
        string app,
        string engine,
        string model) => service.RecordTranscription(
            timestamp,
            words,
            durationSeconds: 60,
            app,
            app,
            engine,
            model);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
