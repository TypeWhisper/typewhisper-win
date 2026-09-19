using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Cohere;

// Plugin-local connection and persistence support. No runtime dependency on another provider.
internal sealed class ProviderConnection(HttpClient http) : IDisposable
{
    internal sealed record Configuration(string? SecretName, Dictionary<string, string> Values);
    private Configuration _configuration = new(null, []);
    private readonly SemaphoreSlim _gate = new(1, 1);
    internal IPluginHostServices? Host { get; private set; }
    internal string? Key { get; private set; }
    internal HttpClient Http => http;
    internal bool Configured => Host is not null && Key is not null;
    internal string Get(string id, string fallback = "") => _configuration.Values.GetValueOrDefault(id, fallback);
    internal string L(string en, string de)
    {
        try { return Host?.Localization.CurrentLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase) == true ? de : en; }
        catch (NotSupportedException) { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? de : en; }
    }

    internal async Task ActivateAsync(IPluginHostServices host)
    {
        var saved = host.GetSetting<Configuration>("configuration") ?? new(null, []);
        var key = saved.SecretName is null ? null : NormalizeKey(await host.LoadSecretAsync(saved.SecretName));
        _configuration = saved with { Values = saved.Values ?? [] }; Key = key; Host = host;
    }
    internal void Deactivate() { Host = null; Key = null; _configuration = new(null, []); }
    internal async Task SetKeyAsync(string value)
    {
        var key = NormalizeKey(value);
        await _gate.WaitAsync();
        try
        {
            var host = Host ?? throw new InvalidOperationException("Activate the plugin first.");
            var previous = _configuration;
            var staged = key is null ? null : "api-key-" + Guid.NewGuid().ToString("N");
            try
            {
                if (staged is not null) await host.StoreSecretAsync(staged, key!);
                var next = previous with { SecretName = staged };
                host.SetSetting("configuration", next);
                _configuration = next; Key = key;
            }
            catch
            {
                if (staged is not null) await CleanupAsync(host, staged);
                throw;
            }
            if (previous.SecretName is not null) await CleanupAsync(host, previous.SecretName);
            host.NotifyCapabilitiesChanged();
        }
        finally { _gate.Release(); }
    }
    private static async Task CleanupAsync(IPluginHostServices host, string key)
    {
        try { await host.DeleteSecretAsync(key); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { host.Log(PluginLogLevel.Warning, "An inactive encrypted provider key could not be removed."); }
    }
    internal async Task SaveAsync(string id, string value, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var host = Host ?? throw new InvalidOperationException("Activate the plugin first."); ct.ThrowIfCancellationRequested();
            var values = new Dictionary<string, string>(_configuration.Values) { [id] = value };
            var next = _configuration with { Values = values };
            host.SetSetting("configuration", next); _configuration = next; host.NotifyCapabilitiesChanged();
        }
        finally { _gate.Release(); }
    }
    internal string RequireKey() => Configured ? Key! : throw new PluginRequestException("API key not configured.", PluginRequestFailureKind.Configuration);
    internal static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var value = key.Trim();
        if (value.Length > 4096 || value.Any(char.IsControl)) throw new ArgumentException("Invalid API key format.");
        return value;
    }
    internal HttpRequestMessage Request(HttpMethod method, string url, string? key = null, string header = "Authorization")
    {
        var request = new HttpRequestMessage(method, url);
        var token = key ?? RequireKey();
        if (header == "Authorization") request.Headers.Authorization = new("Bearer", token);
        else request.Headers.Add(header, token);
        return request;
    }
    internal static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    internal async Task<JsonDocument> ReadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(http, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct); ct.ThrowIfCancellationRequested();
        try { var document = JsonDocument.Parse(json); if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw InvalidResponse(); } return document; }
        catch (JsonException) { throw InvalidResponse(); }
    }
    internal static PluginRequestException InvalidResponse() => new("The provider returned an incomplete or invalid response.", PluginRequestFailureKind.OutputIncomplete);
    internal static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    internal static JsonElement Required(JsonElement element, string name, JsonValueKind kind)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var result) || result.ValueKind != kind) throw InvalidResponse();
        return result;
    }
    internal static string RequiredText(JsonElement element, string name) => !string.IsNullOrWhiteSpace(Text(element, name)) ? Text(element, name)! : throw InvalidResponse();
    internal static double Number(JsonElement element, string name, double fallback = 0) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0 ? number : fallback;
    internal static string? Language(string? language) => string.IsNullOrWhiteSpace(language) || language.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : language.Trim();
    internal static string[] Terms(string? prompt) => PluginDictionaryTerms.Clip(
        prompt?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [], new(MaxTerms: 100, MaxTotalChars: 4000)).ToArray();
    internal static void Audio(byte[] audio, bool translate, bool supportsTranslation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (translate && !supportsTranslation) throw new NotSupportedException("This provider does not support translation.");
        if (audio.Length == 0) throw new ArgumentException("Audio is empty.", nameof(audio));
    }
    internal async Task<PluginTranscriptionResult> MultipartAsync(string url, string model, byte[] audio, string? language,
        string? prompt, CancellationToken ct, string? key = null, string fileField = "file", string? responseFormat = null)
    {
        using var request = Request(HttpMethod.Post, url, key);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(model), "model");
        if (responseFormat is not null) form.Add(new StringContent(responseFormat), "response_format");
        if (Language(language) is { } lang) form.Add(new StringContent(lang), "language");
        if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");
        // Cohere requires every scalar field before the file part.
        var file = new ByteArrayContent(audio); file.Headers.ContentType = new("audio/wav"); form.Add(file, fileField, "audio.wav");
        request.Content = form;
        using var document = await ReadAsync(request, ct); var root = document.RootElement;
        var text = Text(root, "text") ?? throw InvalidResponse();
        var segments = new List<PluginTranscriptionSegment>(); float? noSpeech = null;
        if (root.TryGetProperty("segments", out var rawSegments) && rawSegments.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in rawSegments.EnumerateArray())
            {
                var start = Number(segment, "start"); var end = Number(segment, "end");
                if (end >= start && Text(segment, "text") is { } segmentText) segments.Add(new(segmentText, start, end));
                var probability = Number(segment, "no_speech_prob", -1);
                if (probability is >= 0 and <= 1) noSpeech = noSpeech is null ? (float)probability : Math.Min(noSpeech.Value, (float)probability);
            }
        }
        return new(text.Trim(), Text(root, "language") ?? Language(language), Number(root, "duration"), noSpeech) { Segments = segments };
    }
    internal async Task<string> ChatAsync(string url, string model, string system, string input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(model) || model.Length > 256 || model.Any(char.IsControl)) throw new ArgumentException("Invalid model ID.");
        var body = new Dictionary<string, object>
        {
            ["model"] = model, ["messages"] = new[] { new { role = "system", content = system }, new { role = "user", content = input } },
            ["max_tokens"] = Math.Min(8192, LlmOutputTokenBudget.CalculateWithReasoningReserve(system, input))
        };
        if (Get("temperatureMode", "providerDefault") == "custom") body["temperature"] = double.Parse(Get("temperature", "0.3"), CultureInfo.InvariantCulture);
        using var request = Request(HttpMethod.Post, url); request.Content = Json(body);
        using var document = await ReadAsync(request, ct); var root = document.RootElement;
        LlmResponseTruncationGuard.ThrowIfOpenAiChatCompletionTruncated(root, "Provider");
        var choices = Required(root, "choices", JsonValueKind.Array);
        if (choices.GetArrayLength() == 0 || Text(choices[0], "finish_reason") != "stop") throw InvalidResponse();
        return RequiredText(Required(choices[0], "message", JsonValueKind.Object), "content").Trim();
    }
    public void Dispose() { http.Dispose(); _gate.Dispose(); }
}
