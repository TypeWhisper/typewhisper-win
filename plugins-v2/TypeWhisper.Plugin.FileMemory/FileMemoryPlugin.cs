using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.FileMemory;

/// <summary>Stores explicitly saved memories in an atomic local JSON file.</summary>
public sealed partial class FileMemoryPlugin : IMemoryStoragePlugin
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IPluginHostServices? _host;
    private string? _filePath;
    private MemoryEntry[] _entries = [];
    private string? _loadError;
    private string? _editing;
    private string? _draftId;
    private string _query = "";

    /// <inheritdoc />
    public string PluginId => "com.typewhisper.file-memory";
    /// <inheritdoc />
    public string PluginName => "File Memory";
    /// <inheritdoc />
    public string PluginVersion => "1.3.0";

    /// <inheritdoc />
    public async Task ActivateAsync(IPluginHostServices host)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _host = host;
            _filePath = Path.Combine(host.PluginDataDirectory, "memories.json");
            _entries = [];
            _editing = _draftId = null;
            _loadError = null;
            _query = "";
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
                    var entries = JsonSerializer.Deserialize<MemoryEntry[]>(json, JsonOptions)
                        ?? throw new JsonException("Expected a memory list.");
                    if (entries.Any(e => e is null || string.IsNullOrWhiteSpace(e.Content) || e.Content.Length > 32768))
                        throw new JsonException("Invalid memory entry.");
                    entries = entries.Select(e => e.Id == Guid.Empty ? e with { Id = Guid.NewGuid() } : e).ToArray();
                    if (entries.Select(e => e.Id).Distinct().Count() != entries.Length)
                        throw new JsonException("Duplicate memory identity.");
                    _entries = entries;
                }
                if (_entries.Length == 0) _editing = _draftId = Guid.NewGuid().ToString();
                else _editing = _entries[0].Id.ToString();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _loadError = "The memory file could not be read. Restore or repair memories.json, then reload the plugin. The existing file has been preserved.";
                host.Log(PluginLogLevel.Warning, "Memory file could not be loaded: " + ex.GetType().Name);
            }
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task DeactivateAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try { _host = null; _filePath = null; _entries = []; _editing = _draftId = null; _loadError = null; }
        finally { _lock.Release(); }
    }

    private void RequireWritable()
    {
        if (_host is null || _filePath is null) throw new InvalidOperationException("Plugin is not active.");
        if (_loadError is not null) throw new InvalidOperationException(L(_loadError,
            "Die Erinnerungsdatei konnte nicht geladen werden. Stelle memories.json wieder her oder repariere sie und lade das Plugin erneut. Die vorhandene Datei bleibt erhalten."));
    }

    private static string ValidateContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 32768)
            throw new ArgumentException("Enter between 1 and 32768 characters.", nameof(content));
        return content.Trim();
    }

    // Callers hold the gate. Publish the replacement only after its atomic disk write succeeds.
    private async Task PersistAsync(MemoryEntry[] replacement, CancellationToken ct)
    {
        RequireWritable();
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath!)!);
        var temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(replacement, JsonOptions), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, _filePath!, overwrite: true);
            _entries = replacement;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _host?.Log(PluginLogLevel.Warning, "Could not remove temporary memory file."); }
        }
    }

    /// <inheritdoc />
    public async Task StoreAsync(string content, CancellationToken ct = default)
    {
        content = ValidateContent(content);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireWritable();
            if (_entries.Any(e => e.Content == content)) return;
            await PersistAsync([.. _entries, new(content, DateTime.UtcNow) { Id = Guid.NewGuid() }], ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> SearchAsync(string query, int maxResults = 5, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireWritable();
            return _entries.Where(e => e.Content.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.CreatedAt).Take(Math.Clamp(maxResults, 0, 1000)).Select(e => e.Content).ToArray();
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { RequireWritable(); return _entries.OrderByDescending(e => e.CreatedAt).Select(e => e.Content).ToArray(); }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string content, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireWritable();
            var replacement = _entries.Where(e => e.Content != content).ToArray();
            if (replacement.Length != _entries.Length) await PersistAsync(replacement, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { await PersistAsync([], ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken ct = default) => (await GetAllAsync(ct).ConfigureAwait(false)).Count;
    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    private sealed record MemoryEntry(string Content, DateTime CreatedAt)
    {
        public Guid Id { get; init; }
    }
}
