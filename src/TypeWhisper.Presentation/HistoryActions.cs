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

    /// <summary>Exports the selected persisted entry in the requested format.</summary>
    public async Task<string> ExportAsync(string id, string extension)
    {
        await history.EnsureLoadedAsync().ConfigureAwait(false);
        var record = history.Records.FirstOrDefault(record => record.Id == id)
            ?? throw new InvalidOperationException("This history entry is no longer available.");
        return extension.ToLowerInvariant() switch
        {
            ".txt" => history.ExportToText([record]),
            ".md" => history.ExportToMarkdown([record]),
            ".csv" => history.ExportToCsv([record]),
            ".json" => history.ExportToJson([record]),
            _ => throw new ArgumentException("Choose TXT, Markdown, CSV, or JSON.", nameof(extension))
        };
    }

    /// <summary>Writes an export beside its destination before atomically replacing the chosen file.</summary>
    public async Task ExportFileAsync(string id, string path)
    {
        var destination = Path.GetFullPath(path);
        var text = await ExportAsync(id, Path.GetExtension(destination)).ConfigureAwait(false);
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
