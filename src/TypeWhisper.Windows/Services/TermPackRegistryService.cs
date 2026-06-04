using System.Diagnostics;
using System.Globalization;
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
    private const string RegistryUrl = "https://typewhisper.github.io/typewhisper-termpacks/termpacks.json";
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

            var builtInIds = TermPack.AllPacks
                .Select(pack => pack.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _cachedPacks = response.Packs
                .Where(p => !string.IsNullOrWhiteSpace(p.Id)
                    && !builtInIds.Contains(p.Id)
                    && seenIds.Add(p.Id)
                    && p.Terms is { Count: > 0 })
                .Select(p => new TermPack(
                    p.Id,
                    p.LocalizedName(),
                    p.Icon ?? "",
                    p.Terms.ToArray(),
                    p.RequiresCommercialLicense))
                .OrderBy(pack => pack.Name, StringComparer.CurrentCultureIgnoreCase)
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
        /// <summary>
        /// Gets or sets the packs value.
        /// </summary>
        [JsonPropertyName("packs")]
        public List<RemoteTermPack>? Packs { get; init; }
    }

    private sealed record RemoteTermPack
    {
        /// <summary>
        /// Gets or sets the id value.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; init; } = "";

        /// <summary>
        /// Gets or sets the name value.
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        /// <summary>
        /// Gets or sets the names value.
        /// </summary>
        [JsonPropertyName("names")]
        public Dictionary<string, string>? Names { get; init; }

        /// <summary>
        /// Gets or sets the icon value.
        /// </summary>
        [JsonPropertyName("icon")]
        public string? Icon { get; init; }

        /// <summary>
        /// Gets or sets the requires commercial license value.
        /// </summary>
        [JsonPropertyName("requiresCommercialLicense")]
        public bool RequiresCommercialLicense { get; init; }

        /// <summary>
        /// Gets or sets the terms value.
        /// </summary>
        [JsonPropertyName("terms")]
        public List<string> Terms { get; init; } = [];

        /// <summary>
        /// Gets the localized name.
        /// </summary>
        public string LocalizedName()
        {
            var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (Names?.TryGetValue(language, out var localizedName) == true
                && !string.IsNullOrWhiteSpace(localizedName))
            {
                return localizedName;
            }

            return string.IsNullOrWhiteSpace(Name) ? Id : Name;
        }
    }
}
