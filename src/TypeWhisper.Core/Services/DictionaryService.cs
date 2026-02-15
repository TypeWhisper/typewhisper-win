using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TypeWhisper.Core.Data;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class DictionaryService : IDictionaryService
{
    private readonly ITypeWhisperDatabase _db;
    private List<DictionaryEntry> _cache = [];
    private bool _cacheLoaded;

    public IReadOnlyList<DictionaryEntry> Entries
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    public event Action? EntriesChanged;

    public DictionaryService(ITypeWhisperDatabase db)
    {
        _db = db;
    }

    public void AddEntry(DictionaryEntry entry)
    {
        InsertEntry(entry);
        _cache.Add(entry);
        EntriesChanged?.Invoke();
    }

    public void AddEntries(IEnumerable<DictionaryEntry> entries)
    {
        var list = entries.ToList();
        using var conn = _db.GetConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        foreach (var entry in list)
        {
            InsertEntry(entry, conn);
            _cache.Add(entry);
        }
        tx.Commit();
        EntriesChanged?.Invoke();
    }

    public void UpdateEntry(DictionaryEntry entry)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dictionary_entries
            SET entry_type = @type, original = @orig, replacement = @repl,
                case_sensitive = @cs, is_enabled = @enabled, usage_count = @usage
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", entry.Id);
        cmd.Parameters.AddWithValue("@type", entry.EntryType.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@orig", entry.Original);
        cmd.Parameters.AddWithValue("@repl", (object?)entry.Replacement ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cs", entry.CaseSensitive ? 1 : 0);
        cmd.Parameters.AddWithValue("@enabled", entry.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@usage", entry.UsageCount);
        cmd.ExecuteNonQuery();

        var idx = _cache.FindIndex(e => e.Id == entry.Id);
        if (idx >= 0) _cache[idx] = entry;
        EntriesChanged?.Invoke();
    }

    public void DeleteEntry(string id)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dictionary_entries WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();

        _cache.RemoveAll(e => e.Id == id);
        EntriesChanged?.Invoke();
    }

    public void DeleteEntries(IEnumerable<string> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;

        using var conn = _db.GetConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        foreach (var id in idList)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM dictionary_entries WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();

        var idSet = idList.ToHashSet();
        _cache.RemoveAll(e => idSet.Contains(e.Id));
        EntriesChanged?.Invoke();
    }

    public string ApplyCorrections(string text)
    {
        EnsureCacheLoaded();
        var corrections = _cache
            .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Correction && e.Replacement is not null)
            .OrderByDescending(e => e.Original.Length); // Longest match first

        foreach (var entry in corrections)
        {
            var comparison = entry.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            if (text.Contains(entry.Original, comparison))
            {
                var pattern = Regex.Escape(entry.Original);
                var options = entry.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                text = Regex.Replace(text, @"\b" + pattern + @"\b", entry.Replacement!, options);

                IncrementUsageCount(entry.Id);
            }
        }

        return text;
    }

    public string? GetTermsForPrompt()
    {
        EnsureCacheLoaded();
        var terms = _cache
            .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Term)
            .Select(e => e.Original)
            .ToList();

        if (terms.Count == 0) return null;
        return string.Join(", ", terms);
    }

    public void LearnCorrection(string original, string replacement)
    {
        EnsureCacheLoaded();

        // Check if correction already exists
        var existing = _cache.FirstOrDefault(e =>
            e.EntryType == DictionaryEntryType.Correction &&
            e.Original.Equals(original, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            UpdateEntry(existing with { Replacement = replacement, UsageCount = existing.UsageCount + 1 });
        }
        else
        {
            AddEntry(new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = DictionaryEntryType.Correction,
                Original = original,
                Replacement = replacement
            });
        }
    }

    private void InsertEntry(DictionaryEntry entry, SqliteConnection? existingConn = null)
    {
        var conn = existingConn ?? _db.GetConnection();
        var shouldClose = existingConn is null;
        try
        {
            if (shouldClose) conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO dictionary_entries
                (id, entry_type, original, replacement, case_sensitive, is_enabled, usage_count, created_at)
                VALUES (@id, @type, @orig, @repl, @cs, @enabled, @usage, @created)
                """;
            cmd.Parameters.AddWithValue("@id", entry.Id);
            cmd.Parameters.AddWithValue("@type", entry.EntryType.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("@orig", entry.Original);
            cmd.Parameters.AddWithValue("@repl", (object?)entry.Replacement ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cs", entry.CaseSensitive ? 1 : 0);
            cmd.Parameters.AddWithValue("@enabled", entry.IsEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@usage", entry.UsageCount);
            cmd.Parameters.AddWithValue("@created", entry.CreatedAt.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        finally
        {
            if (shouldClose) conn.Dispose();
        }
    }

    private void IncrementUsageCount(string id)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE dictionary_entries SET usage_count = usage_count + 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();

        var idx = _cache.FindIndex(e => e.Id == id);
        if (idx >= 0) _cache[idx] = _cache[idx] with { UsageCount = _cache[idx].UsageCount + 1 };
    }

    public void ActivatePack(TermPack pack)
    {
        EnsureCacheLoaded();

        var existingOriginals = _cache
            .Where(e => e.EntryType == DictionaryEntryType.Term)
            .Select(e => e.Original)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newEntries = pack.Terms
            .Where(t => !existingOriginals.Contains(t))
            .Select(t => new DictionaryEntry
            {
                Id = $"pack:{pack.Id}:{t}",
                EntryType = DictionaryEntryType.Term,
                Original = t
            })
            .ToList();

        if (newEntries.Count > 0)
            AddEntries(newEntries);
    }

    public void DeactivatePack(string packId)
    {
        EnsureCacheLoaded();

        var prefix = $"pack:{packId}:";
        var idsToDelete = _cache
            .Where(e => e.Id.StartsWith(prefix, StringComparison.Ordinal))
            .Select(e => e.Id)
            .ToList();

        if (idsToDelete.Count > 0)
            DeleteEntries(idsToDelete);
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, entry_type, original, replacement, case_sensitive, is_enabled, usage_count, created_at
            FROM dictionary_entries ORDER BY original
            """;

        _cache = [];
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            _cache.Add(new DictionaryEntry
            {
                Id = reader.GetString(0),
                EntryType = reader.GetString(1) == "correction" ? DictionaryEntryType.Correction : DictionaryEntryType.Term,
                Original = reader.GetString(2),
                Replacement = reader.IsDBNull(3) ? null : reader.GetString(3),
                CaseSensitive = reader.GetInt32(4) != 0,
                IsEnabled = reader.GetInt32(5) != 0,
                UsageCount = reader.GetInt32(6),
                CreatedAt = DateTime.Parse(reader.GetString(7))
            });
        }
        _cacheLoaded = true;
    }
}
