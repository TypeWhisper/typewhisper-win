using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAiVectorMemory;

/// <summary>
/// Provides open ai vector memory plugin behavior.
/// </summary>
public sealed class OpenAiVectorMemoryPlugin : IMemoryStoragePlugin
{
    private const string EmbeddingModel = "text-embedding-3-small";
    private const string EmbeddingUrl = "https://api.openai.com/v1/embeddings";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _filePath;
    private List<VectorMemoryEntry>? _entries;

    // ITypeWhisperPlugin

    /// <summary>
    /// Gets the stable plugin identifier used by the host.
    /// </summary>
    public string PluginId => "com.typewhisper.openai-vector-memory";
    /// <summary>
    /// Gets the plugin display name shown by the host.
    /// </summary>
    public string PluginName => "OpenAI Vector Memory";
    /// <summary>
    /// Gets the plugin version reported to the host.
    /// </summary>
    public string PluginVersion => "1.0.0";

    /// <summary>
    /// Activates the plugin and loads any persisted configuration.
    /// </summary>
    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _filePath = Path.Combine(host.PluginDataDirectory, "vector-memories.json");
        _apiKey = await host.LoadSecretAsync("api-key");
        host.Log(PluginLogLevel.Info, $"Activated (configured={!string.IsNullOrEmpty(_apiKey)})");
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
    public UserControl? CreateSettingsView()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };

        var label = new TextBlock
        {
            Text = "OpenAI API Key",
            Margin = new Thickness(0, 0, 0, 4),
        };

        var box = new PasswordBox { MaxLength = 200 };
        if (!string.IsNullOrEmpty(_apiKey))
            box.Password = _apiKey;

        var status = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 12,
        };

        var btn = new Button
        {
            Content = "Save",
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        btn.Click += async (_, _) =>
        {
            var key = box.Password;
            _apiKey = string.IsNullOrWhiteSpace(key) ? null : key;

            if (_host is not null)
            {
                if (string.IsNullOrWhiteSpace(key))
                    await _host.DeleteSecretAsync("api-key");
                else
                    await _host.StoreSecretAsync("api-key", key);
            }

            status.Text = string.IsNullOrWhiteSpace(key) ? "API key cleared" : "API key saved";
        };

        panel.Children.Add(label);
        panel.Children.Add(box);
        panel.Children.Add(btn);
        panel.Children.Add(status);
        return new UserControl { Content = panel };
    }

    // IMemoryStoragePlugin

    /// <summary>
    /// Performs store asynchronously.
    /// </summary>
    public async Task StoreAsync(string content, CancellationToken ct)
    {
        EnsureConfigured();

        await _lock.WaitAsync(ct);
        try
        {
            var entries = await LoadEntriesAsync(ct);

            if (entries.Any(e => e.Content == content))
            {
                _host?.Log(PluginLogLevel.Debug, "Duplicate memory skipped");
                return;
            }

            var embedding = await GetEmbeddingAsync(content, ct);
            entries.Add(new VectorMemoryEntry(content, embedding, DateTime.UtcNow));
            await SaveEntriesAsync(ct);
            _host?.Log(PluginLogLevel.Debug, $"Stored vector memory (total={entries.Count})");
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
        EnsureConfigured();

        await _lock.WaitAsync(ct);
        try
        {
            var entries = await LoadEntriesAsync(ct);
            if (entries.Count == 0)
                return [];

            var queryEmbedding = await GetEmbeddingAsync(query, ct);

            return entries
                .Select(e => (Entry: e, Similarity: CosineSimilarity(queryEmbedding, e.Embedding)))
                .OrderByDescending(x => x.Similarity)
                .Take(maxResults)
                .Select(x => x.Entry.Content)
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
            _host?.Log(PluginLogLevel.Info, "All vector memories cleared");
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

    // Embedding API

    private async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct)
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            model = EmbeddingModel,
            input = text,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, EmbeddingUrl);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var response = await _httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _host?.Log(PluginLogLevel.Error, $"Embedding API error {response.StatusCode}: {responseBody}");
            throw new HttpRequestException(
                $"OpenAI Embedding API returned {(int)response.StatusCode}: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var embeddingArray = doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding");

        var embedding = new float[embeddingArray.GetArrayLength()];
        var i = 0;
        foreach (var element in embeddingArray.EnumerateArray())
        {
            embedding[i++] = element.GetSingle();
        }

        return embedding;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator == 0 ? 0 : dot / denominator;
    }

    // Persistence

    private async Task<List<VectorMemoryEntry>> LoadEntriesAsync(CancellationToken ct)
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
                _entries = JsonSerializer.Deserialize<List<VectorMemoryEntry>>(json, JsonOptions) ?? [];
            }
            catch (Exception ex)
            {
                _host?.Log(PluginLogLevel.Warning, $"Failed to load vector memories: {ex.Message}");
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

    private void EnsureConfigured()
    {
        if (string.IsNullOrEmpty(_apiKey))
            throw new InvalidOperationException("OpenAI API key not configured. Set it in plugin settings.");
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();
        _lock.Dispose();
    }

    private sealed record VectorMemoryEntry(string Content, float[] Embedding, DateTime CreatedAt);
}
