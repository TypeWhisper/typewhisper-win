using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private AppSettings _current;

    public AppSettings Current => _current;
    public event Action<AppSettings>? SettingsChanged;

    public SettingsService(string filePath)
    {
        _filePath = filePath;
        _current = Load();
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? AppSettings.Default;
                return _current;
            }
        }
        catch
        {
            // Corrupt file - use defaults
        }

        _current = AppSettings.Default;
        return _current;
    }

    public void Save(AppSettings settings)
    {
        _current = settings;

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_filePath, json);

        SettingsChanged?.Invoke(settings);
    }
}
