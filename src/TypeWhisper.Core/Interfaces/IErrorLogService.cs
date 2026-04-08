using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

public interface IErrorLogService
{
    IReadOnlyList<ErrorLogEntry> Entries { get; }
    event Action? EntriesChanged;

    void AddEntry(string message, string category = "general");
    void ClearAll();
    string ExportDiagnostics();
}
