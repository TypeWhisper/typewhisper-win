using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Persists compact daily usage aggregates independently from transcription history.
/// </summary>
public sealed class UsageStatisticsService : IUsageStatisticsService
{
    internal const char KeySeparator = '\u001F';

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly object _gate = new();
    private UsageStatisticsStore _store;

    /// <inheritdoc />
    public IReadOnlyList<UsageStatisticsDaySnapshot> Days
    {
        get
        {
            lock (_gate)
            {
                return _store.Days
                    .OrderBy(day => day.Day)
                    .Select(ToSnapshot)
                    .ToArray();
            }
        }
    }

    /// <inheritdoc />
    public bool HasAnyStatistics
    {
        get
        {
            lock (_gate)
            {
                return _store.Days.Any(day =>
                    day.TranscriptionCount > 0 || day.TotalWords > 0 || day.TotalDurationSeconds > 0);
            }
        }
    }

    /// <inheritdoc />
    public event Action? StatisticsChanged;

    /// <summary>
    /// Initializes a new statistics store at the supplied path.
    /// </summary>
    public UsageStatisticsService(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        _store = Load();
    }

    /// <inheritdoc />
    public void RecordTranscription(
        DateTime timestamp,
        int wordCount,
        double durationSeconds,
        string? appIdentifier,
        string? appName,
        string? engineUsed,
        string? modelUsed)
    {
        if (wordCount <= 0 || !double.IsFinite(durationSeconds) || durationSeconds < 0)
            return;

        var changed = false;
        lock (_gate)
        {
            AddToStore(
                _store,
                timestamp,
                wordCount,
                durationSeconds,
                appIdentifier,
                appName,
                engineUsed,
                modelUsed);
            changed = SaveLocked();
        }

        if (changed)
            StatisticsChanged?.Invoke();
    }

    /// <inheritdoc />
    public void BackfillFromHistoryIfNeeded(IEnumerable<TranscriptionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var changed = false;

        lock (_gate)
        {
            if (_store.HistoryBackfillCompleted)
                return;

            foreach (var record in records)
                AddHistoryRecord(_store, record);

            _store.HistoryBackfillCompleted = true;
            changed = SaveLocked();
        }

        if (changed)
            StatisticsChanged?.Invoke();
    }

    /// <inheritdoc />
    public void ReplaceWithHistoryRecords(IEnumerable<TranscriptionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var replacement = new UsageStatisticsStore { HistoryBackfillCompleted = true };
        foreach (var record in records)
            AddHistoryRecord(replacement, record);

        var changed = false;
        lock (_gate)
        {
            _store = replacement;
            changed = SaveLocked();
        }

        if (changed)
            StatisticsChanged?.Invoke();
    }

    /// <summary>
    /// Builds a stable model aggregation key.
    /// </summary>
    public static string BuildModelKey(string? engineUsed, string? modelUsed)
    {
        var engine = NormalizeValue(engineUsed, "unknown");
        var model = NormalizeValue(modelUsed, string.Empty);
        return string.Concat(engine, KeySeparator, model);
    }

