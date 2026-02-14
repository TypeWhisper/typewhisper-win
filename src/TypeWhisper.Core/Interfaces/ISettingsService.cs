using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Current { get; }
    event Action<AppSettings>? SettingsChanged;
    AppSettings Load();
    void Save(AppSettings settings);
}
