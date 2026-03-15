using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TypeWhisper.Core.Data;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class HistoryService : IHistoryService
{
    private readonly ITypeWhisperDatabase _db;
    private List<TranscriptionRecord> _cache = [];
    private bool _cacheLoaded;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    // Cached stats
    private int _totalRecords;
    private int _totalWords;
    private double _totalDuration;
    private List<string> _distinctApps = [];

    public IReadOnlyList<TranscriptionRecord> Records
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    public event Action? RecordsChanged;

    public int TotalRecords => _cacheLoaded ? _totalRecords : Records.Count;
    public int TotalWords => _cacheLoaded ? _totalWords : Records.Sum(r => r.WordCount);
    public double TotalDuration => _cacheLoaded ? _totalDuration : Records.Sum(r => r.DurationSeconds);

    public HistoryService(ITypeWhisperDatabase db)
    {
        _db = db;
    }

    public async Task EnsureLoadedAsync()
    {
        if (_cacheLoaded) return;

        await _loadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cacheLoaded) return;
            var records = await Task.Run(LoadFromDatabase).ConfigureAwait(false);
            _cache = records;
            RebuildStats();
            _cacheLoaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public IReadOnlyList<string> GetDistinctApps()
    {
        EnsureCacheLoaded();
        return _distinctApps;
    }

    public void AddRecord(TranscriptionRecord record)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO transcription_history
            (id, timestamp, raw_text, final_text, app_name, app_process_name, app_url,
             duration_seconds, language, engine_used, profile_name, model_used, created_at)
            VALUES (@id, @ts, @raw, @final, @app, @proc, @url, @dur, @lang, @engine, @profile, @model_used, @created)
            """;
        cmd.Parameters.AddWithValue("@id", record.Id);
        cmd.Parameters.AddWithValue("@ts", record.Timestamp.ToString("o"));
        cmd.Parameters.AddWithValue("@raw", record.RawText);
        cmd.Parameters.AddWithValue("@final", record.FinalText);
        cmd.Parameters.AddWithValue("@app", (object?)record.AppName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@proc", (object?)record.AppProcessName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@url", (object?)record.AppUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dur", record.DurationSeconds);
        cmd.Parameters.AddWithValue("@lang", (object?)record.Language ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@engine", record.EngineUsed);
        cmd.Parameters.AddWithValue("@profile", (object?)record.ProfileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@model_used", (object?)record.ModelUsed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", record.CreatedAt.ToString("o"));
        cmd.ExecuteNonQuery();

        _cache.Insert(0, record);

        // Incremental stats update
        _totalRecords++;
        _totalWords += record.WordCount;
        _totalDuration += record.DurationSeconds;
        if (!string.IsNullOrEmpty(record.AppProcessName) &&
            !_distinctApps.Contains(record.AppProcessName, StringComparer.OrdinalIgnoreCase))
        {
            _distinctApps.Add(record.AppProcessName);
            _distinctApps.Sort(StringComparer.OrdinalIgnoreCase);
        }

        RecordsChanged?.Invoke();
    }

    public void UpdateRecord(string id, string finalText)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE transcription_history SET final_text = @final WHERE id = @id";
        cmd.Parameters.AddWithValue("@final", finalText);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();

        var idx = _cache.FindIndex(r => r.Id == id);
        if (idx >= 0)
        {
            var old = _cache[idx];
            var updated = old with { FinalText = finalText };
            _cache[idx] = updated;
            _totalWords += updated.WordCount - old.WordCount;
        }
        RecordsChanged?.Invoke();
    }

    public void DeleteRecord(string id)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM transcription_history WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();

        var idx = _cache.FindIndex(r => r.Id == id);
        if (idx >= 0)
        {
            var removed = _cache[idx];
            _cache.RemoveAt(idx);
            _totalRecords--;
            _totalWords -= removed.WordCount;
            _totalDuration -= removed.DurationSeconds;
        }

        RebuildDistinctApps();
        RecordsChanged?.Invoke();
    }

    public void ClearAll()
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM transcription_history";
        cmd.ExecuteNonQuery();

        _cache.Clear();
        _totalRecords = 0;
        _totalWords = 0;
        _totalDuration = 0;
        _distinctApps.Clear();
        RecordsChanged?.Invoke();
    }

    public IReadOnlyList<TranscriptionRecord> Search(string query)
    {
        EnsureCacheLoaded();
        if (string.IsNullOrWhiteSpace(query)) return _cache;

        return _cache.Where(r =>
            r.FinalText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            r.RawText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (r.AppName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        ).ToList();
    }

    public void PurgeOldRecords(int retentionDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("o");

        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM transcription_history WHERE created_at < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        cmd.ExecuteNonQuery();

        _cacheLoaded = false;
        _cache.Clear();
        EnsureCacheLoaded();
        RecordsChanged?.Invoke();
    }

    public string ExportToText(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null)
    {
        var l = labels ?? ExportLabels.Default;
        var sb = new StringBuilder();
        sb.AppendLine(l.Header);
        sb.AppendLine($"{l.Exported}: {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"{l.Entries}: {records.Count}");
        sb.AppendLine(new string('─', 60));
        sb.AppendLine();

        foreach (var r in records)
        {
            sb.AppendLine($"[{r.Timestamp:dd.MM.yyyy HH:mm}] {r.AppProcessName ?? "–"} ({r.DurationSeconds:F1}s)");
            sb.AppendLine(r.FinalText);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string ExportToCsv(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null)
    {
        var l = labels ?? ExportLabels.Default;
        var sb = new StringBuilder();
        sb.AppendLine($"{l.Timestamp},{l.App},{l.Text},{l.Duration},{l.Words},{l.Language}");

        foreach (var r in records)
        {
            var text = "\"" + r.FinalText.Replace("\"", "\"\"") + "\"";
            sb.AppendLine($"{r.Timestamp:yyyy-MM-dd HH:mm:ss},{r.AppProcessName ?? ""},{text},{r.DurationSeconds:F1},{r.WordCount},{r.Language ?? ""}");
        }

        return sb.ToString();
    }

    public string ExportToMarkdown(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null)
    {
        var l = labels ?? ExportLabels.Default;
        var sb = new StringBuilder();
        sb.AppendLine($"# {l.Header}");
        sb.AppendLine();
        sb.AppendLine($"- **{l.Exported}:** {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"- **{l.Entries}:** {records.Count}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var r in records)
        {
            sb.AppendLine($"## {r.Timestamp:dd.MM.yyyy HH:mm}");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(r.AppProcessName))
                sb.AppendLine($"- **{l.App}:** {r.AppProcessName}");
            sb.AppendLine($"- **{l.Duration}:** {r.DurationSeconds:F1}s");
            if (!string.IsNullOrEmpty(r.Language))
                sb.AppendLine($"- **{l.Language}:** {r.Language}");
            sb.AppendLine();
            sb.AppendLine(r.FinalText);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string ExportToJson(IReadOnlyList<TranscriptionRecord> records)
    {
        var data = records.Select(r => new
        {
            id = r.Id,
            timestamp = r.Timestamp.ToString("o"),
            text = r.FinalText,
            raw_text = r.RawText,
            app = r.AppProcessName,
            duration_seconds = r.DurationSeconds,
            language = r.Language,
            engine = r.EngineUsed,
            model = r.ModelUsed,
            profile = r.ProfileName,
            words = r.WordCount
        });

        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        _loadLock.Wait();
        try
        {
            if (_cacheLoaded) return;
            _cache = LoadFromDatabase();
            RebuildStats();
            _cacheLoaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private List<TranscriptionRecord> LoadFromDatabase()
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, timestamp, raw_text, final_text, app_name, app_process_name, app_url,
                   duration_seconds, language, engine_used, created_at, profile_name, model_used
            FROM transcription_history ORDER BY timestamp DESC
            """;

        var records = new List<TranscriptionRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new TranscriptionRecord
            {
                Id = reader.GetString(0),
                Timestamp = DateTime.Parse(reader.GetString(1)),
                RawText = reader.GetString(2),
                FinalText = reader.GetString(3),
                AppName = reader.IsDBNull(4) ? null : reader.GetString(4),
                AppProcessName = reader.IsDBNull(5) ? null : reader.GetString(5),
                AppUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
                DurationSeconds = reader.GetDouble(7),
                Language = reader.IsDBNull(8) ? null : reader.GetString(8),
                EngineUsed = reader.GetString(9),
                CreatedAt = DateTime.Parse(reader.GetString(10)),
                ProfileName = reader.IsDBNull(11) ? null : reader.GetString(11),
                ModelUsed = reader.IsDBNull(12) ? null : reader.GetString(12)
            });
        }
        return records;
    }

    private void RebuildStats()
    {
        _totalRecords = _cache.Count;
        _totalWords = _cache.Sum(r => r.WordCount);
        _totalDuration = _cache.Sum(r => r.DurationSeconds);
        RebuildDistinctApps();
    }

    private void RebuildDistinctApps()
    {
        _distinctApps = _cache
            .Select(r => r.AppProcessName)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToList()!;
    }
}
