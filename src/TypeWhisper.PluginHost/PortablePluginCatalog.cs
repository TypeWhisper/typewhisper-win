using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TypeWhisper.PluginHost;

public sealed record PortableCatalogEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string MinHostVersion { get; init; } = "1.1.0";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "transcription";
    public required string DownloadUrl { get; init; }
    public required string Sha256 { get; init; }
    public long Size { get; init; }
    public string[] Platforms { get; init; } = ["windows"];
    public string[] SupportedArchitectures { get; init; } = [];

    public void Validate()
    {
        ValidateId(Id);
        if (string.IsNullOrWhiteSpace(Name) || !System.Version.TryParse(Version, out _) ||
            !System.Version.TryParse(MinHostVersion, out _) || !Regex.IsMatch(Sha256 ?? "", "^[a-fA-F0-9]{64}$") ||
            Size <= 0 || Size > PortablePluginStore.MaximumPackageBytes ||
            !Uri.TryCreate(DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidDataException("The catalog contains invalid package metadata for " + Id + ".");
    }

    public bool Supports(Version host, string architecture) => System.Version.Parse(MinHostVersion) <= host &&
        Platforms.Contains("windows", StringComparer.OrdinalIgnoreCase) &&
        SupportedArchitectures.Contains(architecture, StringComparer.OrdinalIgnoreCase);

    internal static void ValidateId(string id)
    {
        if (!Regex.IsMatch(id ?? "", "^[a-z0-9]+(?:[.-][a-z0-9]+)+$"))
            throw new InvalidDataException("Invalid plugin identity.");
    }
}

public sealed class PortablePluginCatalog(HttpClient http, Uri? feed = null)
{
    public const string FeedUrl = "https://typewhisper.github.io/typewhisper-win/plugins-v2.json";
    public Uri Feed { get; } = feed ?? new(FeedUrl);
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public async Task<IReadOnlyList<PortableCatalogEntry>> FetchAsync(CancellationToken ct = default)
    {
        if (Feed.Scheme != "https") throw new InvalidDataException("The plugin feed must use HTTPS.");
        using var response = await http.GetAsync(Feed, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new InvalidDataException("The plugin feed redirected outside HTTPS.");
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var entries = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("plugins");
        var result = entries.Deserialize<PortableCatalogEntry[]>(Json) ?? throw new InvalidDataException("Empty plugin catalog.");
        foreach (var entry in result) entry.Validate();
        if (result.GroupBy(e => e.Id).Any(g => g.Count() > 1)) throw new InvalidDataException("Duplicate plugin identities in catalog.");
        return result;
    }
    public static string Architecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
}
