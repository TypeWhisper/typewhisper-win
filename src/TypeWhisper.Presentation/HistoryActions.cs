using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>Persists explicit history edits and deletions using the existing history store.</summary>
public sealed class HistoryActions(IHistoryService history)
{
    /// <summary>Changes only final text, retaining raw text and capture metadata.</summary>
    public async Task<TranscriptionRecord?> EditAsync(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter transcript text.", nameof(text));
        await history.EnsureLoadedAsync().ConfigureAwait(false);
        var original = history.Records.FirstOrDefault(record => record.Id == id);
        if (original is null) return null;
        var updated = original with { FinalText = text };
        return history.TryReplaceRecord(updated) ? updated : null;
    }

    /// <summary>Deletes an entry and verifies that persistence succeeded before reporting success.</summary>
    public async Task<bool> DeleteAsync(string id)
    {
        await history.EnsureLoadedAsync().ConfigureAwait(false);
        history.DeleteRecord(id);
        return history.Records.All(record => record.Id != id);
    }

    /// <summary>Deletes the confirmed identity snapshot in one atomic history mutation.</summary>
    public async Task<bool> DeleteAsync(IReadOnlyCollection<string> ids)
    {
        var snapshot = ids.Distinct(StringComparer.Ordinal).ToArray();
        await history.EnsureLoadedAsync().ConfigureAwait(false);
        return history.TryDeleteRecords(snapshot);
    }

    /// <summary>Captures all current identities for an explicit clear-history confirmation.</summary>
    public async Task<string[]> SnapshotIdsAsync()
    {
        await history.EnsureLoadedAsync().ConfigureAwait(false);
        return history.Records.Select(record => record.Id).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Exports the selected persisted entry in the requested format.</summary>
    public async Task<string> ExportAsync(string id, string extension)
        => await ExportAsync(new[] { id }, extension).ConfigureAwait(false);

    /// <summary>Exports precisely the selected identities; missing records cause a visible failure instead of a partial export.</summary>
    public async Task<string> ExportAsync(IReadOnlyCollection<string> ids, string extension)
    {
        var snapshot = ids.ToHashSet(StringComparer.Ordinal);
        if (snapshot.Count == 0) throw new ArgumentException("Select history entries to export.", nameof(ids));
        await history.EnsureLoadedAsync().ConfigureAwait(false);
        var records = history.Records.Where(record => snapshot.Contains(record.Id)).ToArray();
        if (records.Length != snapshot.Count) throw new InvalidOperationException("Some selected history entries are no longer available. Refresh and select them again.");
        return extension.ToLowerInvariant() switch
        {
            ".txt" => history.ExportToText(records),
            ".md" => history.ExportToMarkdown(records),
            ".csv" => history.ExportToCsv(records),
            ".json" => history.ExportToJson(records),
            _ => throw new ArgumentException("Choose TXT, Markdown, CSV, or JSON.", nameof(extension))
        };
    }

    /// <summary>Writes an export beside its destination before atomically replacing the chosen file.</summary>
    public async Task ExportFileAsync(string id, string path)
        => await ExportFileAsync(new[] { id }, path).ConfigureAwait(false);

    /// <summary>Atomically exports the selected identities using UTF-8 without a byte-order mark.</summary>
    public async Task ExportFileAsync(IReadOnlyCollection<string> ids, string path)
    {
        var destination = Path.GetFullPath(path);
        var text = await ExportAsync(ids, Path.GetExtension(destination)).ConfigureAwait(false);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".typewhisper-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, text, new System.Text.UTF8Encoding(false)).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
