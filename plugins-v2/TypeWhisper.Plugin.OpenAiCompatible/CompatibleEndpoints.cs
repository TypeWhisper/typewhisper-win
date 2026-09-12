using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugin.OpenAiCompatible;

public sealed partial class OpenAiCompatiblePlugin
{
    internal static Uri RequestUri(OpenAiCompatibleProfile profile, string path)
    {
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var server) || server.Scheme is not ("http" or "https"))
            throw new PluginRequestException("Configure an HTTP or HTTPS server URL.", PluginRequestFailureKind.Configuration);
        return new Uri(profile.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/') +
            (string.IsNullOrWhiteSpace(profile.ApiVersion) ? "" : "?api-version=" + Uri.EscapeDataString(profile.ApiVersion.Trim())));
    }

    internal static Uri RealtimeUri(OpenAiCompatibleProfile profile)
    {
        var uri = new UriBuilder(RequestUri(profile, "/v1/realtime"));
        uri.Scheme = uri.Scheme == "https" ? "wss" : "ws";
        uri.Query = uri.Query.TrimStart('?') + (uri.Query.Length > 0 ? "&" : "") + "intent=transcription";
        return uri.Uri;
    }

    internal static bool UsesRealtime(OpenAiCompatibleProfile profile) => profile.TranscriptionTransport == "realtime" ||
        profile.TranscriptionTransport == "auto" && profile.SelectedModelId?.Trim().ToLowerInvariant() is "gpt-live-transcribe" or "gpt-realtime-whisper";

    internal static bool IsDatedApiVersion(string version) => Regex.IsMatch(version.Trim(), @"^\d{4}-\d{2}-\d{2}");

    internal static Uri BatchUri(OpenAiCompatibleProfile profile)
    {
        if (profile.BatchEndpoint == "deployment-scoped" && !IsDatedApiVersion(profile.ApiVersion))
            throw new PluginRequestException("Deployment-scoped batch transcription requires a dated API version.", PluginRequestFailureKind.Configuration);
        return RequestUri(profile, profile.BatchEndpoint == "deployment-scoped"
            ? "/deployments/" + Uri.EscapeDataString(profile.SelectedModelId ?? "") + "/audio/transcriptions"
            : "/v1/audio/transcriptions");
    }

    internal static IReadOnlyDictionary<string, string> AuthenticationHeaders(Uri uri, string? key)
    {
        var headers = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(key)) return headers;
        key = key.Trim();
        headers["Authorization"] = "Bearer " + key;
        if (new[] { ".openai.azure.com", ".openai.azure.us", ".services.ai.azure.com" }
            .Any(suffix => uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))) headers["api-key"] = key;
        return headers;
    }

    internal static void Authenticate(HttpRequestMessage request, string? key)
    {
        foreach (var header in AuthenticationHeaders(request.RequestUri!, key)) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    internal static string ParseResponsesText(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        LlmResponseTruncationGuard.ThrowIfResponsesApiIncomplete(root, "The OpenAI-compatible provider");
        if (root.TryGetProperty("status", out var status) && status.GetString() is "failed" or "cancelled")
            throw new PluginRequestException("The provider did not complete the response.", PluginRequestFailureKind.OutputIncomplete);
        if (root.TryGetProperty("output_text", out var direct) && direct.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(direct.GetString()))
            return direct.GetString()!.Trim();
        var parts = new List<string>();
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var type) && type.GetString() is not ("output_text" or "text")) continue;
                    if (!part.TryGetProperty("text", out var text)) continue;
                    if (text.ValueKind == JsonValueKind.String) parts.Add(text.GetString()!);
                    else if (text.ValueKind == JsonValueKind.Object && text.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                        parts.Add(value.GetString()!);
                }
            }
        var result = string.Concat(parts).Trim();
        if (result.Length > 0) return result;
        throw new PluginRequestException("The Responses API returned no answer text.", PluginRequestFailureKind.EmptyResponse);
    }
}
