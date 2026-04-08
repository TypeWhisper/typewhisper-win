using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Fetches community term packs from the remote registry.
/// </summary>
public sealed class TermPackRegistryService
{
    private const string RegistryUrl = "https://typewhisper.github.io/typewhisper-win/termpacks.json";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private List<TermPack>? _cachedPacks;
    private DateTime _lastFetch = DateTime.MinValue;

    /// <summary>
    /// Fetches remote term packs. Returns cached result if within cache window.
    /// Falls back to empty list on network errors.
    /// </summary>
    public async Task<IReadOnlyList<TermPack>> GetRemotePacksAsync(CancellationToken ct = default)
    {
        if (_cachedPacks is not null && DateTime.UtcNow - _lastFetch < CacheDuration)
            return _cachedPacks;

        try
        {
            var response = await _http.GetFromJsonAsync<RegistryResponse>(RegistryUrl, ct);
            if (response?.Packs is not { Count: > 0 })
                return _cachedPacks ?? [];

            _cachedPacks = response.Packs
                .Where(p => !string.IsNullOrEmpty(p.Id) && p.Terms is { Count: > 0 })
                .Select(p => new TermPack(p.Id, p.Name ?? p.Id, p.Icon ?? "", p.Terms.ToArray()))
                .ToList();
            _lastFetch = DateTime.UtcNow;
            return _cachedPacks;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TermPackRegistry fetch failed: {ex.Message}");
            return _cachedPacks ?? [];
        }
    }

    private sealed record RegistryResponse
    {
        [JsonPropertyName("packs")]
        public List<RemoteTermPack>? Packs { get; init; }
    }

    private sealed record RemoteTermPack
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = "";

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("icon")]
        public string? Icon { get; init; }

        [JsonPropertyName("terms")]
        public List<string> Terms { get; init; } = [];
    }
}
