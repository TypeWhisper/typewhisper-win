using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.Cli;

/// <summary>
/// Represents cli connection options data.
/// </summary>
/// <param name="ApplicationDataRoot">Application data root supplied to the member.</param>
/// <param name="PortOverride">Port override supplied to the member.</param>
/// <param name="ApiTokenOverride">Api token override supplied to the member.</param>
/// <param name="EnvironmentApiToken">Environment api token supplied to the member.</param>
public sealed record CliConnectionOptions(
    string? ApplicationDataRoot = null,
    int? PortOverride = null,
    string? ApiTokenOverride = null,
    string? EnvironmentApiToken = null);

/// <summary>
/// Represents cli connection data.
/// </summary>
/// <param name="Port">Port supplied to the member.</param>
/// <param name="ApiToken">Api token supplied to the member.</param>
public sealed record CliConnection(int Port, string? ApiToken);

/// <summary>
/// Represents cli transcribe request data.
/// </summary>
/// <param name="FilePath">File path supplied to the member.</param>
/// <param name="Language">Language supplied to the member.</param>
/// <param name="LanguageHints">Language hints supplied to the member.</param>
/// <param name="Task">Task supplied to the member.</param>
/// <param name="TargetLanguage">Target language supplied to the member.</param>
/// <param name="Engine">Engine supplied to the member.</param>
/// <param name="Model">Model supplied to the member.</param>
/// <param name="AwaitDownload">Await download supplied to the member.</param>
public sealed record CliTranscribeRequest(
    string FilePath,
    string? Language,
    IReadOnlyList<string> LanguageHints,
    string Task,
    string? TargetLanguage,
    string? Engine,
    string? Model,
    bool AwaitDownload);

/// <summary>
/// Provides cli connection resolver behavior.
/// </summary>
public static class CliConnectionResolver
{
    private const int DefaultPort = 8978;

    /// <summary>
    /// Resolves the supplied input to a configured value.
    /// </summary>
    public static CliConnection Resolve(CliConnectionOptions options)
    {
        var appDirectory = Path.Join(
            options.ApplicationDataRoot
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TypeWhisper");

        var discovery = ReadDiscovery(Path.Join(appDirectory, "api-discovery.json"));
        var port = ValidatePort(options.PortOverride)
            ?? ValidatePort(discovery?.Port)
            ?? ValidatePort(ReadLegacyPort(Path.Join(appDirectory, "api-port")))
            ?? DefaultPort;
        var token = FirstNonBlank(
            options.ApiTokenOverride,
            options.EnvironmentApiToken,
            discovery?.Token);

        return new CliConnection(port, token);
    }

    /// <summary>
    /// Returns whether port in range.
    /// </summary>
    public static bool IsPortInRange(int port) => port is >= 1 and <= 65535;

    private static ApiDiscovery? ReadDiscovery(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var discovery = JsonSerializer.Deserialize<ApiDiscovery>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return discovery;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
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
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return null;
        }

        return null;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static int? ValidatePort(int? port) =>
        port is int value && IsPortInRange(value) ? value : null;

    private sealed record ApiDiscovery
    {
        /// <summary>
        /// Gets or sets the version value.
        /// </summary>
        public int Version { get; init; }
        /// <summary>
        /// Gets or sets the port value.
        /// </summary>
        public int Port { get; init; }
        /// <summary>
        /// Gets or sets the token value.
        /// </summary>
        public string? Token { get; init; }
    }
}

/// <summary>
/// Provides cli request builder behavior.
/// </summary>
public static class CliRequestBuilder
{
    /// <summary>
    /// Builds get.
    /// </summary>
    public static HttpRequestMessage BuildGet(string baseUrl, string path, string? apiToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(baseUrl, path));
        ApplyApiToken(request, apiToken);
        return request;
    }

    /// <summary>
    /// Builds transcribe local file.
    /// </summary>
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

    /// <summary>
    /// Applies api token.
    /// </summary>
    public static void ApplyApiToken(HttpRequestMessage request, string? apiToken)
    {
        if (!string.IsNullOrWhiteSpace(apiToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
    }

    /// <summary>
    /// Builds stdin file name.
    /// </summary>
    public static string BuildStdinFileName(ReadOnlySpan<byte> audioBytes)
    {
        if (audioBytes.Length >= 12
            && audioBytes[..4].SequenceEqual("RIFF"u8)
            && audioBytes[8..12].SequenceEqual("WAVE"u8))
        {
            return "stdin.wav";
        }

        if (audioBytes.StartsWith("fLaC"u8))
            return "stdin.flac";

        if (audioBytes.StartsWith("OggS"u8))
            return "stdin.ogg";

        if (LooksLikeAdtsAac(audioBytes))
            return "stdin.aac";

        if (audioBytes.StartsWith("ID3"u8) || LooksLikeMp3Frame(audioBytes))
        {
            return "stdin.mp3";
        }

        return "stdin.wav";
    }

    private static bool LooksLikeAdtsAac(ReadOnlySpan<byte> audioBytes) =>
        audioBytes.Length >= 2
        && audioBytes[0] == 0xFF
        && (audioBytes[1] & 0xF0) == 0xF0
        && (audioBytes[1] & 0x06) == 0x00;

    private static bool LooksLikeMp3Frame(ReadOnlySpan<byte> audioBytes)
    {
        if (audioBytes.Length < 4 || audioBytes[0] != 0xFF || (audioBytes[1] & 0xE0) != 0xE0)
            return false;

        var version = (audioBytes[1] >> 3) & 0b11;
        var layer = (audioBytes[1] >> 1) & 0b11;

        return version != 0b01 && layer == 0b01;
    }

    private static Uri BuildUri(string baseUrl, string path) =>
        new(new Uri(baseUrl.TrimEnd('/')), path);
}
