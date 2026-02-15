using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

public interface ISnippetService
{
    IReadOnlyList<Snippet> Snippets { get; }
    IReadOnlyList<string> AllTags { get; }
    event Action? SnippetsChanged;

    void AddSnippet(Snippet snippet);
    void UpdateSnippet(Snippet snippet);
    void DeleteSnippet(string id);
    string ApplySnippets(string text, Func<string>? clipboardProvider = null);

    string ExportToJson();
    int ImportFromJson(string json);
}
