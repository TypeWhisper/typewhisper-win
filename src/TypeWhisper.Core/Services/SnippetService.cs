using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Sync;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Provides snippet service behavior.
/// </summary>
public sealed partial class SnippetService : ISnippetService
{
    private readonly string _filePath;
    private List<Snippet> _cache = [];
    private bool _cacheLoaded;

    /// <summary>
    /// Gets the configured snippets in display order.
    /// </summary>
    public IReadOnlyList<Snippet> Snippets
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    /// <summary>
    /// Gets the all tags.
    /// </summary>
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

    /// <summary>
    /// Raised when snippets changes.
    /// </summary>
    public event Action? SnippetsChanged;

    /// <summary>
    /// Initializes a new instance of the SnippetService class.
    /// </summary>
    public SnippetService(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Adds snippet.
    /// </summary>
    public void AddSnippet(Snippet snippet)
    {
        EnsureCacheLoaded();
        _cache.Add(BackfillTimestamps(snippet));
        SaveToDisk();
        SnippetsChanged?.Invoke();
    }

    /// <summary>
    /// Updates snippet.
    /// </summary>
    public void UpdateSnippet(Snippet snippet)
    {
        EnsureCacheLoaded();
        var idx = _cache.FindIndex(s => s.Id == snippet.Id);
        if (idx >= 0)
        {
            var existing = _cache[idx];
            _cache[idx] = BackfillTimestamps(snippet) with { UpdatedAt = NextUpdatedAt(existing.UpdatedAt) };
        }
        SaveToDisk();
        SnippetsChanged?.Invoke();
    }

    /// <summary>
    /// Deletes snippet.
    /// </summary>
    public void DeleteSnippet(string id)
    {
        EnsureCacheLoaded();
        _cache.RemoveAll(s => s.Id == id);
        SaveToDisk();
        SnippetsChanged?.Invoke();
    }

    /// <summary>
    /// Applies snippets.
    /// </summary>
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

            var pattern = Regex.Escape(snippet.Trigger) + @"[.!?]?";
            var options = snippet.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            text = Regex.Replace(text, pattern, expanded.Replace("$", "$$"), options);

            IncrementUsageCount(snippet.Id);
        }

