using System.Text.Json;
using System.Text.Json.Nodes;

namespace TypeWhisper.Core.Services;

/// <summary>Merges successful snippet usage into the current catalog without restoring an older recording snapshot.</summary>
public static class SnippetUsageRecorder
{
    /// <summary>Increments each existing applied ID once. The caller must invoke this only once after actual expansion succeeds, never during clipboard probing. Returns the number of updated snippets.</summary>
    public static int Record(string path, IReadOnlyCollection<string> appliedIds)
    {
        var applied = appliedIds.ToHashSet(StringComparer.Ordinal);
        if (applied.Count == 0) return 0;
        using var mutation = ProfileMutationCoordinator.Enter();
        string json;
        try { json = File.ReadAllText(path); }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
        using var document = JsonDocument.Parse(json);
        SnippetCatalogTransaction.ValidateObjectFields(document.RootElement);
        var entries = JsonNode.Parse(json)!.AsArray();
        var knownIds = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var node in entries)
        {
            var entry = node!.AsObject();
            var idProperty = entry.FirstOrDefault(property => property.Key.Equals("Id", StringComparison.OrdinalIgnoreCase));
            if (idProperty.Value is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) ||
                string.IsNullOrWhiteSpace(id) || !knownIds.Add(id)) throw new JsonException("Invalid or duplicate snippet ID. Usage was not saved.");
            var usageProperty = entry.FirstOrDefault(property => property.Key.Equals("UsageCount", StringComparison.OrdinalIgnoreCase));
            var usage = 0;
            if (usageProperty.Key is not null && (usageProperty.Value is not JsonValue usageValue ||
                !usageValue.TryGetValue<int>(out usage) || usage < 0)) throw new JsonException("Invalid snippet usage count. Usage was not saved.");
            if (!applied.Contains(id)) continue;
            entry[usageProperty.Key ?? "UsageCount"] = checked(usage + 1);
            count++;
        }
        if (count > 0) SnippetCatalogTransaction.WriteAtomically(path, entries.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return count;
    }
}
