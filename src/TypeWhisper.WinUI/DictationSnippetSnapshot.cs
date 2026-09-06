using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.WinUI;

// A recording never writes its older catalog back over edits made in the UI.
internal sealed class DictationSnippetSnapshot
{
    internal static string StoragePath => Path.Combine(Path.GetDirectoryName(DictationDictionarySnapshot.StoragePath)!, "snippets.json");
    private readonly Snippet[] _entries;
    internal string? Error { get; }
    private DictationSnippetSnapshot(Snippet[] entries, string? error = null) { _entries = entries; Error = error; }

    internal static Snippet[] ReadEntries(string path)
    {
        if (!File.Exists(path)) return [];
        var entries = JsonSerializer.Deserialize<Snippet[]>(File.ReadAllText(path));
        if (entries is null || entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id) ||
            string.IsNullOrWhiteSpace(e.Trigger) || e.Replacement is null || e.Tags is null) ||
            entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new JsonException("Invalid snippet entries.");
        return entries;
    }

    internal static DictationSnippetSnapshot Load(string path)
    {
        try { return new(ReadEntries(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new([], "Snippets unavailable · transcript retained without snippet expansion."); }
    }

    internal bool NeedsClipboard(string text)
    {
        var needed = false;
        _ = Apply(text, () => { needed = true; return ""; });
        return needed;
    }

    internal (string Text, string? Error) Apply(string text, Func<string>? clipboardProvider = null)
    {
        if (Error is not null) return (text, Error);
        try
        {
            // Read only when a matching snippet actually expands this placeholder.
            return (SnippetService.ApplySnippetsSnapshot(text, _entries,
                clipboardProvider ?? (() => throw new InvalidOperationException("Clipboard text is unavailable."))), null);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        { return (text, "Snippet expansion failed · transcript retained: " + ex.Message); }
    }
}
