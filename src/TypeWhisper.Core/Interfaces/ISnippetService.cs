using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Defines the snippet service contract.
/// </summary>
public interface ISnippetService
{
    /// <summary>
    /// Gets the configured snippets in display order.
    /// </summary>
    IReadOnlyList<Snippet> Snippets { get; }
    /// <summary>
    /// Gets the all tags.
    /// </summary>
    IReadOnlyList<string> AllTags { get; }
    /// <summary>
    /// Raised when snippets changes.
    /// </summary>
    event Action? SnippetsChanged;

    /// <summary>
    /// Adds snippet.
    /// </summary>
    void AddSnippet(Snippet snippet);
    /// <summary>
    /// Updates snippet.
    /// </summary>
    void UpdateSnippet(Snippet snippet);
    /// <summary>
    /// Deletes snippet.
    /// </summary>
    void DeleteSnippet(string id);
    /// <summary>
    /// Applies snippets.
    /// </summary>
    string ApplySnippets(string text, Func<string>? clipboardProvider = null);

    /// <summary>
    /// Exports the current data as JSON.
    /// </summary>
    string ExportToJson();
    /// <summary>
    /// Imports from json.
    /// </summary>
    int ImportFromJson(string json);
}
