using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Shared;

// Fireworks part of the connection; the shared core is linked from plugins/shared/ProviderConnection.cs.
internal sealed partial class ProviderConnection
{
    internal const string CatalogValue = "catalog";
    internal const string CatalogFetchedAtValue = "catalogFetchedAtUtc";

    private static partial string NormalizeLanguage(string language) => language;
    private static partial IEnumerable<string> ParseTerms(string? prompt) => PluginDictionaryTerms.ParsePrompt(prompt);

    // The catalog was fetched with the previous key; a new key may see a different set of models.
    partial void OnKeyReplacing(ref Configuration next, string? key)
    {
        if (string.Equals(key, Key, StringComparison.Ordinal)) return;
        var values = new Dictionary<string, string>(next.Values);
        values.Remove(CatalogValue);
        values.Remove(CatalogFetchedAtValue);
        next = next with { Values = values };
    }

    /// <summary>When the saved catalog was fetched; null without a catalog, for one saved before 1.2.0, or for a damaged value.</summary>
    internal DateTimeOffset? CatalogFetchedAt =>
        Get(CatalogValue).Length > 0
        && DateTimeOffset.TryParseExact(Get(CatalogFetchedAtValue), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fetchedAt)
            ? fetchedAt : null;

    /// <summary>
    /// Saves the catalog and its fetch date in one write. Saves nothing and returns false when the plugin was deactivated
    /// or the key changed since the fetch, so a late result cannot replace the current catalog.
    /// </summary>
    internal async Task<bool> TrySaveCatalogAsync(string catalog, DateTimeOffset fetchedAt, string expectedKey, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            // One read, before the checks: Deactivate resets the configuration without the gate, and a save built from the
            // reset copy would drop the key reference. A copy read before the reset belongs to the checked host and key.
            var current = Volatile.Read(ref _configuration);
            if (Host is not { } host || !string.Equals(expectedKey, Key, StringComparison.Ordinal)) return false;
            var values = new Dictionary<string, string>(current.Values)
            {
                [CatalogValue] = catalog,
                [CatalogFetchedAtValue] = fetchedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            };
            var next = current with { Values = values };
            host.SetSetting("configuration", next); _configuration = next; host.NotifyCapabilitiesChanged();
            return true;
        }
        finally { _gate.Release(); }
    }

    internal async Task<PluginTranscriptionResult> MultipartAsync(string url, string model, byte[] audio, string? language,
        string? prompt, CancellationToken ct, string? key = null, string fileField = "file", string? responseFormat = null)
    {
        using var request = Request(HttpMethod.Post, url, key);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio); file.Headers.ContentType = new("audio/wav"); form.Add(file, fileField, "audio.wav");
        form.Add(new StringContent(model), "model");
        if (responseFormat is not null) form.Add(new StringContent(responseFormat), "response_format");
        if (Language(language) is { } lang) form.Add(new StringContent(lang), "language");
        if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");
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
            ["max_tokens"] = model is "accounts/fireworks/models/gpt-oss-120b" or "accounts/fireworks/models/gpt-oss-20b"
                ? LlmOutputTokenBudget.CalculateWithReasoningReserve(system, input)
                : LlmOutputTokenBudget.Calculate(system, input)
        };
        if (Get("temperatureMode", "providerDefault") == "custom")
        {
            if (!double.TryParse(Get("temperature", "0.3"), NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature)
                || !double.IsFinite(temperature) || temperature is < 0 or > 2)
                throw new PluginRequestException("Invalid custom temperature.", PluginRequestFailureKind.Configuration);
            body["temperature"] = temperature;
        }
        using var request = Request(HttpMethod.Post, url); request.Content = Json(body);
        using var document = await ReadAsync(request, ct); var root = document.RootElement;
        LlmResponseTruncationGuard.ThrowIfOpenAiChatCompletionTruncated(root, "Provider");
        var choices = Required(root, "choices", JsonValueKind.Array);
        if (choices.GetArrayLength() == 0 || Text(choices[0], "finish_reason") != "stop") throw InvalidResponse();
        return RequiredText(Required(choices[0], "message", JsonValueKind.Object), "content").Trim();
    }
}
