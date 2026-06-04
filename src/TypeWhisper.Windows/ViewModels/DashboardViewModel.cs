using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Represents activity data point data.
/// </summary>
/// <param name="Date">Date supplied to the member.</param>
/// <param name="WordCount">Word count supplied to the member.</param>
public record ActivityDataPoint(DateTime Date, int WordCount);

/// <summary>
/// Represents recent transcription data.
/// </summary>
/// <param name="Preview">Preview supplied to the member.</param>
/// <param name="TimeAgo">Time ago supplied to the member.</param>
/// <param name="AppName">App name supplied to the member.</param>
public record RecentTranscription(string Preview, string TimeAgo, string? AppName);

/// <summary>
/// Provides dashboard view model behavior.
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly IHistoryService _history;

    // 0 = week, 1 = month, 2 = all time
    [ObservableProperty] private int _selectedPeriod;

    [ObservableProperty] private int _wordsCount;
    [ObservableProperty] private string _averageWpm = "0";
    [ObservableProperty] private int _appsUsed;
    [ObservableProperty] private string _timeSaved = "0m";

    [ObservableProperty] private string _wordsTrend = "";
    [ObservableProperty] private string _wpmTrend = "";
    [ObservableProperty] private string _appsTrend = "";
    [ObservableProperty] private string _timeTrend = "";

    /// <summary>
    /// Gets the chart data.
    /// </summary>
    public ObservableCollection<ActivityDataPoint> ChartData { get; } = [];
    [ObservableProperty] private int _chartMaxValue = 1;

    /// <summary>
    /// Gets the recent transcriptions.
    /// </summary>
    public ObservableCollection<RecentTranscription> RecentTranscriptions { get; } = [];
    [ObservableProperty] private bool _hasRecentTranscriptions;

    // Backward compat
    /// <summary>
    /// Gets whether is month view.
    /// </summary>
    public bool IsMonthView
    {
        get => SelectedPeriod == 1;
        set => SelectedPeriod = value ? 1 : 0;
    }

    /// <summary>
    /// Initializes a new instance of the DashboardViewModel class.
    /// </summary>
    public DashboardViewModel(IHistoryService history)
    {
        _history = history;
        _history.RecordsChanged += () =>
            Application.Current?.Dispatcher.Invoke(Refresh);
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        await _history.EnsureLoadedAsync().ConfigureAwait(false);
        Application.Current?.Dispatcher.Invoke(Refresh);
    }

    partial void OnSelectedPeriodChanged(int value) => Refresh();

    /// <summary>
    /// Refreshes
    /// </summary>
    public void Refresh()
    {
        var now = DateTime.UtcNow.Date;
        int days = SelectedPeriod switch { 0 => 7, 1 => 30, _ => 0 };
        var isAllTime = SelectedPeriod == 2;

        var cutoff = isAllTime ? DateTime.MinValue : now.AddDays(-(days - 1));
        var prevCutoff = isAllTime ? DateTime.MinValue : cutoff.AddDays(-days);

        var records = _history.Records
            .Where(r => r.Timestamp.Date >= cutoff)
            .ToList();

        var prevRecords = isAllTime
            ? []
            : _history.Records
                .Where(r => r.Timestamp.Date >= prevCutoff && r.Timestamp.Date < cutoff)
                .ToList();

        // Stat cards
        WordsCount = records.Sum(r => r.WordCount);
        var prevWords = prevRecords.Sum(r => r.WordCount);
        WordsTrend = FormatTrend(WordsCount, prevWords, isAllTime);

        var totalSeconds = records.Sum(r => r.DurationSeconds);
        var wpm = totalSeconds > 0 ? (int)Math.Round(WordsCount / (totalSeconds / 60.0)) : 0;
        AverageWpm = wpm.ToString();
        var prevSeconds = prevRecords.Sum(r => r.DurationSeconds);
        var prevWpm = prevSeconds > 0 ? (int)Math.Round(prevWords / (prevSeconds / 60.0)) : 0;
        WpmTrend = FormatTrend(wpm, prevWpm, isAllTime);

        AppsUsed = records
            .Where(r => r.AppProcessName is not null)
            .Select(r => r.AppProcessName!)
            .Distinct()
            .Count();
        var prevApps = prevRecords
            .Where(r => r.AppProcessName is not null)
            .Select(r => r.AppProcessName!)
            .Distinct()
            .Count();
        AppsTrend = FormatTrend(AppsUsed, prevApps, isAllTime);

        double typingMinutes = WordsCount / 45.0;
        double speakingMinutes = totalSeconds / 60.0;
        var savedMinutes = typingMinutes - speakingMinutes;
        TimeSaved = FormatTimeSaved(savedMinutes);
        double prevTyping = prevWords / 45.0;
        double prevSpeaking = prevSeconds / 60.0;
        TimeTrend = FormatTrend((int)savedMinutes, (int)(prevTyping - prevSpeaking), isAllTime);

        // Chart
        ChartData.Clear();
        int chartDays = isAllTime ? Math.Min(90, (int)(now - (_history.Records.LastOrDefault()?.Timestamp.Date ?? now)).TotalDays + 1) : days;
        var chartCutoff = now.AddDays(-(chartDays - 1));
        var allInRange = _history.Records.Where(r => r.Timestamp.Date >= chartCutoff).ToList();
        var grouped = allInRange
            .GroupBy(r => r.Timestamp.Date)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.WordCount));

        int max = 0;
        for (int i = 0; i < chartDays; i++)
        {
            var date = chartCutoff.AddDays(i);
            int wc = grouped.GetValueOrDefault(date);
            if (wc > max) max = wc;
            ChartData.Add(new ActivityDataPoint(date, wc));
        }
        ChartMaxValue = max > 0 ? max : 1;

        // Recent transcriptions
        RecentTranscriptions.Clear();
        foreach (var r in _history.Records.Take(3))
        {
            var preview = r.FinalText.Length > 80
                ? string.Concat(r.FinalText.AsSpan(0, 80), "...")
                : r.FinalText;
            RecentTranscriptions.Add(new RecentTranscription(preview, FormatTimeAgo(r.Timestamp), r.AppProcessName));
        }
        HasRecentTranscriptions = RecentTranscriptions.Count > 0;
    }

    static string FormatTrend(int current, int previous, bool isAllTime)
    {
        if (isAllTime || previous == 0) return "";
        var diff = current - previous;
        if (diff == 0) return "";
        var pct = (int)Math.Round((double)diff / previous * 100);
        return diff > 0 ? $"+{pct}%" : $"{pct}%";
    }

    static string FormatTimeSaved(double minutes)
    {
        if (minutes <= 0) return "0m";
        int total = (int)Math.Round(minutes);
        if (total < 60) return $"{total}m";
        int h = total / 60, m = total % 60;
        return m > 0 ? $"{h}h {m}m" : $"{h}h";
    }

    static string FormatTimeAgo(DateTime timestamp)
    {
        var diff = DateTime.UtcNow - timestamp;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
        return timestamp.ToString("dd.MM");
    }
}
