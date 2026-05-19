using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.Cli;

public sealed record CliConnectionOptions(
    string? ApplicationDataRoot = null,
    int? PortOverride = null,
    string? ApiTokenOverride = null,
    string? EnvironmentApiToken = null);

public sealed record CliConnection(int Port, string? ApiToken);

public sealed record CliTranscribeRequest(
    string FilePath,
    string? Language,
    IReadOnlyList<string> LanguageHints,
    string Task,
    string? TargetLanguage,
    string? Engine,
    string? Model,
    bool AwaitDownload);

public static class CliConnectionResolver
{
    private const int DefaultPort = 8978;

    public static CliConnection Resolve(CliConnectionOptions options)
    {
        var appDirectory = Path.Combine(
            options.ApplicationDataRoot
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TypeWhisper");

        var discovery = ReadDiscovery(Path.Combine(appDirectory, "api-discovery.json"));
        var port = options.PortOverride
            ?? discovery?.Port
            ?? ReadLegacyPort(Path.Combine(appDirectory, "api-port"))
            ?? DefaultPort;
        var token = FirstNonBlank(
            options.ApiTokenOverride,
            options.EnvironmentApiToken,
            discovery?.Token);

        return new CliConnection(port, token);
    }

    private static ApiDiscovery? ReadDiscovery(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var discovery = JsonSerializer.Deserialize<ApiDiscovery>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return discovery?.Port > 0 ? discovery : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadLegacyPort(string path)
    {
        try
        {
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var port))
                return port;
        }
        catch { }

        return null;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private sealed record ApiDiscovery
    {
        public int Version { get; init; }
        public int Port { get; init; }
        public string? Token { get; init; }
    }
}

public static class CliRequestBuilder
{
    public static HttpRequestMessage BuildGet(string baseUrl, string path, string? apiToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(baseUrl, path));
        ApplyApiToken(request, apiToken);
        return request;
    }

    public static HttpRequestMessage BuildTranscribeLocalFile(
        string baseUrl,
        CliTranscribeRequest request,
        string? apiToken)
    {
        var path = request.AwaitDownload
            ? "/v1/transcribe/local-file?await_download=1"
            : "/v1/transcribe/local-file";
        var message = new HttpRequestMessage(HttpMethod.Post, BuildUri(baseUrl, path));
        ApplyApiToken(message, apiToken);

        var body = new Dictionary<string, object?>
        {
            ["path"] = request.FilePath,
            ["language"] = request.Language,
            ["language_hints"] = request.LanguageHints,
            ["task"] = request.Task,
            ["target_language"] = request.TargetLanguage,
            ["engine"] = request.Engine,
            ["model"] = request.Model
        }
        .Where(pair => pair.Value is not null)
        .ToDictionary(pair => pair.Key, pair => pair.Value);

        var json = JsonSerializer.Serialize(body);
        message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return message;
    }

    public static void ApplyApiToken(HttpRequestMessage request, string? apiToken)
    {
        if (!string.IsNullOrWhiteSpace(apiToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
    }

    private static Uri BuildUri(string baseUrl, string path) =>
        new(new Uri(baseUrl.TrimEnd('/')), path);
}
