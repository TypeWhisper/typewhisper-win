using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.FileMemory;

/// <summary>
/// Provides file memory plugin behavior.
/// </summary>
public sealed class FileMemoryPlugin : IMemoryStoragePlugin
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private IPluginHostServices? _host;
    private string? _filePath;
    private List<MemoryEntry>? _entries;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.file-memory";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "File Memory";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.0.0";

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _filePath = Path.Combine(host.PluginDataDirectory, "memories.json");
        _host.Log(PluginLogLevel.Info, "Activated");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deactivates the plugin and releases provider resources.
    /// </summary>
    public Task DeactivateAsync()
    {
        _host = null;
        _entries = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the settings view shown by the host, or null when no UI is required.
    /// </summary>
    public UserControl? CreateSettingsView() => null;

    // IMemoryStoragePlugin

    /// <summary>
    /// Performs store asynchronously.
    /// </summary>
    public async Task StoreAsync(string content, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = await LoadEntriesAsync(ct);

            if (entries.Any(e => e.Content == content))
            {
                _host?.Log(PluginLogLevel.Debug, "Duplicate memory skipped");
                return;
            }

            entries.Add(new MemoryEntry(content, DateTime.UtcNow));
            await SaveEntriesAsync(ct);
            _host?.Log(PluginLogLevel.Debug, $"Stored memory (total={entries.Count})");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Performs search asynchronously.
    /// </summary>
    public async Task<IReadOnlyList<string>> SearchAsync(string query, int maxResults = 5, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = await LoadEntriesAsync(ct);

            return entries
                .Where(e => e.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.CreatedAt)
                .Take(maxResults)
                .Select(e => e.Content)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns all asynchronously.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAllAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = await LoadEntriesAsync(ct);
            return entries.Select(e => e.Content).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Performs delete asynchronously.
    /// </summary>
    public async Task DeleteAsync(string content, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = await LoadEntriesAsync(ct);
            var removed = entries.RemoveAll(e => e.Content == content);

            if (removed > 0)
                await SaveEntriesAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Clears all asynchronously.
    /// </summary>
    public async Task ClearAllAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = await LoadEntriesAsync(ct);
            entries.Clear();
            await SaveEntriesAsync(ct);
            _host?.Log(PluginLogLevel.Info, "All memories cleared");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Counts asynchronously..
    /// </summary>
    public async Task<int> CountAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = await LoadEntriesAsync(ct);
            return entries.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Private helpers

    private async Task<List<MemoryEntry>> LoadEntriesAsync(CancellationToken ct)
    {
        if (_entries is not null)
            return _entries;

        if (_filePath is null)
            throw new InvalidOperationException("Plugin not activated");

        if (File.Exists(_filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_filePath, ct);
                _entries = JsonSerializer.Deserialize<List<MemoryEntry>>(json, JsonOptions) ?? [];
            }
            catch (Exception ex)
            {
                _host?.Log(PluginLogLevel.Warning, $"Failed to load memories: {ex.Message}");
                _entries = [];
            }
        }
        else
        {
            _entries = [];
        }

        return _entries;
    }

    private async Task SaveEntriesAsync(CancellationToken ct)
    {
        if (_filePath is null || _entries is null)
            return;

        var dir = Path.GetDirectoryName(_filePath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_entries, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json, ct);
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _lock.Dispose();
    }

    private sealed record MemoryEntry(string Content, DateTime CreatedAt);
}
