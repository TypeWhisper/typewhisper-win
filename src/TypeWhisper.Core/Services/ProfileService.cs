using System.Text.Json;
using Microsoft.Data.Sqlite;
using TypeWhisper.Core.Data;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class ProfileService : IProfileService
{
    private readonly ITypeWhisperDatabase _db;
    private List<Profile> _cache = [];
    private bool _cacheLoaded;

    public IReadOnlyList<Profile> Profiles
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    public event Action? ProfilesChanged;

    public ProfileService(ITypeWhisperDatabase db)
    {
        _db = db;
    }

    public void AddProfile(Profile profile)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO profiles
            (id, name, is_enabled, priority, process_names, url_patterns,
             input_language, translation_target, selected_task,
             whisper_mode_override, created_at, updated_at)
            VALUES (@id, @name, @enabled, @priority, @procs, @urls,
                    @lang, @trans, @task, @whisper, @created, @updated)
            """;
        BindProfileParams(cmd, profile);
        cmd.ExecuteNonQuery();

        _cache.Add(profile);
        SortCache();
        ProfilesChanged?.Invoke();
    }

    public void UpdateProfile(Profile profile)
    {
        var updated = profile with { UpdatedAt = DateTime.UtcNow };

        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE profiles
            SET name = @name, is_enabled = @enabled, priority = @priority,
                process_names = @procs, url_patterns = @urls,
                input_language = @lang, translation_target = @trans, selected_task = @task,
                whisper_mode_override = @whisper, updated_at = @updated
            WHERE id = @id
            """;
        BindProfileParams(cmd, updated);
        cmd.ExecuteNonQuery();

        var idx = _cache.FindIndex(p => p.Id == profile.Id);
        if (idx >= 0) _cache[idx] = updated;
        SortCache();
        ProfilesChanged?.Invoke();
    }

    public void DeleteProfile(string id)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM profiles WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();

        _cache.RemoveAll(p => p.Id == id);
        ProfilesChanged?.Invoke();
    }

    public Profile? MatchProfile(string? processName, string? url)
    {
        EnsureCacheLoaded();

        foreach (var profile in _cache.Where(p => p.IsEnabled))
        {
            // Match by process name
            if (processName is not null && profile.ProcessNames.Count > 0)
            {
                if (profile.ProcessNames.Any(pn =>
                    processName.Equals(pn, StringComparison.OrdinalIgnoreCase)))
                    return profile;
            }

            // Match by URL pattern
            if (url is not null && profile.UrlPatterns.Count > 0)
            {
                if (profile.UrlPatterns.Any(pattern =>
                    url.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                    return profile;
            }
        }

        return null;
    }

    private void SortCache()
    {
        _cache.Sort((a, b) => b.Priority.CompareTo(a.Priority));
    }

    private static void BindProfileParams(SqliteCommand cmd, Profile p)
    {
        cmd.Parameters.AddWithValue("@id", p.Id);
        cmd.Parameters.AddWithValue("@name", p.Name);
        cmd.Parameters.AddWithValue("@enabled", p.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@priority", p.Priority);
        cmd.Parameters.AddWithValue("@procs", JsonSerializer.Serialize(p.ProcessNames));
        cmd.Parameters.AddWithValue("@urls", JsonSerializer.Serialize(p.UrlPatterns));
        cmd.Parameters.AddWithValue("@lang", (object?)p.InputLanguage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@trans", (object?)p.TranslationTarget ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@task", (object?)p.SelectedTask ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@whisper", p.WhisperModeOverride.HasValue ? (p.WhisperModeOverride.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("@created", p.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@updated", p.UpdatedAt.ToString("o"));
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, is_enabled, priority, process_names, url_patterns,
                   input_language, translation_target, selected_task,
                   whisper_mode_override, created_at, updated_at
            FROM profiles ORDER BY priority DESC
            """;

        _cache = [];
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            _cache.Add(new Profile
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                IsEnabled = reader.GetInt32(2) != 0,
                Priority = reader.GetInt32(3),
                ProcessNames = JsonSerializer.Deserialize<List<string>>(reader.GetString(4)) ?? [],
                UrlPatterns = JsonSerializer.Deserialize<List<string>>(reader.GetString(5)) ?? [],
                InputLanguage = reader.IsDBNull(6) ? null : reader.GetString(6),
                TranslationTarget = reader.IsDBNull(7) ? null : reader.GetString(7),
                SelectedTask = reader.IsDBNull(8) ? null : reader.GetString(8),
                WhisperModeOverride = reader.IsDBNull(9) ? null : reader.GetInt32(9) != 0,
                CreatedAt = DateTime.Parse(reader.GetString(10)),
                UpdatedAt = DateTime.Parse(reader.GetString(11))
            });
        }
        _cacheLoaded = true;
    }
}
