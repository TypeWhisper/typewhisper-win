using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>Updates the latest persisted snippet catalog while serializing profile mutations.</summary>
public static class SnippetCatalogTransaction
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    /// <summary>Reads current entries under the profile gate, applies a change, and atomically saves it. Invalid or unfamiliar input and write errors leave the previous file intact.</summary>
    public static IReadOnlyList<Snippet> Update(string path, Func<IReadOnlyList<Snippet>, IReadOnlyList<Snippet>> change)
    {
        using var mutation = ProfileMutationCoordinator.Enter();
        Snippet[] current;
        try
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            ValidateObjectFields(document.RootElement);
            current = JsonSerializer.Deserialize<Snippet[]>(json, Options) ?? throw new JsonException("Invalid snippet catalog.");
        }
        catch (FileNotFoundException) { current = []; }
        catch (DirectoryNotFoundException) { current = []; }
        Validate(current);
        var next = change(current).ToArray();
        Validate(next);
        WriteAtomically(path, JsonSerializer.Serialize(next, Options));
        return next;
    }

    internal static void ValidateObjectFields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new JsonException("Snippet catalog must be an array.");
        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid snippet entry.");
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (entry.EnumerateObject().Any(property => !keys.Add(property.Name))) throw new JsonException("Duplicate snippet field.");
        }
    }

    private static void Validate(IReadOnlyList<Snippet> entries)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id) ||
            string.IsNullOrWhiteSpace(entry.Trigger) || entry.Replacement is null || entry.Tags is null || entry.UsageCount < 0))
            throw new JsonException("Invalid snippet catalog. No changes were saved.");
    }

    internal static void WriteAtomically(string path, string json)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try { File.WriteAllText(temporary, json); File.Move(temporary, path, overwrite: true); }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
