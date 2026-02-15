using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TypeWhisper.Core.Data;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed partial class SnippetService : ISnippetService
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

    public IReadOnlyList<string> AllTags
    {
        get
        {
            EnsureCacheLoaded();
            return _cache
                .SelectMany(s => s.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public event Action? SnippetsChanged;

    public SnippetService(ITypeWhisperDatabase db)
    {
        _db = db;
    }

    public void AddSnippet(Snippet snippet)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snippets (id, trigger, replacement, case_sensitive, is_enabled, usage_count, tags, created_at)
            VALUES (@id, @trigger, @repl, @cs, @enabled, @usage, @tags, @created)
            """;
        cmd.Parameters.AddWithValue("@id", snippet.Id);
        cmd.Parameters.AddWithValue("@trigger", snippet.Trigger);
        cmd.Parameters.AddWithValue("@repl", snippet.Replacement);
        cmd.Parameters.AddWithValue("@cs", snippet.CaseSensitive ? 1 : 0);
        cmd.Parameters.AddWithValue("@enabled", snippet.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@usage", snippet.UsageCount);
        cmd.Parameters.AddWithValue("@tags", snippet.Tags);
        cmd.Parameters.AddWithValue("@created", snippet.CreatedAt.ToString("o"));
        cmd.ExecuteNonQuery();

        _cache.Add(snippet);
        SnippetsChanged?.Invoke();
    }

    public void UpdateSnippet(Snippet snippet)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE snippets
            SET trigger = @trigger, replacement = @repl,
                case_sensitive = @cs, is_enabled = @enabled, usage_count = @usage, tags = @tags
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", snippet.Id);
        cmd.Parameters.AddWithValue("@trigger", snippet.Trigger);
        cmd.Parameters.AddWithValue("@repl", snippet.Replacement);
        cmd.Parameters.AddWithValue("@cs", snippet.CaseSensitive ? 1 : 0);
        cmd.Parameters.AddWithValue("@enabled", snippet.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@usage", snippet.UsageCount);
        cmd.Parameters.AddWithValue("@tags", snippet.Tags);
        cmd.ExecuteNonQuery();

        var idx = _cache.FindIndex(s => s.Id == snippet.Id);
        if (idx >= 0) _cache[idx] = snippet;
        SnippetsChanged?.Invoke();
    }

    public void DeleteSnippet(string id)
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM snippets WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();

        _cache.RemoveAll(s => s.Id == id);
        SnippetsChanged?.Invoke();
    }

    public string ApplySnippets(string text, Func<string>? clipboardProvider = null)
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

            var expanded = ExpandPlaceholders(snippet.Replacement, clipboardProvider);

            var pattern = Regex.Escape(snippet.Trigger);
            var options = snippet.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            text = Regex.Replace(text, pattern, expanded.Replace("$", "$$"), options);

            IncrementUsageCount(snippet.Id);
        }

        return text;
    }

    public string ExportToJson()
    {
        EnsureCacheLoaded();
        return JsonSerializer.Serialize(_cache, JsonContext.Default.ListSnippet);
    }

    public int ImportFromJson(string json)
    {
        var imported = JsonSerializer.Deserialize(json, JsonContext.Default.ListSnippet);
        if (imported is null or { Count: 0 }) return 0;

        EnsureCacheLoaded();
        var existingTriggers = _cache.Select(s => s.Trigger).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var count = 0;
        foreach (var snippet in imported)
        {
            if (existingTriggers.Contains(snippet.Trigger)) continue;

            var newSnippet = snippet with { Id = Guid.NewGuid().ToString() };
            AddSnippet(newSnippet);
            existingTriggers.Add(newSnippet.Trigger);
            count++;
        }

        return count;
    }

    private static string ExpandPlaceholders(string template, Func<string>? clipboardProvider)
    {
        var now = DateTime.Now;

        // Simple placeholders first (backward compat)
        template = template
            .Replace("{day}", now.ToString("dddd"))
            .Replace("{year}", now.Year.ToString());

        // Regex-based placeholders with optional format: {date:FORMAT}, {time:FORMAT}, {datetime:FORMAT}, {clipboard}
        template = PlaceholderRegex().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            var format = match.Groups[2].Success ? match.Groups[2].Value : null;

            return name switch
            {
                "date" => now.ToString(format ?? "yyyy-MM-dd"),
                "time" => now.ToString(format ?? "HH:mm"),
                "datetime" => now.ToString(format ?? "yyyy-MM-dd HH:mm"),
                "clipboard" => clipboardProvider?.Invoke() ?? "",
                _ => match.Value
            };
        });

        return template;
    }

    [GeneratedRegex(@"\{(date|time|datetime|clipboard)(?::([^}]+))?\}")]
    private static partial Regex PlaceholderRegex();

    private void IncrementUsageCount(string id)
    {
        var conn = _db.GetConnection();
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

        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, trigger, replacement, case_sensitive, is_enabled, usage_count, tags, created_at
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
                Tags = reader.GetString(6),
                CreatedAt = DateTime.Parse(reader.GetString(7))
            });
        }
        _cacheLoaded = true;
    }
}

[JsonSerializable(typeof(List<Snippet>))]
internal partial class JsonContext : JsonSerializerContext;
