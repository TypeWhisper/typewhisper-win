using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class ErrorLogService : IErrorLogService
{
    private const int MaxEntries = 200;

    private readonly string _logFilePath;
    private readonly List<ErrorLogEntry> _entries = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<ErrorLogEntry> Entries
    {
        get
        {
            lock (_lock) return _entries.ToList();
        }
    }

    public event Action? EntriesChanged;

    public ErrorLogService(string dataDirectory)
    {
        _logFilePath = Path.Combine(dataDirectory, "error-log.json");
        LoadFromDisk();
    }

    public void AddEntry(string message, string category = "general")
    {
        var entry = ErrorLogEntry.Create(message, category);

        lock (_lock)
        {
            _entries.Insert(0, entry);

            while (_entries.Count > MaxEntries)
                _entries.RemoveAt(_entries.Count - 1);
        }

        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    public void ClearAll()
    {
        lock (_lock) _entries.Clear();
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    public string ExportDiagnostics()
    {
        var report = new
        {
            exported_at = DateTime.UtcNow.ToString("o"),
            app = new
            {
                version = GetAppVersion(),
                platform = "Windows",
                os_version = Environment.OSVersion.VersionString,
                dotnet_version = Environment.Version.ToString(),
                locale = System.Globalization.CultureInfo.CurrentCulture.Name,
                timezone = TimeZoneInfo.Local.Id
            },
            error_count = _entries.Count,
            errors = _entries.Select(e => new
            {
                timestamp = e.Timestamp.ToString("o"),
                category = e.Category,
                message = e.Message
            })
        };

        return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_logFilePath)) return;

            var json = File.ReadAllText(_logFilePath);
            var entries = JsonSerializer.Deserialize<List<ErrorLogEntry>>(json);
            if (entries is not null)
            {
                lock (_lock)
                {
                    _entries.Clear();
                    _entries.AddRange(entries);
                }
            }
        }
        catch
        {
            // Corrupted log file - start fresh
        }
    }

    private void SaveToDisk()
    {
        try
        {
            List<ErrorLogEntry> snapshot;
            lock (_lock) snapshot = [.. _entries];

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });

            var dir = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_logFilePath, json);
        }
        catch
        {
            // Ignore save failures
        }
    }

    private static string GetAppVersion()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        return asm?.GetName().Version?.ToString() ?? "unknown";
    }
}
