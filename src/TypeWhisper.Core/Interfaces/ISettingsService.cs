using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Defines the settings service contract.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Gets the current.
    /// </summary>
    AppSettings Current { get; }
    /// <summary>
    /// Raised when settings changes.
    /// </summary>
    event Action<AppSettings>? SettingsChanged;
    /// <summary>
    /// Loads persisted state from storage.
    /// </summary>
    AppSettings Load();
    /// <summary>
    /// Persists the supplied state to storage.
    /// </summary>
    void Save(AppSettings settings);
}
