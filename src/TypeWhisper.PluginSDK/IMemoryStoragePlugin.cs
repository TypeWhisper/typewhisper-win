namespace TypeWhisper.PluginSDK;

/// <summary>
/// Plugin that provides persistent memory storage for extracted facts.
/// Memory entries are key facts extracted from transcriptions via LLM.
/// </summary>
public interface IMemoryStoragePlugin : ITypeWhisperPlugin
{
    /// <summary>Stores a memory entry. Duplicate content should be deduplicated by the plugin.</summary>
    Task StoreAsync(string content, CancellationToken ct = default);

    /// <summary>Searches stored memories by query. Returns relevant entries ranked by relevance.</summary>
    Task<IReadOnlyList<string>> SearchAsync(string query, int maxResults = 5, CancellationToken ct = default);

    /// <summary>Returns all stored memory entries.</summary>
    Task<IReadOnlyList<string>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Deletes a specific memory entry by its content.</summary>
    Task DeleteAsync(string content, CancellationToken ct = default);

    /// <summary>Deletes all stored memory entries.</summary>
    Task ClearAllAsync(CancellationToken ct = default);

    /// <summary>Number of stored memory entries.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}