        return text;
    }

    /// <summary>
    /// Exports the current data as JSON.
    /// </summary>
    public string ExportToJson()
    {
        EnsureCacheLoaded();
        return JsonSerializer.Serialize(_cache, SnippetJsonContext.Default.ListSnippet);
    }

    /// <summary>
    /// Imports from json.
    /// </summary>
    public int ImportFromJson(string json)
    {
        var imported = JsonSerializer.Deserialize(json, SnippetJsonContext.Default.ListSnippet);
        if (imported is null or { Count: 0 }) return 0;

        EnsureCacheLoaded();
        var existingTriggers = _cache.Select(s => s.Trigger).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var count = 0;
        foreach (var snippet in imported)
        {
            if (existingTriggers.Contains(snippet.Trigger)) continue;

            var newSnippet = BackfillTimestamps(snippet with { Id = Guid.NewGuid().ToString() });
            _cache.Add(newSnippet);
            existingTriggers.Add(newSnippet.Trigger);
            count++;
        }

        if (count > 0)
        {
            SaveToDisk();
            SnippetsChanged?.Invoke();
        }

        return count;
    }

    private static string ExpandPlaceholders(string template, Func<string>? clipboardProvider)
    {
        var now = DateTime.Now;

        template = template
            .Replace("{day}", now.ToString("dddd"))
            .Replace("{year}", now.Year.ToString());

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
        var idx = _cache.FindIndex(s => s.Id == id);
        if (idx >= 0)
        {
            _cache[idx] = _cache[idx] with { UsageCount = _cache[idx].UsageCount + 1 };
            SaveToDisk();
        }
    }

    /// <summary>
    /// Returns user data sync snippets.
    /// </summary>
    public IReadOnlyList<UserDataSyncSnippet> GetUserDataSyncSnippets()
    {
        EnsureCacheLoaded();
        return _cache
            .Select(snippet => new UserDataSyncSnippet(
                snippet.Trigger,
                snippet.Replacement,
                snippet.CaseSensitive,
                snippet.IsEnabled,
                snippet.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                snippet.CreatedAt,
                snippet.UpdatedAt))
            .ToList();
    }

    /// <summary>
    /// Applies user data sync mutations.
    /// </summary>
    public void ApplyUserDataSyncMutations(IReadOnlyList<UserDataSyncMutation> mutations)
    {
        EnsureCacheLoaded();

        var changed = false;
        foreach (var mutation in mutations)
        {
            switch (mutation)
            {
                case UserDataSyncMutation.UpsertSnippet upsert:
                    changed |= UpsertSyncedSnippet(upsert.Snippet);
                    break;
                case UserDataSyncMutation.DeleteSnippet delete:
                    changed |= DeleteSyncedSnippet(delete.ItemId);
                    break;
            }
        }

        if (!changed)
            return;

        SaveToDisk();
        SnippetsChanged?.Invoke();
    }

    private bool UpsertSyncedSnippet(UserDataSyncSnippet synced)
    {
        var targetId = UserDataSyncIdentity.SnippetItemId(synced.Trigger);
        var idx = _cache.FindIndex(snippet =>
            UserDataSyncIdentity.SnippetItemId(snippet.Trigger) == targetId);
        var tags = string.Join(",", synced.Tags);

        if (idx >= 0)
        {
            var existing = _cache[idx];
            _cache[idx] = existing with
            {
                Trigger = synced.Trigger,
                Replacement = synced.Replacement,
                CaseSensitive = synced.CaseSensitive,
                IsEnabled = synced.IsEnabled,
                Tags = tags,
                UpdatedAt = synced.UpdatedAt
            };
            return true;
        }

        _cache.Add(new Snippet
        {
            Id = Guid.NewGuid().ToString(),
            Trigger = synced.Trigger,
            Replacement = synced.Replacement,
            CaseSensitive = synced.CaseSensitive,
            IsEnabled = synced.IsEnabled,
            Tags = tags,
            CreatedAt = synced.CreatedAt,
            UpdatedAt = synced.UpdatedAt
        });
        return true;
    }

    private bool DeleteSyncedSnippet(string itemId)
    {
        var removed = _cache.RemoveAll(snippet =>
            UserDataSyncIdentity.SnippetItemId(snippet.Trigger) == itemId);
        return removed > 0;
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _cache = JsonSerializer.Deserialize<List<Snippet>>(json) ?? [];
                _cache = _cache.Select(BackfillTimestamps).ToList();
            }
        }
        catch
        {
            _cache = [];
        }

        _cacheLoaded = true;
    }

    private void SaveToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }

    private static Snippet BackfillTimestamps(Snippet snippet)
    {
        var createdAt = snippet.CreatedAt == default ? DateTime.UtcNow : NormalizeUtc(snippet.CreatedAt);
        var updatedAt = snippet.UpdatedAt == default ? createdAt : NormalizeUtc(snippet.UpdatedAt);
        return snippet with { CreatedAt = createdAt, UpdatedAt = updatedAt };
    }

    private static DateTime NextUpdatedAt(DateTime previousUpdatedAt)
    {
        var previous = previousUpdatedAt == default ? DateTime.UtcNow : NormalizeUtc(previousUpdatedAt);
        var now = DateTime.UtcNow;
        return now > previous ? now : previous.AddTicks(1);
    }

    private static DateTime NormalizeUtc(DateTime date) =>
        date.Kind == DateTimeKind.Utc ? date : date.ToUniversalTime();
}

[JsonSerializable(typeof(List<Snippet>))]
internal partial class SnippetJsonContext : JsonSerializerContext;