    /// <summary>
    /// Splits a model aggregation key into its original components.
    /// </summary>
    public static (string EngineUsed, string? ModelUsed) ParseModelKey(string key)
    {
        var parts = key.Split(KeySeparator, 2);
        return (parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }

    private static void AddHistoryRecord(UsageStatisticsStore store, TranscriptionRecord record)
    {
        if (record.Status != TranscriptionRecordStatus.Succeeded)
            return;

        AddToStore(
            store,
            record.Timestamp,
            record.WordCount,
            record.DurationSeconds,
            record.AppProcessName,
            record.AppName,
            record.EngineUsed,
            record.ModelUsed);
    }

    private static void AddToStore(
        UsageStatisticsStore store,
        DateTime timestamp,
        int wordCount,
        double durationSeconds,
        string? appIdentifier,
        string? appName,
        string? engineUsed,
        string? modelUsed)
    {
        if (wordCount <= 0 || !double.IsFinite(durationSeconds) || durationSeconds < 0)
            return;

        var localTimestamp = ToLocalTime(timestamp);
        var day = store.Days.FirstOrDefault(candidate => candidate.Day.Date == localTimestamp.Date);
        if (day is null)
        {
            day = new UsageStatisticsDayData { Day = localTimestamp.Date };
            store.Days.Add(day);
        }

        day.TranscriptionCount++;
        day.TotalWords += wordCount;
        day.TotalDurationSeconds += durationSeconds;

        var appKey = NormalizeValue(appIdentifier, NormalizeValue(appName, string.Empty));
        if (appKey.Length > 0)
            day.AppCounts[appKey] = day.AppCounts.GetValueOrDefault(appKey) + 1;

        var modelKey = BuildModelKey(engineUsed, modelUsed);
        day.ModelCounts[modelKey] = day.ModelCounts.GetValueOrDefault(modelKey) + 1;

        NormalizeHours(day);
        day.HourCounts[localTimestamp.Hour]++;
    }

    private UsageStatisticsStore Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new UsageStatisticsStore();

            var json = File.ReadAllText(_filePath);
            var store = JsonSerializer.Deserialize<UsageStatisticsStore>(json, JsonOptions)
                ?? new UsageStatisticsStore();
            store.Days ??= [];
            foreach (var day in store.Days)
            {
                day.AppCounts = new Dictionary<string, int>(
                    day.AppCounts ?? new Dictionary<string, int>(),
                    StringComparer.OrdinalIgnoreCase);
                day.ModelCounts = new Dictionary<string, int>(
                    day.ModelCounts ?? new Dictionary<string, int>(),
                    StringComparer.OrdinalIgnoreCase);
                NormalizeHours(day);
            }

            return store;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Failed to load usage statistics: {ex.GetType().Name}");
            return new UsageStatisticsStore();
        }
    }

    private bool SaveLocked()
    {
        var tempPath = string.Concat(_filePath, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            _store.Days = _store.Days.OrderBy(day => day.Day).ToList();
            File.WriteAllText(tempPath, JsonSerializer.Serialize(_store, JsonOptions));
            File.Move(tempPath, _filePath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Failed to save usage statistics: {ex.GetType().Name}");
            TryDelete(tempPath);
            return false;
        }
    }

    private static UsageStatisticsDaySnapshot ToSnapshot(UsageStatisticsDayData day) => new()
    {
        Day = day.Day.Date,
        TranscriptionCount = day.TranscriptionCount,
        TotalWords = day.TotalWords,
        TotalDurationSeconds = day.TotalDurationSeconds,
        AppCounts = new Dictionary<string, int>(day.AppCounts, StringComparer.OrdinalIgnoreCase),
        ModelCounts = new Dictionary<string, int>(day.ModelCounts, StringComparer.OrdinalIgnoreCase),
        HourCounts = day.HourCounts.ToArray()
    };

    private static DateTime ToLocalTime(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value.ToLocalTime(),
        DateTimeKind.Local => value,
        _ => DateTime.SpecifyKind(value, DateTimeKind.Local)
    };

    private static string NormalizeValue(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static void NormalizeHours(UsageStatisticsDayData day)
    {
        if (day.HourCounts is { Length: 24 })
            return;

        var hours = new int[24];
        if (day.HourCounts is not null)
            Array.Copy(day.HourCounts, hours, Math.Min(day.HourCounts.Length, hours.Length));
        day.HourCounts = hours;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class UsageStatisticsStore
    {
        public int Version { get; set; } = 1;
        public bool HistoryBackfillCompleted { get; set; }
        public List<UsageStatisticsDayData> Days { get; set; } = [];
    }

    private sealed class UsageStatisticsDayData
    {
        public DateTime Day { get; set; }
        public int TranscriptionCount { get; set; }
        public int TotalWords { get; set; }
        public double TotalDurationSeconds { get; set; }
        public Dictionary<string, int> AppCounts { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ModelCounts { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public int[] HourCounts { get; set; } = new int[24];
    }
}
