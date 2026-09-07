using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>Strict, loss-aware JSON transfer for personal dictionary entries and snippets.</summary>
public static class LexiconTransfer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Reads Windows dictionary arrays or the verified macOS DictionaryExporter array format. Pack entries are allowed only when validating existing local storage.</summary>
    public static DictionaryEntry[] ReadDictionary(string json, bool allowPackEntries = false)
    {
        var entries = ReadArray(json).Select(element =>
        {
            var node = JsonNode.Parse(element.GetRawText())!.AsObject();
            if (node.ContainsKey("type"))
            {
                // macOS DictionaryExporter exports fields, not SwiftData IDs or timestamps.
                var allowed = new HashSet<string> { "type", "original", "replacement", "caseSensitive", "isEnabled", "ctcMinSimilarity", "source" };
                if (node.Any(p => !allowed.Contains(p.Key))) throw new JsonException("Unknown macOS dictionary field. Import canceled to avoid data loss.");
                if (node["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type))
                    throw new JsonException("Dictionary type must be a string.");
                if (type is not ("term" or "correction")) throw new JsonException("Unknown dictionary entry type.");
                node.Remove("type"); node["EntryType"] = type; node["Id"] = Guid.NewGuid().ToString();
            }
            ValidateSource(node);
            return node.Deserialize<DictionaryEntry>(Options) ?? throw new JsonException("Invalid dictionary entry.");
        }).ToArray();
        ValidateDictionary(entries, allowPackEntries);
        return entries;
    }

    /// <summary>Reads the existing Windows snippet JSON array without ignoring unknown fields.</summary>
    public static Snippet[] ReadSnippets(string json)
    {
        var entries = ReadArray(json).Select(element => element.Deserialize<Snippet>(Options)
            ?? throw new JsonException("Invalid snippet.")).ToArray();
        ValidateSnippets(entries);
        return entries;
    }

    /// <summary>Serializes complete Windows dictionary records, retaining IDs, flags, provenance and counters.</summary>
    public static string WriteDictionary(IReadOnlyList<DictionaryEntry> entries) => JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
    /// <summary>Serializes complete Windows snippets, retaining IDs, tags, timestamps and counters.</summary>
    public static string WriteSnippets(IReadOnlyList<Snippet> entries) => JsonSerializer.Serialize(entries, Options);

    /// <summary>Builds an additive or replacement personal dictionary; installed pack entries remain unchanged.</summary>
    public static DictionaryEntry[] MergeDictionary(IReadOnlyList<DictionaryEntry> current, IReadOnlyList<DictionaryEntry> imported, bool replace)
    {
        ValidateDictionary(imported);
        var personal = current.Where(entry => !entry.Id.StartsWith("pack:", StringComparison.Ordinal)).ToArray();
        var next = (replace ? imported : personal.Concat(imported)).ToArray();
        ValidateDictionary(next);
        return current.Where(entry => entry.Id.StartsWith("pack:", StringComparison.Ordinal)).Concat(next).ToArray();
    }

    /// <summary>Builds an additive or replacement snippet list; duplicate IDs or conflicting triggers abort.</summary>
    public static Snippet[] MergeSnippets(IReadOnlyList<Snippet> current, IReadOnlyList<Snippet> imported, bool replace)
    {
        var next = (replace ? imported : current.Concat(imported)).ToArray();
        ValidateSnippets(next);
        return next;
    }

    /// <summary>Atomically exports JSON; a failed write preserves an existing destination.</summary>
    public static void WriteFile(string path, string json)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try { File.WriteAllText(temporary, json); File.Move(temporary, path, overwrite: true); }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static JsonElement[] ReadArray(string json)
    {
        if (json.Length > 5_000_000) throw new JsonException("Import is limited to 5 million characters.");
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 10000)
            throw new JsonException("Choose a JSON array containing at most 10,000 entries.");
        var entries = document.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
        foreach (var entry in entries)
        {
            if (entry.ValueKind != JsonValueKind.Object) throw new JsonException("Every imported entry must be an object.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (entry.EnumerateObject().Any(property => !names.Add(property.Name))) throw new JsonException("Duplicate JSON field. Import canceled.");
        }
        return entries;
    }

    private static void ValidateSource(JsonObject entry)
    {
        foreach (var property in entry.Where(p => p.Key.Equals("Source", StringComparison.OrdinalIgnoreCase)))
        {
            var value = property.Value?.ToJsonString();
            if (value is not ("\"manual\"" or "\"autoLearned\"" or "0" or "1"))
                throw new JsonException("Unknown dictionary provenance. Import canceled to avoid data loss.");
        }
    }

    private static void ValidateDictionary(IReadOnlyList<DictionaryEntry> entries, bool allowPackEntries = false)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            ValidateKey(entry.Id, entry.Original, entry.UsageCount, ids);
            if (!allowPackEntries && entry.Id.StartsWith("pack:", StringComparison.Ordinal)) throw new JsonException("Import personal entries only. Manage installed terms through Term packs.");
            if (!Enum.IsDefined(entry.EntryType)) throw new JsonException("Unknown dictionary entry type.");
            if (entry.Replacement?.Length > 10000 || entry.EntryType == DictionaryEntryType.Correction && entry.Replacement is null)
                throw new JsonException("Corrections need replacement text of at most 10,000 characters.");
            if (entry.EntryType == DictionaryEntryType.Term && entry.Replacement is not null)
                throw new JsonException("Term entries cannot contain replacement text.");
            if (entry.CtcMinSimilarity is { } similarity && (!float.IsFinite(similarity) || similarity < .4f || similarity > .95f))
                throw new JsonException("CTC similarity must be between 0.4 and 0.95.");
            if (entry.IsRegex)
                try { _ = new Regex(entry.Original, RegexOptions.None, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException) { throw new JsonException("Invalid dictionary regular expression."); }
        }
        foreach (var group in entries.Where(entry => !entry.Id.StartsWith("pack:", StringComparison.Ordinal))
            .GroupBy(entry => $"{(int)entry.EntryType}:{entry.Original}", StringComparer.OrdinalIgnoreCase))
            if (group.Count() > 1 && (group.Any(entry => !entry.CaseSensitive) || group.Select(entry => entry.Original).Distinct(StringComparer.Ordinal).Count() != group.Count()))
                throw new JsonException("Conflicting dictionary phrases. No entries were imported; review duplicates or choose Replace.");
    }

    private static void ValidateSnippets(IReadOnlyList<Snippet> entries)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            ValidateKey(entry.Id, entry.Trigger, entry.UsageCount, ids);
            if (entry.Replacement is null || entry.Replacement.Length > 10000 || entry.Tags is null || entry.Tags.Length > 300)
                throw new JsonException("Invalid snippet replacement or tags.");
            try { _ = SnippetService.ApplySnippetsSnapshot(entry.Trigger, [entry with { IsEnabled = true }], () => ""); }
            catch (FormatException) { throw new JsonException("Invalid snippet date or time format."); }
        }
        foreach (var group in entries.GroupBy(entry => entry.Trigger, StringComparer.OrdinalIgnoreCase))
            if (group.Count() > 1 && (group.Any(entry => !entry.CaseSensitive) || group.Select(entry => entry.Trigger).Distinct(StringComparer.Ordinal).Count() != group.Count()))
                throw new JsonException("Conflicting snippet triggers. No entries were imported; review duplicates or choose Replace.");
    }

    private static void ValidateKey(string id, string key, int usage, HashSet<string> ids)
    {
        if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) throw new JsonException("Missing or duplicate entry ID. No entries were imported.");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 160 || key.Contains('\r') || key.Contains('\n') || key != key.Trim())
            throw new JsonException("Entry phrases must be single lines of 1–160 characters without surrounding whitespace.");
        if (usage < 0) throw new JsonException("Usage counts cannot be negative.");
    }
}
