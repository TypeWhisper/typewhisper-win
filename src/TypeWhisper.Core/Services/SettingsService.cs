using System.Diagnostics;
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
    private string BackupPath => _filePath + ".bak";
    private string TempPath => _filePath + ".tmp";

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
        // Try primary settings file
        var result = TryLoadFrom(_filePath);
        if (result is not null)
        {
            _current = result;
            return _current;
        }

        // Primary failed — try backup
        if (File.Exists(BackupPath))
        {
            LogWarning("Primary settings corrupt or missing, trying backup.");
            result = TryLoadFrom(BackupPath);
            if (result is not null)
            {
                _current = result;
                // Restore backup as primary
                try { File.Copy(BackupPath, _filePath, overwrite: true); }
                catch { /* best effort */ }
                return _current;
            }
        }

        LogWarning("No valid settings found, using defaults.");
        _current = AppSettings.Default;
        return _current;
    }

    public void Save(AppSettings settings)
    {
        _current = settings;

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Backup current settings before overwriting
        if (File.Exists(_filePath))
        {
            try { File.Copy(_filePath, BackupPath, overwrite: true); }
            catch { /* best effort */ }
        }

        // Atomic write: serialize to .tmp, then move over primary
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(TempPath, json);
        File.Move(TempPath, _filePath, overwrite: true);

        SettingsChanged?.Invoke(settings);
    }

    private static AppSettings? TryLoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            LogWarning($"Failed to load settings from {path}: {ex.Message}");
            return null;
        }
    }

    private static void LogWarning(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SettingsService] {message}";
        Debug.WriteLine(line);

        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TypeWhisper", "Logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "settings.log"), line + Environment.NewLine);
        }
        catch { /* logging must never throw */ }
    }
}
