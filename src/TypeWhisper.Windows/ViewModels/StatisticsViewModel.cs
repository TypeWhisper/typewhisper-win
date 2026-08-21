using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Lists the supported statistics periods.
/// </summary>
public enum StatisticsPeriod
{
    /// <summary>
    /// Represents the current seven-day period.
    /// </summary>
    Week,
    /// <summary>
    /// Represents the current thirty-day period.
    /// </summary>
    Month,
    /// <summary>
    /// Represents all recorded statistics.
    /// </summary>
    AllTime
}

/// <summary>
/// Represents one activity chart bar.
/// </summary>
public sealed partial class StatisticsActivityPoint : ObservableObject
{
    /// <summary>
    /// Gets the local date.
    /// </summary>
    public required DateTime Date { get; init; }
    /// <summary>
    /// Gets the word count.
    /// </summary>
    public required int WordCount { get; init; }
    /// <summary>
    /// Gets the axis label.
    /// </summary>
    public required string AxisLabel { get; init; }
    /// <summary>
    /// Gets the hover detail.
    /// </summary>
    public required string Tooltip { get; init; }
    /// <summary>
    /// Gets the click detail.
    /// </summary>
    public required string Detail { get; init; }
    /// <summary>
    /// Gets whether the axis label is shown.
    /// </summary>
    public required bool ShowAxisLabel { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Represents an application or model usage row.
/// </summary>
public sealed record StatisticsUsageStat(
    string Id,
    string Label,
    int Count,
    double Percentage,
    string Tooltip,
    string Detail);

/// <summary>
/// Represents one clickable heatmap cell.
/// </summary>
public sealed partial class StatisticsHeatmapCell : ObservableObject
{
    /// <summary>
    /// Gets the zero-based Monday-first weekday index.
    /// </summary>
    public required int WeekdayIndex { get; init; }
    /// <summary>
    /// Gets the hour.
    /// </summary>
    public required int Hour { get; init; }
    /// <summary>
    /// Gets the dictation count.
    /// </summary>
    public required int Count { get; init; }
    /// <summary>
    /// Gets the normalized fill opacity.
    /// </summary>
    public required double Intensity { get; init; }
    /// <summary>
    /// Gets the hover detail.
    /// </summary>
    public required string Tooltip { get; init; }
    /// <summary>
    /// Gets the click detail.
    /// </summary>
    public required string Detail { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Represents one weekday row in the activity heatmap.
/// </summary>
public sealed record StatisticsHeatmapRow(
    string Label,
    IReadOnlyList<StatisticsHeatmapCell> Cells);

/// <summary>
/// Provides the WPF statistics page state and interactions.
/// </summary>
public sealed partial class StatisticsViewModel : ObservableObject
{
    private const int WeekLength = 7;
    private const int MonthLength = 30;

    private readonly IUsageStatisticsService _statistics;
    private readonly Func<DateTime> _now;
    private readonly Func<IReadOnlyList<ITranscriptionEnginePlugin>> _transcriptionEngines;
    private readonly PluginManager? _pluginManager;

    [ObservableProperty] private StatisticsPeriod _selectedPeriod = StatisticsPeriod.AllTime;
    [ObservableProperty] private bool _hasAnyData;
    [ObservableProperty] private bool _hasPeriodActivity;
    [ObservableProperty] private int _totalDaysActive;
    [ObservableProperty] private int _currentStreak;
    [ObservableProperty] private int _longestStreak;
    [ObservableProperty] private int _totalTranscriptions;
    [ObservableProperty] private int _wordsCount;
    [ObservableProperty] private string _averageWpm = "-";
    [ObservableProperty] private int _appsUsed;
    [ObservableProperty] private string _timeSaved = "-";
    [ObservableProperty] private string _wordsTrend = string.Empty;
    [ObservableProperty] private string _wpmTrend = string.Empty;
    [ObservableProperty] private string _appsTrend = string.Empty;
    [ObservableProperty] private string _timeSavedTrend = string.Empty;
    [ObservableProperty] private bool _wordsTrendIsNegative;
    [ObservableProperty] private bool _wpmTrendIsNegative;
    [ObservableProperty] private bool _appsTrendIsNegative;
    [ObservableProperty] private bool _timeSavedTrendIsNegative;
    [ObservableProperty] private int _chartMaxValue = 1;
    [ObservableProperty] private string _selectedMetricDetail = string.Empty;
    [ObservableProperty] private string _selectedActivityDetail = string.Empty;
    [ObservableProperty] private string _selectedHeatmapDetail = string.Empty;
    [ObservableProperty] private StatisticsUsageStat? _selectedAppStat;
    [ObservableProperty] private StatisticsUsageStat? _selectedModelStat;

    /// <summary>
    /// Gets the activity chart data.
    /// </summary>
    public ObservableCollection<StatisticsActivityPoint> ChartData { get; } = [];
    /// <summary>
    /// Gets the most-used applications.
    /// </summary>
    public ObservableCollection<StatisticsUsageStat> AppUsageStats { get; } = [];
    /// <summary>
    /// Gets the transcription models used.
    /// </summary>
    public ObservableCollection<StatisticsUsageStat> ModelUsageStats { get; } = [];
    /// <summary>
    /// Gets the Monday-first time-of-day heatmap rows.
    /// </summary>
    public ObservableCollection<StatisticsHeatmapRow> HeatmapRows { get; } = [];

    /// <summary>
    /// Gets the compact hour labels used above the heatmap.
    /// </summary>
    public IReadOnlyList<string> HeatmapHourLabels { get; } = Enumerable.Range(0, 24)
        .Select(hour => hour % 6 == 0 ? hour.ToString(CultureInfo.InvariantCulture) : string.Empty)
        .ToArray();

    /// <summary>
    /// Initializes the statistics view model.
    /// </summary>
    public StatisticsViewModel(IUsageStatisticsService statistics, PluginManager pluginManager)
        : this(
            statistics,
            () => Program.UiAutomation.IsEnabled
                ? Program.UiAutomation.ReferenceUtc.ToLocalTime()
                : DateTime.Now,
            () => pluginManager.TranscriptionEngines)
    {
        _pluginManager = pluginManager;
        _pluginManager.PluginStateChanged += OnPluginStateChanged;
    }

    internal StatisticsViewModel(
        IUsageStatisticsService statistics,
        Func<DateTime> now,
        Func<IReadOnlyList<ITranscriptionEnginePlugin>>? transcriptionEngines = null)
    {
        _statistics = statistics;
        _now = now;
        _transcriptionEngines = transcriptionEngines ?? (() => []);
        _statistics.StatisticsChanged += OnStatisticsChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        Refresh();
    }

    partial void OnSelectedPeriodChanged(StatisticsPeriod value) => Refresh();

    partial void OnSelectedAppStatChanged(StatisticsUsageStat? value) =>
        OnPropertyChanged(nameof(SelectedAppDetail));

    partial void OnSelectedModelStatChanged(StatisticsUsageStat? value) =>
        OnPropertyChanged(nameof(SelectedModelDetail));

    /// <summary>
    /// Gets the selected application detail.
    /// </summary>
    public string SelectedAppDetail => SelectedAppStat?.Detail ?? string.Empty;

    /// <summary>
    /// Gets the selected model detail.
    /// </summary>
    public string SelectedModelDetail => SelectedModelStat?.Detail ?? string.Empty;

    /// <summary>
    /// Rebuilds all statistics sections from the persisted daily snapshots.
    /// </summary>
    public void Refresh()
    {
        var now = _now();
        var today = now.Date;
        var allDays = _statistics.Days
            .Where(IsActiveDay)
            .OrderBy(day => day.Day)
            .ToArray();
        var periodLength = GetPeriodLength(SelectedPeriod);
        var periodStart = periodLength is int length
            ? today.AddDays(-(length - 1))
            : allDays.FirstOrDefault()?.Day.Date ?? today;
        var periodDays = allDays
            .Where(day => day.Day.Date >= periodStart && day.Day.Date <= today)
            .ToArray();

        HasAnyData = _statistics.HasAnyStatistics;
        HasPeriodActivity = periodDays.Length > 0;
        TotalDaysActive = periodDays.Length;
        TotalTranscriptions = periodDays.Sum(day => day.TranscriptionCount);
        WordsCount = periodDays.Sum(day => day.TotalWords);

        var streaks = ComputeStreaks(periodDays, today);
        CurrentStreak = streaks.Current;
        LongestStreak = streaks.Longest;

        var currentSummary = Summarize(periodDays);
        AverageWpm = currentSummary.RawWpm > 0
            ? ((int)currentSummary.RawWpm).ToString(CultureInfo.CurrentCulture)
            : "-";
        AppsUsed = currentSummary.AppCount;
        TimeSaved = FormatTimeSaved(currentSummary.RawSavedMinutes);

        ApplyTrends(allDays, periodLength, periodStart, currentSummary);
        BuildChart(allDays, periodStart, today);
        BuildAppStats(periodDays);
        BuildModelStats(periodDays);
        BuildHeatmap(periodDays);

        SelectedMetricDetail = string.Empty;
        SelectedActivityDetail = string.Empty;
        SelectedHeatmapDetail = string.Empty;
        SelectedAppStat = null;
        SelectedModelStat = null;
    }

    [RelayCommand]
    private void SelectActivityPoint(StatisticsActivityPoint? point)
    {
        foreach (var item in ChartData)
            item.IsSelected = ReferenceEquals(item, point) && !item.IsSelected;

        var selected = ChartData.FirstOrDefault(item => item.IsSelected);
        SelectedActivityDetail = selected?.Detail ?? string.Empty;
    }

    [RelayCommand]
    private void SelectHeatmapCell(StatisticsHeatmapCell? cell)
    {
        foreach (var row in HeatmapRows)
        {
            foreach (var item in row.Cells)
                item.IsSelected = ReferenceEquals(item, cell) && !item.IsSelected;
        }

        var selected = HeatmapRows
            .SelectMany(row => row.Cells)
            .FirstOrDefault(item => item.IsSelected);
        SelectedHeatmapDetail = selected?.Detail ?? string.Empty;
    }

    [RelayCommand]
    private void ShowMetricDetail(string? metric)
    {
        if (string.IsNullOrWhiteSpace(metric))
            return;

        SelectedMetricDetail = Loc.Instance.GetString($"Statistics.MetricDetail.{metric}");
    }

    private void BuildChart(
        IReadOnlyList<UsageStatisticsDaySnapshot> allDays,
        DateTime periodStart,
        DateTime today)
    {
        var wordCounts = allDays.ToDictionary(day => day.Day.Date, day => day.TotalWords);
        var chartStart = SelectedPeriod == StatisticsPeriod.AllTime && allDays.Count > 0
            ? allDays[0].Day.Date
            : periodStart;
        var dayCount = Math.Max(1, (today - chartStart).Days + 1);
        var bucketSize = SelectedPeriod == StatisticsPeriod.AllTime && dayCount > 60
            ? (int)Math.Ceiling(dayCount / 48d)
            : 1;
        var pointCount = (int)Math.Ceiling(dayCount / (double)bucketSize);
        var labelStride = SelectedPeriod switch
        {
            StatisticsPeriod.Week => 1,
            StatisticsPeriod.Month => 5,
            _ => Math.Max(1, (int)Math.Ceiling(pointCount / 8d))
        };
        var culture = ResolveCulture();

        ChartData.Clear();
        var max = 0;
        for (var index = 0; index < pointCount; index++)
        {
            var date = chartStart.AddDays(index * bucketSize);
            var endDate = DateTime.Compare(date.AddDays(bucketSize - 1), today) > 0
                ? today
                : date.AddDays(bucketSize - 1);
            var words = 0;
            for (var cursor = date; cursor <= endDate; cursor = cursor.AddDays(1))
                words += wordCounts.GetValueOrDefault(cursor);
            max = Math.Max(max, words);
            var wordsText = Loc.Instance.GetString("Statistics.WordsFormat", words);
            var dateText = date == endDate
                ? date.ToString("d MMMM yyyy", culture)
                : Loc.Instance.GetString(
                    "Statistics.DateRangeFormat",
                    date.ToString("d MMM yyyy", culture),
                    endDate.ToString("d MMM yyyy", culture));
            ChartData.Add(new StatisticsActivityPoint
            {
                Date = date,
                WordCount = words,
                AxisLabel = date.ToString(SelectedPeriod == StatisticsPeriod.Week ? "ddd" : "d MMM", culture),
                Tooltip = string.Concat(wordsText, Environment.NewLine, dateText),
                Detail = Loc.Instance.GetString("Statistics.ActivityDetailFormat", dateText, wordsText),
                ShowAxisLabel = index % labelStride == 0 || index == pointCount - 1
            });
        }

        ChartMaxValue = Math.Max(1, max);
    }

    private void BuildAppStats(IReadOnlyList<UsageStatisticsDaySnapshot> days)
    {
        var counts = MergeCounts(days.Select(day => day.AppCounts));
        var total = counts.Values.Sum();
        AppUsageStats.Clear();
        if (total <= 0)
            return;

        foreach (var pair in counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(8))
        {
            var label = FormatAppName(pair.Key);
            var percentage = pair.Value * 100d / total;
            var countText = Loc.Instance.GetString("Statistics.DictationsFormat", pair.Value);
            AppUsageStats.Add(new StatisticsUsageStat(
                pair.Key,
                label,
                pair.Value,
                percentage,
                string.Concat(label, Environment.NewLine, countText),
                Loc.Instance.GetString("Statistics.BreakdownDetailFormat", label, countText, (int)Math.Round(percentage))));
        }
    }

    private void BuildModelStats(IReadOnlyList<UsageStatisticsDaySnapshot> days)
    {
        var rawCounts = MergeCounts(days.Select(day => day.ModelCounts));
        var counts = rawCounts
            .GroupBy(pair => FormatModelLabel(pair.Key), StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value));
        var total = counts.Values.Sum();
        ModelUsageStats.Clear();
        if (total <= 0)
            return;

        foreach (var pair in counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key))
        {
            var percentage = pair.Value * 100d / total;
            var countText = Loc.Instance.GetString("Statistics.DictationsFormat", pair.Value);
            ModelUsageStats.Add(new StatisticsUsageStat(
                pair.Key,
                pair.Key,
                pair.Value,
                percentage,
                string.Concat(pair.Key, Environment.NewLine, countText),
                Loc.Instance.GetString("Statistics.BreakdownDetailFormat", pair.Key, countText, (int)Math.Round(percentage))));
        }
    }

    private void BuildHeatmap(IReadOnlyList<UsageStatisticsDaySnapshot> days)
    {
        var grid = new int[7, 24];
        foreach (var day in days)
        {
            var weekday = day.Day.DayOfWeek == DayOfWeek.Sunday
                ? 6
                : (int)day.Day.DayOfWeek - 1;
            for (var hour = 0; hour < Math.Min(24, day.HourCounts.Count); hour++)
                grid[weekday, hour] += day.HourCounts[hour];
        }

        var max = grid.Cast<int>().DefaultIfEmpty().Max();
        var labels = new[]
        {
            "Statistics.Weekday.Mon",
            "Statistics.Weekday.Tue",
            "Statistics.Weekday.Wed",
            "Statistics.Weekday.Thu",
            "Statistics.Weekday.Fri",
            "Statistics.Weekday.Sat",
            "Statistics.Weekday.Sun"
        };

        HeatmapRows.Clear();
        for (var weekday = 0; weekday < 7; weekday++)
        {
            var label = Loc.Instance.GetString(labels[weekday]);
            var cells = new List<StatisticsHeatmapCell>(24);
            for (var hour = 0; hour < 24; hour++)
            {
                var count = grid[weekday, hour];
                var countText = Loc.Instance.GetString("Statistics.DictationsFormat", count);
                var timeText = $"{hour:00}:00";
                cells.Add(new StatisticsHeatmapCell
                {
                    WeekdayIndex = weekday,
                    Hour = hour,
                    Count = count,
                    Intensity = count > 0 && max > 0 ? 0.18 + count / (double)max * 0.82 : 0,
                    Tooltip = string.Concat(label, ", ", timeText, Environment.NewLine, countText),
                    Detail = Loc.Instance.GetString("Statistics.HeatmapDetailFormat", label, timeText, countText)
                });
            }

            HeatmapRows.Add(new StatisticsHeatmapRow(label, cells));
        }
    }

    private void ApplyTrends(
        IReadOnlyList<UsageStatisticsDaySnapshot> allDays,
        int? periodLength,
        DateTime periodStart,
        StatisticsSummary current)
    {
        if (periodLength is not int length)
        {
            SetTrends(null, null, null, null);
            return;
        }

        var previousStart = periodStart.AddDays(-length);
        var previousDays = allDays
            .Where(day => day.Day.Date >= previousStart && day.Day.Date < periodStart)
            .ToArray();
        var previous = Summarize(previousDays);
        SetTrends(
            TrendPercent(current.Words, previous.Words),
            TrendPercent(current.RawWpm, previous.RawWpm),
            TrendPercent(current.AppCount, previous.AppCount),
            TrendPercent(current.RawSavedMinutes, previous.RawSavedMinutes));
    }

    private void SetTrends(double? words, double? wpm, double? apps, double? timeSaved)
    {
        (WordsTrend, WordsTrendIsNegative) = FormatTrend(words);
        (WpmTrend, WpmTrendIsNegative) = FormatTrend(wpm);
        (AppsTrend, AppsTrendIsNegative) = FormatTrend(apps);
        (TimeSavedTrend, TimeSavedTrendIsNegative) = FormatTrend(timeSaved);
    }

    internal static double? TrendPercent(double current, double previous) =>
        previous > 0 ? (current - previous) / previous * 100 : null;

    private static (int Current, int Longest) ComputeStreaks(
        IReadOnlyList<UsageStatisticsDaySnapshot> days,
        DateTime today)
    {
        if (days.Count == 0)
            return (0, 0);

        var active = days.Select(day => day.Day.Date).ToHashSet();
        var sorted = active.Order().ToArray();
        var longest = 1;
        var run = 1;
        for (var index = 1; index < sorted.Length; index++)
        {
            if ((sorted[index] - sorted[index - 1]).Days == 1)
                run++;
            else
                run = 1;
            longest = Math.Max(longest, run);
        }

        var cursor = active.Contains(today)
            ? today
            : active.Contains(today.AddDays(-1)) ? today.AddDays(-1) : DateTime.MinValue;
        var current = 0;
        while (cursor != DateTime.MinValue && active.Contains(cursor))
        {
            current++;
            cursor = cursor.AddDays(-1);
        }

        return (current, longest);
    }

    private static StatisticsSummary Summarize(IReadOnlyList<UsageStatisticsDaySnapshot> days)
    {
        var words = days.Sum(day => day.TotalWords);
        var seconds = days.Sum(day => day.TotalDurationSeconds);
        var apps = days.SelectMany(day => day.AppCounts.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return new StatisticsSummary(words, seconds, apps);
    }

    private static Dictionary<string, int> MergeCounts(
        IEnumerable<IReadOnlyDictionary<string, int>> dictionaries)
    {
        var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var dictionary in dictionaries)
        {
            foreach (var pair in dictionary)
                merged[pair.Key] = merged.GetValueOrDefault(pair.Key) + pair.Value;
        }

        return merged;
    }

    private static string FormatAppName(string identifier)
    {
        var normalized = identifier.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? identifier[..^4]
            : identifier;
        var known = normalized.ToLowerInvariant() switch
        {
            "chrome" => "Google Chrome",
            "msedge" => "Microsoft Edge",
            "winword" => "Microsoft Word",
            "excel" => "Microsoft Excel",
            "powerpnt" => "Microsoft PowerPoint",
            "outlook" or "olk" => "Microsoft Outlook",
            "code" => "Visual Studio Code",
            "notepad" => "Notepad",
            "discord" => "Discord",
            "slack" => "Slack",
            _ => null
        };
        if (known is not null)
            return known;

        normalized = normalized.Replace('-', ' ').Replace('_', ' ');
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalized.ToLower(CultureInfo.CurrentCulture));
    }

    private string FormatModelLabel(string key)
    {
        var (storedEngine, storedModel) = UsageStatisticsService.ParseModelKey(key);
        var normalizedModel = NormalizeStoredModel(storedModel);
        var engine = _transcriptionEngines().FirstOrDefault(candidate =>
            MatchesEngine(candidate, storedEngine)
            || (normalizedModel.SelectionId is not null
                && MatchesEngine(candidate, normalizedModel.SelectionId)));
        var engineLabel = engine?.ProviderDisplayName ?? FormatEngineLabel(storedEngine);
        var modelLabel = engine?.TranscriptionModels.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, normalizedModel.ModelId, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName
            ?? FormatModelName(normalizedModel.ModelId);

        return string.IsNullOrWhiteSpace(modelLabel)
            || string.Equals(modelLabel, engineLabel, StringComparison.OrdinalIgnoreCase)
                ? engineLabel
                : string.Concat(engineLabel, " - ", modelLabel);
    }

    private static bool MatchesEngine(ITranscriptionEnginePlugin engine, string storedEngine) =>
        string.Equals(engine.GetTranscriptionSelectionId(), storedEngine, StringComparison.OrdinalIgnoreCase)
        || string.Equals(engine.ProviderId, storedEngine, StringComparison.OrdinalIgnoreCase)
        || string.Equals(engine.PluginId, storedEngine, StringComparison.OrdinalIgnoreCase);

    private static (string? SelectionId, string? ModelId) NormalizeStoredModel(string? storedModel)
    {
        if (string.IsNullOrWhiteSpace(storedModel))
            return (null, null);

        if (!ModelManagerService.IsPluginModel(storedModel))
            return (null, storedModel);

        try
        {
            var (selectionId, modelId) = ModelManagerService.ParsePluginModelId(storedModel);
            return (selectionId, modelId);
        }
        catch (ArgumentException)
        {
            return (null, storedModel);
        }
    }

    private static string FormatEngineLabel(string engine) => engine.ToLowerInvariant() switch
        {
            "whisper" => "Whisper",
            "parakeet" => "Parakeet",
            "unknown" => Loc.Instance.GetString("Statistics.Unknown"),
            _ => FormatModelName(engine)
        };

    private static string FormatModelName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
            model.Replace('-', ' ').Replace('_', ' ').ToLower(CultureInfo.CurrentCulture));
    }

    private static string FormatTimeSaved(double minutes)
    {
        if (minutes <= 0)
            return "-";

        var total = (int)minutes;
        return total >= 60
            ? Loc.Instance.GetString("Statistics.HoursMinutesFormat", total / 60, total % 60)
            : Loc.Instance.GetString("Statistics.MinutesFormat", total);
    }

    private static (string Text, bool IsNegative) FormatTrend(double? value)
    {
        if (value is null)
            return (string.Empty, false);

        var rounded = (int)Math.Abs(value.Value);
        return value.Value >= 0
            ? ($"\u2197 {rounded}%", false)
            : ($"\u2198 {rounded}%", true);
    }

    private static int? GetPeriodLength(StatisticsPeriod period) => period switch
    {
        StatisticsPeriod.Week => WeekLength,
        StatisticsPeriod.Month => MonthLength,
        _ => null
    };

    private static bool IsActiveDay(UsageStatisticsDaySnapshot day) =>
        day.TranscriptionCount > 0 || day.TotalWords > 0 || day.TotalDurationSeconds > 0;

    private static CultureInfo ResolveCulture()
    {
        try
        {
            return CultureInfo.GetCultureInfo(Loc.Instance.CurrentLanguage);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.CurrentCulture;
        }
    }

    private void OnStatisticsChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(Refresh);
            return;
        }

        Refresh();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Refresh();

    private void OnPluginStateChanged(object? sender, EventArgs e) => OnStatisticsChanged();

    private sealed record StatisticsSummary(int Words, double DurationSeconds, int AppCount)
    {
        public double RawWpm => DurationSeconds > 0 && Words > 0
            ? Words / (DurationSeconds / 60d)
            : 0;

        public double RawSavedMinutes => Words / 45d - DurationSeconds / 60d;
    }
}
