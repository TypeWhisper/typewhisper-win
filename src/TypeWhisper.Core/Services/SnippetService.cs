using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TypeWhisper.Core.Data;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class SnippetService : ISnippetService
{
    private readonly ITypeWhisperDatabase _db;
    private List<Snippet> _cache = [];
    private bool _cacheLoaded;

    public IReadOnlyList<Snippet> Snippets
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    public event Action? SnippetsChanged;

    public SnippetService(ITypeWhisperDatabase db)
    {
        _db = db;
    }

    public void AddSnippet(Snippet snippet)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snippets (id, trigger, replacement, case_sensitive, is_enabled, usage_count, created_at)
            VALUES (@id, @trigger, @repl, @cs, @enabled, @usage, @created)
            """;
        cmd.Parameters.AddWithValue("@id", snippet.Id);
        cmd.Parameters.AddWithValue("@trigger", snippet.Trigger);
        cmd.Parameters.AddWithValue("@repl", snippet.Replacement);
        cmd.Parameters.AddWithValue("@cs", snippet.CaseSensitive ? 1 : 0);
        cmd.Parameters.AddWithValue("@enabled", snippet.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@usage", snippet.UsageCount);
        cmd.Parameters.AddWithValue("@created", snippet.CreatedAt.ToString("o"));
        cmd.ExecuteNonQuery();

        _cache.Add(snippet);
        SnippetsChanged?.Invoke();
    }

    public void UpdateSnippet(Snippet snippet)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE snippets
            SET trigger = @trigger, replacement = @repl,
                case_sensitive = @cs, is_enabled = @enabled, usage_count = @usage
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", snippet.Id);
        cmd.Parameters.AddWithValue("@trigger", snippet.Trigger);
        cmd.Parameters.AddWithValue("@repl", snippet.Replacement);
        cmd.Parameters.AddWithValue("@cs", snippet.CaseSensitive ? 1 : 0);
        cmd.Parameters.AddWithValue("@enabled", snippet.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@usage", snippet.UsageCount);
        cmd.ExecuteNonQuery();

        var idx = _cache.FindIndex(s => s.Id == snippet.Id);
        if (idx >= 0) _cache[idx] = snippet;
        SnippetsChanged?.Invoke();
    }

    public void DeleteSnippet(string id)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM snippets WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();

        _cache.RemoveAll(s => s.Id == id);
        SnippetsChanged?.Invoke();
    }

    public string ApplySnippets(string text)
    {
        EnsureCacheLoaded();
        var activeSnippets = _cache
            .Where(s => s.IsEnabled)
            .OrderByDescending(s => s.Trigger.Length);

        foreach (var snippet in activeSnippets)
        {
            var comparison = snippet.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            if (!text.Contains(snippet.Trigger, comparison)) continue;

            var expanded = ExpandPlaceholders(snippet.Replacement);

            var pattern = Regex.Escape(snippet.Trigger);
            var options = snippet.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            text = Regex.Replace(text, pattern, expanded, options);

            IncrementUsageCount(snippet.Id);
        }

        return text;
    }

    private static string ExpandPlaceholders(string template)
    {
        var now = DateTime.Now;
        return template
            .Replace("{date}", now.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HH:mm"))
            .Replace("{datetime}", now.ToString("yyyy-MM-dd HH:mm"))
            .Replace("{day}", now.ToString("dddd"))
            .Replace("{year}", now.Year.ToString());
    }

    private void IncrementUsageCount(string id)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE snippets SET usage_count = usage_count + 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();

        var idx = _cache.FindIndex(s => s.Id == id);
        if (idx >= 0) _cache[idx] = _cache[idx] with { UsageCount = _cache[idx].UsageCount + 1 };
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, trigger, replacement, case_sensitive, is_enabled, usage_count, created_at
            FROM snippets ORDER BY trigger
            """;

        _cache = [];
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            _cache.Add(new Snippet
            {
                Id = reader.GetString(0),
                Trigger = reader.GetString(1),
                Replacement = reader.GetString(2),
                CaseSensitive = reader.GetInt32(3) != 0,
                IsEnabled = reader.GetInt32(4) != 0,
                UsageCount = reader.GetInt32(5),
                CreatedAt = DateTime.Parse(reader.GetString(6))
            });
        }
        _cacheLoaded = true;
    }
}
