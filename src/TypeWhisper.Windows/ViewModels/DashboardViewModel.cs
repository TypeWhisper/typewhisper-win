using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Windows.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly IHistoryService _history;

    [ObservableProperty] private int _totalRecords;
    [ObservableProperty] private int _totalWords;
    [ObservableProperty] private string _totalDuration = "0:00";
    [ObservableProperty] private int _recordsToday;
    [ObservableProperty] private int _recordsThisWeek;
    [ObservableProperty] private string? _mostUsedLanguage;
    [ObservableProperty] private string? _mostUsedApp;

    public DashboardViewModel(IHistoryService history)
    {
        _history = history;
        _history.RecordsChanged += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        var records = _history.Records;

        TotalRecords = records.Count;
        TotalWords = _history.TotalWords;

        var dur = TimeSpan.FromSeconds(_history.TotalDuration);
        TotalDuration = dur.TotalHours >= 1
            ? $"{(int)dur.TotalHours}:{dur.Minutes:D2}:{dur.Seconds:D2}"
            : $"{dur.Minutes}:{dur.Seconds:D2}";

        var today = DateTime.UtcNow.Date;
        RecordsToday = records.Count(r => r.Timestamp.Date == today);

        var weekStart = today.AddDays(-(int)today.DayOfWeek + (int)DayOfWeek.Monday);
        if (today.DayOfWeek == DayOfWeek.Sunday) weekStart = weekStart.AddDays(-7);
        RecordsThisWeek = records.Count(r => r.Timestamp.Date >= weekStart);

        MostUsedLanguage = records
            .Where(r => r.Language is not null)
            .GroupBy(r => r.Language!)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;

        MostUsedApp = records
            .Where(r => r.AppProcessName is not null)
            .GroupBy(r => r.AppProcessName!)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;
    }
}
