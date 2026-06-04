using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Defines the error log service contract.
/// </summary>
public interface IErrorLogService
{
    /// <summary>
    /// Gets the configured dictionary entries.
    /// </summary>
    IReadOnlyList<ErrorLogEntry> Entries { get; }
    /// <summary>
    /// Raised when entries changes.
    /// </summary>
    event Action? EntriesChanged;

    /// <summary>
    /// Adds an error log entry and persists the updated log.
    /// </summary>
    void AddEntry(string message, string category = "general");
    /// <summary>
    /// Clears all items from the current collection.
    /// </summary>
    void ClearAll();
    /// <summary>
    /// Exports diagnostics.
    /// </summary>
    string ExportDiagnostics();
}
