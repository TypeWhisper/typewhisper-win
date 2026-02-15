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

    public IReadOnlyList<TranscriptionRecord> Records
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    public event Action? RecordsChanged;

    public int TotalRecords => Records.Count;
    public int TotalWords => Records.Sum(r => r.WordCount);
    public double TotalDuration => Records.Sum(r => r.DurationSeconds);

    public HistoryService(ITypeWhisperDatabase db)
    {
        _db = db;
    }

    public void AddRecord(TranscriptionRecord record)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO transcription_history
            (id, timestamp, raw_text, final_text, app_name, app_process_name, app_url,
             duration_seconds, language, engine_used, profile_name, created_at)
            VALUES (@id, @ts, @raw, @final, @app, @proc, @url, @dur, @lang, @engine, @profile, @created)
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
        cmd.Parameters.AddWithValue("@created", record.CreatedAt.ToString("o"));
        cmd.ExecuteNonQuery();

        _cache.Insert(0, record);
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
            _cache[idx] = _cache[idx] with { FinalText = finalText };
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

        _cache.RemoveAll(r => r.Id == id);
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

    public string ExportToText(IReadOnlyList<TranscriptionRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TypeWhisper — Transkriptions-Verlauf");
        sb.AppendLine($"Exportiert: {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"Einträge: {records.Count}");
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

    public string ExportToCsv(IReadOnlyList<TranscriptionRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Zeitstempel,App,Text,Dauer (s),Wörter,Sprache");

        foreach (var r in records)
        {
            var text = "\"" + r.FinalText.Replace("\"", "\"\"") + "\"";
            sb.AppendLine($"{r.Timestamp:yyyy-MM-dd HH:mm:ss},{r.AppProcessName ?? ""},{text},{r.DurationSeconds:F1},{r.WordCount},{r.Language ?? ""}");
        }

        return sb.ToString();
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, timestamp, raw_text, final_text, app_name, app_process_name, app_url,
                   duration_seconds, language, engine_used, created_at, profile_name
            FROM transcription_history ORDER BY timestamp DESC
            """;

        _cache = [];
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            _cache.Add(new TranscriptionRecord
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
                ProfileName = reader.IsDBNull(11) ? null : reader.GetString(11)
            });
        }
        _cacheLoaded = true;
    }
}
