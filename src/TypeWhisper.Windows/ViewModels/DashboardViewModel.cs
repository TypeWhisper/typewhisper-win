using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Windows.ViewModels;

public record ActivityDataPoint(DateTime Date, int WordCount);

public partial class DashboardViewModel : ObservableObject
{
    private readonly IHistoryService _history;

    [ObservableProperty] private bool _isMonthView;

    [ObservableProperty] private int _wordsCount;
    [ObservableProperty] private string _averageWpm = "0";
    [ObservableProperty] private int _appsUsed;
    [ObservableProperty] private string _timeSaved = "0m";

    public ObservableCollection<ActivityDataPoint> ChartData { get; } = [];
    [ObservableProperty] private int _chartMaxValue = 1;

    public DashboardViewModel(IHistoryService history)
    {
        _history = history;
        _history.RecordsChanged += () =>
            Application.Current?.Dispatcher.Invoke(Refresh);
        Refresh();
    }

    partial void OnIsMonthViewChanged(bool value) => Refresh();

    public void Refresh()
    {
        var now = DateTime.UtcNow.Date;
        int days = IsMonthView ? 30 : 7;
        var cutoff = now.AddDays(-(days - 1));

        var records = _history.Records
            .Where(r => r.Timestamp.Date >= cutoff)
            .ToList();

        // Stat cards
        WordsCount = records.Sum(r => r.WordCount);

        var totalSeconds = records.Sum(r => r.DurationSeconds);
        AverageWpm = totalSeconds > 0
            ? ((int)Math.Round(WordsCount / (totalSeconds / 60.0))).ToString()
            : "0";

        AppsUsed = records
            .Where(r => r.AppProcessName is not null)
            .Select(r => r.AppProcessName!)
            .Distinct()
            .Count();

        double typingMinutes = WordsCount / 45.0;
        double speakingMinutes = totalSeconds / 60.0;
        TimeSaved = FormatTimeSaved(typingMinutes - speakingMinutes);

        // Chart: one bar per day
        ChartData.Clear();
        var grouped = records
            .GroupBy(r => r.Timestamp.Date)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.WordCount));

        int max = 0;
        for (int i = 0; i < days; i++)
        {
            var date = cutoff.AddDays(i);
            int wc = grouped.GetValueOrDefault(date);
            if (wc > max) max = wc;
            ChartData.Add(new ActivityDataPoint(date, wc));
        }
        ChartMaxValue = max > 0 ? max : 1;
    }

    static string FormatTimeSaved(double minutes)
    {
        if (minutes <= 0) return "0m";
        int total = (int)Math.Round(minutes);
        if (total < 60) return $"{total}m";
        int h = total / 60, m = total % 60;
        return m > 0 ? $"{h}h {m}m" : $"{h}h";
    }
}
