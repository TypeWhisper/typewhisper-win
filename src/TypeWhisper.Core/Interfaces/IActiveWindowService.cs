namespace TypeWhisper.Core.Interfaces;

public interface IActiveWindowService
{
    string? GetActiveWindowProcessName();
    string? GetActiveWindowTitle();
    string? GetBrowserUrl();
}
